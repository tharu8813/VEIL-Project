using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

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

    internal static (ClientIdentity Identity, SafeProcessHandle Handle) Identify(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle;
        if (!GetNamedPipeClientProcessId(handle, out uint pid) || !GetNamedPipeClientSessionId(handle, out uint session))
            throw new Win32Exception();
        var process = OpenProcess(QueryLimitedInformation | Synchronize, false, pid);
        try
        {
            if (process.IsInvalid) throw new Win32Exception();
            var observed = ObserveProcess(process);
            if (observed.ProcessId != (int)pid || observed.SessionId != (int)session ||
                !MatchesPipeClient(pipe, process, observed))
                throw new UnauthorizedAccessException("Client pipe identity mismatch.");
            return (observed, process);
        }
        catch { process.Dispose(); throw; }
    }

    internal static bool MatchesPipeClient(NamedPipeServerStream pipe, SafeProcessHandle process, ClientIdentity expected)
    {
        try
        {
            var handle = pipe.SafePipeHandle;
            if (!GetNamedPipeClientProcessId(handle, out uint pid) ||
                !GetNamedPipeClientSessionId(handle, out uint session) ||
                pid != (uint)expected.ProcessId || session != (uint)expected.SessionId ||
                ObserveProcess(process) != expected) return false;
            using var current = OpenProcess(QueryLimitedInformation | Synchronize, false, pid);
            return SameLiveProcess(process, current) && ObserveProcess(current) == expected;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal static bool SameLiveProcess(SafeProcessHandle first, SafeProcessHandle second)
    {
        try
        {
            return IsRunning(first) && IsRunning(second) &&
                CompareObjectHandles(first, second) && IsRunning(first) && IsRunning(second);
        }
        catch (ObjectDisposedException) { return false; }
    }

    [DllImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(SafeProcessHandle first, SafeProcessHandle second);

    // Compare an exited, retained child to the handle still owned by its
    // authenticated parent. Do not reopen a PID or accept a numeric PID.
    internal static bool MatchesExitedNomination(ClientIdentity owner, SafeProcessHandle ownerHandle,
        long nomination, SafeProcessHandle retained, uint expectedExit)
    {
        if (nomination <= 0 || ObserveProcess(ownerHandle) != owner) return false;
        using var source = OpenProcess(QueryLimitedInformation | Synchronize | 0x0040, false, (uint)owner.ProcessId);
        if (!SameLiveProcess(source, ownerHandle) ||
            !DuplicateHandle(source, new IntPtr(nomination), new IntPtr(-1), out var child,
                QueryLimitedInformation | Synchronize, false, 0)) return false;
        using (child)
            return CompareObjectHandles(child, retained) &&
                WaitForSingleObject(child, 0) == 0 &&
                GetExitCodeProcess(child, out uint code) && code == expectedExit &&
                SameLiveProcess(source, ownerHandle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    internal static ClientIdentity ObserveProcess(SafeProcessHandle process)
    {
        uint pid = GetProcessId(process);
        if (pid == 0 || !IsRunning(process)) throw new UnauthorizedAccessException("Process is unavailable.");
        var image = new StringBuilder(32768);
        int length = image.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, image, ref length)) throw new Win32Exception();
        if (!ProcessIdToSessionId(pid, out uint session))
            throw new Win32Exception();
        if (!OpenProcessToken(process, 0x8 /* TOKEN_QUERY */, out var token)) throw new Win32Exception();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (!GetProcessTimes(process, out long created, out _, out _, out _) ||
                created <= 0 || !IsRunning(process))
                throw new UnauthorizedAccessException("Client process identity is unavailable.");
            return new ClientIdentity((int)pid, (int)session, identity.User!.Value, image.ToString(), created);
        }
    }

    // Disconnected future launch-binding primitive. The source handle value
    // must come from an authenticated owner's explicit launch nomination,
    // never from the child or an environment variable. No authority is granted.
    internal static (SafeProcessHandle Handle, ClientIdentity Identity) DuplicateNominatedChild(
        ClientIdentity owner, SafeProcessHandle originalOwner, IntPtr nominatedHandle)
    {
        if (nominatedHandle.ToInt64() <= 0 || ObserveProcess(originalOwner) != owner)
            throw new UnauthorizedAccessException("CEF owner or nominated handle is invalid.");

        const uint duplicateHandleAccess = 0x0040;
        using var source = OpenProcess(QueryLimitedInformation | Synchronize | duplicateHandleAccess,
            false, (uint)owner.ProcessId);
        if (source.IsInvalid || ObserveProcess(source) != owner ||
            !SameLiveProcess(originalOwner, source) ||
            !DuplicateHandle(source, nominatedHandle, new IntPtr(-1), out var child,
                QueryLimitedInformation | Synchronize, false, 0))
            throw new UnauthorizedAccessException("CEF child handle could not be duplicated.");
        try
        {
            var identity = ObserveProcess(child);
            if (identity.ProcessId == owner.ProcessId ||
                identity.CreatedFileTime < owner.CreatedFileTime ||
                identity.SessionId != owner.SessionId ||
                !string.Equals(identity.UserSid, owner.UserSid, StringComparison.OrdinalIgnoreCase) ||
                !HasReportedParent(identity.ProcessId, owner.ProcessId) ||
                ObserveProcess(originalOwner) != owner || ObserveProcess(child) != identity)
                throw new UnauthorizedAccessException("CEF child nomination did not match live process identities.");
            return (child, identity);
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    // Toolhelp parent PID is only corroborating metadata (parent selection can
    // be overridden at creation); it is NOT proof of the original creator.
    // Independent broker-side snapshot of the live process. Holding the
    // original process object avoids treating a reused PID as this child.
    // Toolhelp is a loader inventory, not an exhaustive memory attestation.
    internal static string[] SnapshotModulePaths(SafeProcessHandle process, ClientIdentity expected)
    {
        if (ObserveProcess(process) != expected)
            throw new UnauthorizedAccessException("CEF module process identity changed.");
        using var snapshot = CreateToolhelp32Snapshot(0x00000008 | 0x00000010, (uint)expected.ProcessId);
        if (snapshot.IsInvalid) throw new Win32Exception();
        var entry = new ModuleEntry { Size = (uint)Marshal.SizeOf<ModuleEntry>(), ModuleName = "", ImagePath = "" };
        if (!Module32FirstW(snapshot, ref entry)) throw new Win32Exception();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            if (entry.ProcessId != (uint)expected.ProcessId || entry.Module == IntPtr.Zero ||
                entry.ImagePath.Length is 0 or >= 259 || !paths.Add(entry.ImagePath) || paths.Count > 1024)
                throw new UnauthorizedAccessException("Invalid CEF module snapshot.");
            entry.Size = (uint)Marshal.SizeOf<ModuleEntry>();
        } while (Module32NextW(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18 || ObserveProcess(process) != expected ||
            !paths.Contains(expected.ImagePath))
            throw new UnauthorizedAccessException("Incomplete CEF module snapshot.");
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry
    {
        public uint Size, ModuleId, ProcessId, GlobalUsage, ProcessUsage;
        public IntPtr BaseAddress;
        public uint BaseSize;
        public IntPtr Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ModuleName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ImagePath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32FirstW(SafeFileHandle snapshot, ref ModuleEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32NextW(SafeFileHandle snapshot, ref ModuleEntry entry);

    private static bool HasReportedParent(int child, int parent)
    {
        using var snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot.IsInvalid) return false;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ImageName = string.Empty };
        if (!Process32FirstW(snapshot, ref entry)) return false;
        do
        {
            if (entry.ProcessId == (uint)child) return entry.ParentProcessId == (uint)parent;
        } while (Process32NextW(snapshot, ref entry));
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ImageName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(SafeProcessHandle sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out SafeProcessHandle targetHandle, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    // Match the process generation observed on the pipe, not merely a PID
    // that Windows could have reused while this request waited for serial.
    internal static SafeProcessHandle OpenForWait(ClientIdentity client, SafeProcessHandle observed)
    {
        // Duplicate the actual pipe-observed handle, not a reopened PID.
        using var self = OpenProcess(0x0040 /* PROCESS_DUP_HANDLE */, false, (uint)Environment.ProcessId);
        if (self.IsInvalid || !IsRunning(observed) ||
            !DuplicateHandle(self, observed.DangerousGetHandle(), new IntPtr(-1), out var process,
                QueryLimitedInformation | Synchronize, false, 0))
            return new SafeProcessHandle(IntPtr.Zero, ownsHandle: true);
        try
        {
            if (SameLiveProcess(observed, process) && ObserveProcess(process) == client)
                return process;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        process.Dispose();
        return new SafeProcessHandle(IntPtr.Zero, ownsHandle: true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created,
        out long exited, out long kernel, out long user);

    // The crash watcher may dispose the handle concurrently after process exit.
    // A closed, signaled, invalid or failed wait never attests a live owner.
    // Only used after the nominated child has passed the production pin.
    // Open a termination-capable handle and bind it to the retained object,
    // never terminate based on a PID or executable name alone.
    internal static SafeProcessHandle OpenCefTerminationHandle(ClientIdentity child, SafeProcessHandle observed)
    {
        var handle = OpenProcess(QueryLimitedInformation | Synchronize | 0x0001, false, (uint)child.ProcessId);
        try
        {
            if (!SameLiveProcess(observed, handle) || ObserveProcess(handle) != child)
                throw new UnauthorizedAccessException("CEF termination handle did not match the nominated child.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static bool TerminateCefChildAndWait(SafeProcessHandle child)
    {
        if (child.IsClosed || child.IsInvalid) return false;
        uint state = WaitForSingleObject(child, 0);
        if (state == 0) return true;
        if (state != 0x102) return false;
        // 35 is forced termination, never the verified-disabled exit 29.
        if (!TerminateProcess(child, 35) && WaitForSingleObject(child, 0) != 0)
            return false;
        return WaitForSingleObject(child, 2000) == 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    internal static bool IsRunning(SafeProcessHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid) return false;
        try { return WaitForSingleObject(handle, 0) == 0x102 /* WAIT_TIMEOUT */; }
        catch (ObjectDisposedException) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    internal static void AttachRegisteredImage(string path, string userSid)
    {
        // Keep the backing file admin-only while the mounted device grants access to the registered user.
        const uint vhdx = 3, attachReadWrite = 0x00020000, permanentLifetime = 4;
        var storage = new VirtualStorageType { DeviceId = vhdx, VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B") };
        uint result = OpenVirtualDisk(ref storage, path, attachReadWrite, 0, IntPtr.Zero, out var disk);
        using (disk)
        {
            if (result != 0) throw new Win32Exception((int)result);
            string sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{new SecurityIdentifier(userSid).Value})";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, IntPtr.Zero))
                throw new Win32Exception();
            try
            {
                var parameters = new AttachVirtualDiskParameters { Version = 1 };
                result = AttachVirtualDisk(disk, descriptor, permanentLifetime, 0, ref parameters, IntPtr.Zero);
                if (result != 0) throw new Win32Exception((int)result);
            }
            finally { LocalFree(descriptor); }
        }
    }

    // The process handle must be the original observed live child handle.
    // Verify its generation and image name; this is not a measurement of
    // loaded code, mapped DLLs or an actual parent/child relationship.
    internal static bool MatchesLiveProcessImage(SafeProcessHandle process, ClientIdentity expected)
    {
        if (!IsRunning(process) || GetProcessId(process) != (uint)expected.ProcessId ||
            !GetProcessTimes(process, out long created, out _, out _, out _) ||
            created != expected.CreatedFileTime) return false;
        var image = new StringBuilder(32768);
        int length = image.Capacity;
        return QueryFullProcessImageNameW(process, 0, image, ref length) &&
            string.Equals(image.ToString(), expected.ImagePath, StringComparison.OrdinalIgnoreCase) &&
            IsRunning(process);
    }

    internal static void RequireCanonicalSingleLinkImage(SafeFileHandle file, string path)
    {
        var resolved = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(file, resolved, (uint)resolved.Capacity, 0);
        if (length == 0 || length >= resolved.Capacity ||
            !string.Equals(resolved.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase) ||
            !GetFileInformationByHandle(file, out var info) ||
            (info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0 ||
            info.Links != 1)
            throw new UnauthorizedAccessException("CEF executable is not a canonical single-link file.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ImageFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ImageFileInformation info);

    internal static bool MatchesSha256(string path, string expected)
    {
        try
        {
            using var image = File.OpenRead(path);
            return string.Equals(Convert.ToHexString(SHA256.HashData(image)), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType { public uint DeviceId; public Guid VendorId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttachVirtualDiskParameters { public uint Version; public uint Reserved; }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(ref VirtualStorageType storage, string path, uint access,
        uint flags, IntPtr parameters, out SafeFileHandle disk);

    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(SafeFileHandle disk, IntPtr securityDescriptor, uint flags,
        uint providerFlags, ref AttachVirtualDiskParameters parameters, IntPtr overlapped);

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
