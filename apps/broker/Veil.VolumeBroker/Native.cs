using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

internal sealed record ClientIdentity(int ProcessId, int SessionId, string UserSid, string ImagePath);

internal static class Native
{
    private const uint PipeAccessDuplex = 0x3, FirstPipeInstance = 0x00080000, Overlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x8;
    private const uint QueryLimitedInformation = 0x1000, Synchronize = 0x00100000;

    /// <summary>Creates one pipe instance: SYSTEM and the registered user only, network logons denied,
    /// remote clients rejected by the pipe itself.</summary>
    internal static NamedPipeServerStream CreatePipe(string name, string userSid, bool first)
    {
        string sddl = $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GRGW;;;{new SecurityIdentifier(userSid).Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception();
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipeW($@"\\.\pipe\{name}", PipeAccessDuplex | Overlapped | (first ? FirstPipeInstance : 0),
                PipeRejectRemoteClients, 255, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid) throw new Win32Exception();
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    internal static ClientIdentity Identify(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle;
        if (!GetNamedPipeClientProcessId(handle, out uint pid) || !GetNamedPipeClientSessionId(handle, out uint session))
            throw new Win32Exception();
        using var process = OpenProcess(QueryLimitedInformation | Synchronize, false, pid);
        if (process.IsInvalid) throw new Win32Exception();
        var image = new StringBuilder(1024);
        int length = image.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, image, ref length)) throw new Win32Exception();
        if (!ProcessIdToSessionId(pid, out uint processSession) || processSession != session)
            throw new UnauthorizedAccessException("Client session mismatch.");
        if (!OpenProcessToken(process, 0x8 /* TOKEN_QUERY */, out var token)) throw new Win32Exception();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return new ClientIdentity((int)pid, (int)session, identity.User!.Value, image.ToString());
    }

    internal static SafeProcessHandle OpenForWait(int pid) => OpenProcess(Synchronize, false, (uint)pid);

    /// <summary>Authenticode check of a file, without network revocation retrieval (offline VM).</summary>
    internal static bool IsSigned(string path)
    {
        var file = new WinTrustFileInfo { Size = Marshal.SizeOf<WinTrustFileInfo>(), Path = path };
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData
            {
                Size = Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 0, UnionChoice = 1,
                File = filePointer, ProviderFlags = 0x10 | 0x1000, // REVOCATION_CHECK_NONE | CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo { public int Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File; public IntPtr Subject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public int Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public int UiChoice;
        public int RevocationChecks; public int UnionChoice; public IntPtr File; public int StateAction;
        public IntPtr StateData; public IntPtr UrlReference; public int ProviderFlags; public int UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision,
        out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
