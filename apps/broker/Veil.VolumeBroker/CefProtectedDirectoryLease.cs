// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

// Read-only, disconnected single-directory pin. Neither a path supplied
// by a client nor a backend status alone authorizes protected startup.
// A future coordinator must derive the expected path from independently
// registered authority, hold the complete ancestor/profile lifetime and
// verify the authenticated generation. No filesystem mutation here.
internal sealed class CefProtectedDirectoryLease : IDisposable
{
    private readonly string path;
    private readonly string volume;
    private readonly SafeFileHandle handle;
    private bool disposed;

    private CefProtectedDirectoryLease(string path, string volume, SafeFileHandle handle)
    {
        this.path = path;
        this.volume = volume;
        this.handle = handle;
    }

    internal static void RequireRegisteredProtection(string registeredVolume, BackendStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!IsVolumeGuid(registeredVolume) || !status.Attached || !status.Unlocked ||
            !status.FullyEncrypted || !status.ProtectionOn || !status.ProtectorPresent ||
            !string.Equals(status.VolumeGuid, registeredVolume, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF registered volume protection unavailable.");
    }

    private static bool IsVolumeGuid(string? value) =>
        value is { Length: 49 } && value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) &&
        value.EndsWith(@"}\", StringComparison.Ordinal) && Guid.TryParseExact(value[10..^1], "B", out var guid) &&
        guid != Guid.Empty;

    private static void RequireCanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.Ordinal) ||
            string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF directory path is not canonical and local.");
    }

    internal static CefProtectedDirectoryLease Open(string registeredPath, string registeredVolume,
        Func<BackendStatus> readStatus)
    {
        ArgumentNullException.ThrowIfNull(readStatus);
        RequireCanonicalPath(registeredPath);
        RequireRegisteredProtection(registeredVolume, readStatus());
        var handle = OpenDirectory(registeredPath);
        try
        {
            RequireDirectory(handle, registeredPath, registeredVolume);
            RequireRegisteredProtection(registeredVolume, readStatus());
            return new CefProtectedDirectoryLease(registeredPath, registeredVolume, handle);
        }
        catch { handle.Dispose(); throw; }
    }

    internal void RequireCurrent(Func<BackendStatus> readStatus)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(readStatus);
        RequireRegisteredProtection(volume, readStatus());
        RequireDirectory(handle, path, volume);
        using var current = OpenDirectory(path);
        RequireDirectory(current, path, volume);
        if (!CompareObjectHandles(handle, current))
        {
            // Separately opened file handles can refer to different file
            // objects for the same on-disk directory. Use volume/file ID.
            var original = Identity(handle);
            var observed = Identity(current);
            if (original.VolumeSerial != observed.VolumeSerial ||
                original.IndexHigh != observed.IndexHigh || original.IndexLow != observed.IndexLow)
                throw new UnauthorizedAccessException("CEF directory object changed.");
        }
        RequireRegisteredProtection(volume, readStatus());
    }

    // Actual requested access, under the synchronous user token scope.
    // No create, write or delete operations. Compare to the retained pin.
    internal void RequireUserAccess(bool writable)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var observed = OpenAccess(path, writable ? 0xA7u : 0xA0u);
        RequireDirectory(observed, path, volume);
        RequireSameIdentity(handle, observed);
    }

    // Check add-file and delete-child directory rights under the real user
    // token. This is an OPEN_EXISTING access probe, not a rename permit.
    internal void RequireLibraryDirectoryAccess()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var observed = OpenAccess(path, 0xE3); // list/add file/traverse/delete child/read attributes
        RequireDirectory(observed, path, volume);
        RequireSameIdentity(handle, observed);
    }

    private static SafeFileHandle OpenAccess(string path, uint rights)
    {
        var result = CreateFileW(path, rights, FileShare.ReadWrite, IntPtr.Zero,
            FileMode.Open, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (result.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            result.Dispose();
            throw new Win32Exception(error, "CEF user filesystem access refused.");
        }
        return result;
    }

    private static void RequireSameIdentity(SafeFileHandle retained, SafeFileHandle observed)
    {
        var original = Identity(retained);
        var current = Identity(observed);
        if (original.VolumeSerial != current.VolumeSerial ||
            original.IndexHigh != current.IndexHigh || original.IndexLow != current.IndexLow)
            throw new UnauthorizedAccessException("CEF user filesystem object changed.");
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        // FILE_READ_ATTRIBUTES, OPEN_EXISTING, BACKUP_SEMANTICS and
        // OPEN_REPARSE_POINT. Deny delete sharing while the pin is held.
        var result = CreateFileW(path, 0x80, FileShare.ReadWrite, IntPtr.Zero,
            FileMode.Open, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (result.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            result.Dispose();
            throw new Win32Exception(error, "CEF directory open refused.");
        }
        return result;
    }

    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception();
        return info;
    }

    private static void RequireDirectory(SafeFileHandle handle, string path, string volume)
    {
        var info = Identity(handle);
        if ((info.Attributes & (uint)FileAttributes.Directory) == 0 ||
            (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("CEF directory is redirected or not a directory.");
        string dos = FinalPath(handle, 0); // VOLUME_NAME_DOS
        if (!string.Equals(dos, @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF directory resolves elsewhere.");
        string guid = FinalPath(handle, 1); // VOLUME_NAME_GUID
        string relative = path[Path.GetPathRoot(path)!.Length..];
        if (!string.Equals(guid, volume + relative, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF directory is outside the registered volume.");
    }

    // File opens use the same no-follow, metadata-only, no-delete-sharing
    // flags as directory opens. A log may be written by its future owner,
    // but cannot be replaced through ordinary delete/rename while pinned.
    internal static RegularFilePin OpenRegularFile(string path, string volume, Func<BackendStatus> readStatus)
    {
        RequireCanonicalPath(path);
        RequireRegisteredProtection(volume, readStatus());
        var handle = OpenDirectory(path);
        try
        {
            RequireRegularFile(handle, path, volume);
            RequireRegisteredProtection(volume, readStatus());
            return new RegularFilePin(path, volume, handle);
        }
        catch { handle.Dispose(); throw; }
    }

    private static void RequireRegularFile(SafeFileHandle handle, string path, string volume)
    {
        var info = Identity(handle);
        if ((info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0 ||
            info.Links != 1)
            throw new UnauthorizedAccessException("CEF file is redirected, linked or not regular.");
        string relative = path[Path.GetPathRoot(path)!.Length..];
        if (!string.Equals(FinalPath(handle, 0), @"\\?\" + path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(FinalPath(handle, 1), volume + relative, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF file resolves outside its expected location.");
    }

    internal sealed class RegularFilePin(string path, string volume, SafeFileHandle handle) : IDisposable
    {
        internal void RequireCurrent(Func<BackendStatus> readStatus)
        {
            RequireRegisteredProtection(volume, readStatus());
            RequireRegularFile(handle, path, volume);
            using var current = OpenDirectory(path);
            RequireRegularFile(current, path, volume);
            var original = Identity(handle);
            var observed = Identity(current);
            if (original.VolumeSerial != observed.VolumeSerial ||
                original.IndexHigh != observed.IndexHigh || original.IndexLow != observed.IndexLow)
                throw new UnauthorizedAccessException("CEF regular file identity changed.");
            RequireRegisteredProtection(volume, readStatus());
        }

        internal void RequireUserAccess()
        {
            // READ_DATA | WRITE_DATA | APPEND_DATA | READ_ATTRIBUTES
            using var observed = OpenAccess(path, 0x87);
            RequireRegularFile(observed, path, volume);
            RequireSameIdentity(handle, observed);
        }

        // Committed data requires read access; staging additionally requires
        // write and append. DELETE itself is intentionally not probed while
        // the no-delete-sharing identity pin is retained.
        internal void RequireLibraryDataAccess(bool staged)
        {
            using var observed = OpenAccess(path, staged ? 0x87u : 0x81u);
            RequireRegularFile(observed, path, volume);
            RequireSameIdentity(handle, observed);
        }

        public void Dispose() => handle.Dispose();
    }

#if VEIL_CEF_PROFILE_ACCESS_TEST
    // Synthetic Windows handle experiment only. Not compiled in the broker.
    // This is a first-publication primitive, never a replace-existing API.
    // Retain a data-access directory handle and a DELETE-capable source
    // handle; deny concurrent write/delete opens. Rename by source handle
    // with a simple name in its own directory. No path-based rename fallback.
    internal static bool TryPublishNewLibraryFixture(string engineRoot, string volume,
        Func<BackendStatus> readStatus, Func<bool> current)
    {
        if (!Environment.Is64BitProcess || !current()) return false;
        using var directory = Open(engineRoot, volume, readStatus);
        using var directoryAccess = OpenAccess(engineRoot, 0xA1); // list/traverse/read attributes
        RequireDirectory(directoryAccess, engineRoot, volume);
        RequireSameIdentity(directory.handle, directoryAccess);
        string stagedPath = Path.Combine(engineRoot, "library-state.stage");
        // DELETE | SYNCHRONIZE | READ_ATTRIBUTES | READ_DATA.
        using var source = CreateFileW(stagedPath, 0x00110081, FileShare.Read, IntPtr.Zero,
            FileMode.Open, 0x00200000, IntPtr.Zero);
        if (source.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        RequireRegularFile(source, stagedPath, volume);
        directory.RequireCurrent(readStatus);
        RequireSameIdentity(directory.handle, directoryAccess);
        RequireRegisteredProtection(volume, readStatus());
        if (!current()) return false;
        RequireRegularFile(source, stagedPath, volume);

        // Native FILE_RENAME_INFORMATION x64: ReplaceIfExists=false,
        // RootDirectory=NULL (same-directory simple name), length at 16,
        // UTF-16 name at 20. No overwrite, stream name, or caller input.
        byte[] name = Encoding.Unicode.GetBytes("library-state.json");
        byte[] buffer = new byte[24 + name.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), name.Length);
        name.CopyTo(buffer, 20);
        int status = NtSetInformationFile(source, out _, buffer, (uint)buffer.Length, 10);
        if (status == 0) return true; // irreversible native success
        if (unchecked((uint)status) == 0xC0000035u) return false; // STATUS_OBJECT_NAME_COLLISION
        throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)), "Synthetic CEF rename refused.");
    }

    // Disposable generation experiment. The directory and minimum are fixture
    // inputs, NOT a product registration or authenticated rollback anchor.
    // Unlike legacy overwrite, a competing final name is never replaced.
    internal static bool ProbeLibraryGenerationFixture(string generationRoot, string volume,
        Func<BackendStatus> readStatus, Func<bool> current, int minimumCommitted)
    {
        if (!Environment.Is64BitProcess || !current()) return false;
        using var directory = Open(generationRoot, volume, readStatus);
        var snapshot = CefLibraryGenerationCatalog.Inspect(
            Directory.EnumerateFileSystemEntries(generationRoot).Select(path => Path.GetFileName(path)!),
            minimumCommitted, allowEmpty: minimumCommitted == 0);
        if (snapshot.Pending is not int generation) return false;
        string stagedPath = Path.Combine(generationRoot, CefLibraryGenerationCatalog.Name(generation, staged: true));
        using var source = CreateFileW(stagedPath, 0x00110081, FileShare.Read, IntPtr.Zero,
            FileMode.Open, 0x00200000, IntPtr.Zero);
        if (source.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        RequireRegularFile(source, stagedPath, volume);
        directory.RequireCurrent(readStatus);
        RequireRegisteredProtection(volume, readStatus());
        if (!current()) return false;
        RequireRegularFile(source, stagedPath, volume);
        byte[] name = Encoding.Unicode.GetBytes(CefLibraryGenerationCatalog.Name(generation));
        byte[] buffer = new byte[24 + name.Length]; // ReplaceIfExists remains zero
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), name.Length);
        name.CopyTo(buffer, 20);
        int status = NtSetInformationFile(source, out _, buffer, (uint)buffer.Length, 10);
        if (status == 0) return true;
        if (unchecked((uint)status) == 0xC0000035u) return false;
        throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)), "Synthetic generation publication refused.");
    }

    // Disposable fixture only: compare legacy replacement with a retained
    // destination pin and after its release. The second case intentionally
    // has an identity-to-rename gap, and MUST NOT become a product writer.
    internal static bool ProbeLibraryReplacementFixture(string engineRoot, string volume,
        Func<BackendStatus> readStatus, Func<bool> current, bool retainDestination)
    {
        if (!Environment.Is64BitProcess || !current()) return false;
        using var directory = Open(engineRoot, volume, readStatus);
        using var directoryAccess = OpenAccess(engineRoot, 0xA1);
        RequireDirectory(directoryAccess, engineRoot, volume);
        RequireSameIdentity(directory.handle, directoryAccess);
        string stagedPath = Path.Combine(engineRoot, "library-state.stage");
        string destinationPath = Path.Combine(engineRoot, "library-state.json");
        using var source = CreateFileW(stagedPath, 0x00110081, FileShare.Read, IntPtr.Zero,
            FileMode.Open, 0x00200000, IntPtr.Zero);
        if (source.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        RequireRegularFile(source, stagedPath, volume);
        // Actual data-access handle: unlike a metadata-only handle, it
        // denies delete sharing for the destination while retained.
        using var target = OpenAccess(destinationPath, 0x81);
        RequireRegularFile(target, destinationPath, volume);
        directory.RequireCurrent(readStatus);
        RequireSameIdentity(directory.handle, directoryAccess);
        RequireRegisteredProtection(volume, readStatus());
        if (!current()) return false;
        RequireRegularFile(source, stagedPath, volume);
        RequireRegularFile(target, destinationPath, volume);

        // ONLY the controlled fixture may release this pin before rename.
        // No production fallback, retry, or POSIX sharing override.
        if (!retainDestination) target.Dispose();
        byte[] name = Encoding.Unicode.GetBytes("library-state.json");
        byte[] buffer = new byte[24 + name.Length];
        buffer[0] = 1; // legacy FILE_RENAME_INFORMATION ReplaceIfExists
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), name.Length);
        name.CopyTo(buffer, 20);
        int status = NtSetInformationFile(source, out _, buffer, (uint)buffer.Length, 10);
        if (status == 0) return true;
        int error = unchecked((int)RtlNtStatusToDosError(status));
        if (error is 5 or 32 or 33) return false; // access/sharing/lock violation
        throw new Win32Exception(error, "Synthetic CEF legacy replacement refused.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock status,
        byte[] information, uint length, int informationClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
#endif

    // Acquire the same existing profile.lock used by the desktop. This
    // is a broker-owned exclusive open, not proof of another process's
    // lock and not a claim that all filesystem writers honor the lock.
    // Never create, truncate, write, or take over a busy lock file.
    internal static ExclusiveProfileLock OpenExclusiveProfileLock(string productRoot,
        string volume, Func<BackendStatus> readStatus)
    {
        ArgumentNullException.ThrowIfNull(readStatus);
        RequireCanonicalPath(productRoot);
        string path = Path.Combine(productRoot, "profile.lock");
        RequireRegisteredProtection(volume, readStatus());
        // GENERIC_READ | GENERIC_WRITE, no sharing, OPEN_EXISTING,
        // OPEN_REPARSE_POINT. No handle inheritance or backup access.
        var handle = CreateFileW(path, 0xC0000000, FileShare.None, IntPtr.Zero,
            FileMode.Open, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "CEF exclusive profile lock unavailable.");
        }
        try
        {
            RequireRegularFile(handle, path, volume);
            RequireRegisteredProtection(volume, readStatus());
            return new ExclusiveProfileLock(path, volume, handle);
        }
        catch { handle.Dispose(); throw; }
    }

    internal sealed class ExclusiveProfileLock : IDisposable
    {
        private readonly string path;
        private readonly string volume;
        private readonly SafeFileHandle handle;

        internal ExclusiveProfileLock(string path, string volume, SafeFileHandle handle)
        {
            this.path = path;
            this.volume = volume;
            this.handle = handle;
        }

        // Keep the original exclusive handle; never release and reopen it
        // to demonstrate exclusivity. The profile owns the ancestor pins.
        internal void RequireCurrent(Func<BackendStatus> readStatus)
        {
            if (handle.IsClosed || handle.IsInvalid)
                throw new UnauthorizedAccessException("CEF profile lock was lost.");
            RequireRegisteredProtection(volume, readStatus());
            RequireRegularFile(handle, path, volume);
            RequireRegisteredProtection(volume, readStatus());
        }

        public void Dispose() => handle.Dispose();
    }

    private static string FinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CEF directory resolution failed.");
        return buffer.ToString();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share,
        IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity identity);
    [DllImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(SafeFileHandle first, SafeFileHandle second);
}
