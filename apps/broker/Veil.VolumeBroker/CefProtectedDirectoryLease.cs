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
