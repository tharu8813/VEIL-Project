using System.IO;

namespace Veil.Desktop;

/// <summary>
/// SEC-004: keeps temporary files of this process and the engine processes it launches inside the
/// profile directory, i.e. on the protected volume, instead of the user's %TEMP% on the system disk.
/// The folder is emptied at start and on exit. It is a mitigation only; Procmon traces in the
/// isolated VM decide whether any write still leaves the volume.
/// </summary>
internal static class ProfileScratch
{
    internal const string FolderName = "temp";

    /// <summary>Creates an empty <c>temp</c> folder in <paramref name="profileRoot"/> and points TEMP/TMP
    /// at it. Must run before the WebView2 environment is created so child processes inherit it.</summary>
    internal static string Redirect(string profileRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot));
        string scratch = Path.Combine(root, FolderName);
        Clear(scratch);
        var created = Directory.CreateDirectory(scratch);
        if (created.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ProfileLocationException("The profile temp folder is redirected.");
        Environment.SetEnvironmentVariable("TEMP", scratch);
        Environment.SetEnvironmentVariable("TMP", scratch);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), scratch, StringComparison.OrdinalIgnoreCase))
            throw new ProfileLocationException("The process temp path could not be moved into the profile.");
        return scratch;
    }

    /// <summary>Best-effort removal of leftovers; never follows a junction out of the profile.</summary>
    internal static void Clear(string scratch)
    {
        var info = new DirectoryInfo(scratch);
        if (!info.Exists) return;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ProfileLocationException("The profile temp folder is redirected.");
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo directory && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    directory.Delete(recursive: true);
                else
                    entry.Delete(); // a reparse point is removed itself, its target is untouched
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Still in use by an exiting engine process; the next start retries.
            }
        }
    }
}
