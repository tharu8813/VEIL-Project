// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

// A machine-wide cooperative writer lock. Check the actual object's owner
// and DACL even if another process created it first. Denial of service by
// object-name squatting is possible; never continue without the lock.
internal sealed class CefSystemBaselineWriterLock : IDisposable
{
    private const string Name = @"Global\VEIL.CefSystemBaselineTrust.Writer.v1";
    private const string Descriptor = "O:BAG:BAD:P(A;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)";
    private const uint ReadControlSynchronizeModify = 0x00120001;
    private readonly SafeWaitHandle handle;
    private bool disposed;

    private CefSystemBaselineWriterLock(SafeWaitHandle handle) => this.handle = handle;

    internal static void RequireDescriptor(byte[] bytes)
    {
        var expected = new RawSecurityDescriptor(Descriptor);
        var actual = new RawSecurityDescriptor(bytes, 0);
        var sections = AccessControlSections.Owner | AccessControlSections.Access;
        if (expected.GetSddlForm(sections) != actual.GetSddlForm(sections))
            throw new UnauthorizedAccessException("Unexpected baseline writer mutex owner or DACL.");
    }

    internal static CefSystemBaselineWriterLock Acquire()
    {
        var raw = new RawSecurityDescriptor(Descriptor);
        byte[] bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        SafeWaitHandle? handle = null;
        bool owned = false;
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pointer };
            handle = CreateMutexEx(ref security, Name, 0, ReadControlSynchronizeModify);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            RequireSecurity(handle);
            uint status = WaitForSingleObject(handle, 30000);
            owned = status is 0 or 0x80;
            if (status == 0x80)
                throw new InvalidOperationException("Abandoned baseline writer; inspect installed state before retry.");
            if (status != 0)
                throw new InvalidOperationException("Baseline writer lock unavailable.");
            RequireSecurity(handle);
            var result = new CefSystemBaselineWriterLock(handle);
            handle = null;
            return result;
        }
        finally
        {
            if (handle is not null)
            {
                if (owned) ReleaseMutex(handle);
                handle.Dispose();
            }
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void RequireSecurity(SafeWaitHandle handle)
    {
        const uint OwnerAndDacl = 5;
        _ = GetKernelObjectSecurity(handle, OwnerAndDacl, null, 0, out uint size);
        if (size is 0 or > 65536)
            throw new UnauthorizedAccessException("Baseline writer security unavailable.");
        byte[] data = new byte[(int)size];
        if (!GetKernelObjectSecurity(handle, OwnerAndDacl, data, size, out uint actual) ||
            actual == 0 || actual > size)
            throw new UnauthorizedAccessException("Baseline writer security unavailable.");
        RequireDescriptor(data);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!ReleaseMutex(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { handle.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateMutexExW", SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(ref SecurityAttributes attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(SafeWaitHandle handle);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeWaitHandle handle, uint information,
        [Out] byte[]? descriptor, uint size, out uint needed);
}
