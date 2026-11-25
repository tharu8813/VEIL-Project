using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Veil.Desktop;

/// <summary>Startup refusal for an unsafe profile location (ADR-0003). Derives from IOException so
/// existing fail-closed handlers and locked-volume expectations keep working.</summary>
internal sealed class ProfileLocationException(string message) : IOException(message);

/// <summary>Protection state of a registered profile volume, as reported by the privileged broker.</summary>
internal sealed record VolumeStatus(string VolumeGuid, bool FullyEncrypted, bool ProtectionOn, bool Unlocked,
    bool ReadyToUnlock, bool ProtectorPresent);

/// <summary>Read-only view of the volume broker used before the engine starts.</summary>
internal interface IVolumeBroker
{
    /// <summary>Status of the registered volume, or null when no broker-managed volume exists.</summary>
    VolumeStatus? QueryRegisteredVolume();
}

/// <summary>Development mode: the volume is managed outside VEIL, so protection is never claimed.</summary>
internal sealed class UnmanagedDevelopmentVolume : IVolumeBroker
{
    internal static readonly UnmanagedDevelopmentVolume Instance = new();
    public VolumeStatus? QueryRegisteredVolume() => null;
}

/// <param name="Protected">True only when the broker verified the volume holding this profile.</param>
internal sealed record ProfileLocation(string FinalPath, string VolumeGuid, bool Protected);

internal static class ProfileGate
{
    internal static void RejectElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new ProfileLocationException("VEIL must run without administrator rights.");
    }

    /// <summary>Checks the nearest existing ancestor before creating anything, then verifies the created directory,
    /// so a junction or redirected parent never receives a new profile.</summary>
    internal static ProfileLocation Prepare(string directory, IVolumeBroker broker, bool requireProtected)
    {
        string requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(requested))
        {
            var existing = new DirectoryInfo(requested).Parent;
            while (existing is not null && !existing.Exists) existing = existing.Parent;
            if (existing is null)
                throw new ProfileLocationException("The profile has no accessible existing parent directory.");
            // Product launches must prove the parent is on the registered, unlocked
            // protected volume before creating even an empty directory.
            Verify(existing.FullName, broker, requireProtected);
            Directory.CreateDirectory(requested);
        }
        return Verify(requested, broker, requireProtected);
    }

    /// <summary>Validates an existing profile directory. With <paramref name="requireProtected"/>, the broker
    /// must report the same volume as fully encrypted, protected and unlocked.</summary>
    internal static ProfileLocation Verify(string directory, IVolumeBroker broker, bool requireProtected)
    {
        string requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (requested.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(requested)!).DriveType is DriveType.Network or DriveType.NoRootDirectory)
            throw new ProfileLocationException("The profile must be on a local volume.");

        for (var current = new DirectoryInfo(requested); current is not null; current = current.Parent)
        {
            // Throws for a missing or inaccessible directory, including a locked volume.
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ProfileLocationException("The profile path contains a junction or symbolic link.");
        }

        string final = FinalPath(requested);
        if (!string.Equals(final, requested, StringComparison.OrdinalIgnoreCase))
            throw new ProfileLocationException("The profile path resolves to a different location.");

        string volume = VolumeGuid(final);
        var status = broker.QueryRegisteredVolume();
        bool verified = status is { FullyEncrypted: true, ProtectionOn: true, Unlocked: true, ProtectorPresent: true } &&
            string.Equals(status.VolumeGuid, volume, StringComparison.OrdinalIgnoreCase);
        if (requireProtected && !verified)
            throw new ProfileLocationException("The profile is not on the registered protected volume.");
        return new ProfileLocation(final, volume, verified);
    }

    /// <summary>Reject existing redirected files and directories before opening profile state.
    /// This is a startup check, not protection against concurrent same-user path changes.</summary>
    internal static void RejectRedirectedChildren(string directory)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        while (pending.TryPop(out var parent))
        {
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ProfileLocationException("The profile contains a redirected directory.");
            foreach (var entry in parent.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ProfileLocationException("The profile contains a redirected file or directory.");
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
    }

    private static string FinalPath(string path)
    {
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"Cannot open the profile directory.", new Win32Exception());
        var buffer = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("Cannot resolve the profile directory.", new Win32Exception());
        string final = buffer.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new ProfileLocationException("The profile must be on a local volume.");
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        return Path.TrimEndingDirectorySeparator(final);
    }

    private static string VolumeGuid(string path)
    {
        var mount = new StringBuilder(1024);
        var name = new StringBuilder(64);
        if (!GetVolumePathNameW(path, mount, (uint)mount.Capacity) ||
            !GetVolumeNameForVolumeMountPointW(mount.ToString(), name, (uint)name.Capacity))
            throw new IOException("Cannot identify the profile volume.", new Win32Exception());
        return name.ToString();
    }

    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder mount, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder name, uint length);
}
