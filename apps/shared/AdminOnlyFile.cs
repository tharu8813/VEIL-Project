using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Veil.Shared;

/// <summary>Accepts a configuration file only when it and its directory can be changed solely by
/// SYSTEM, Administrators or TrustedInstaller (ADR-0003 registered-volume configuration).</summary>
internal static class AdminOnlyFile
{
    private static readonly string[] Trusted =
    [
        "S-1-5-18",
        "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    ];

    private const FileSystemRights Changes = FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    internal static string ReadAllText(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.Directory!.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("Configuration path must not be a link.");
        Require(file.GetAccessControl(), "configuration file");
        Require(file.Directory.GetAccessControl(), "configuration directory");
        return File.ReadAllText(file.FullName);
    }

    // Separate from configuration loading. The caller must retain a read
    // handle denying write/delete and separately verify its final path.
    internal static void RequireProtectedExecutable(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("Executable must not be a link.");
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Executable ancestor must not be a link.");
        Require(file.GetAccessControl(), "executable");
        Require(file.Directory!.GetAccessControl(), "executable directory");
    }

    private static void Require(FileSystemSecurity security, string what)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner is null || !Trusted.Contains(owner))
            throw new UnauthorizedAccessException($"The {what} must be owned by an administrator account.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & Changes) != 0 &&
                !Trusted.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException($"The {what} is writable by a non-administrator account.");
        }
    }
}
