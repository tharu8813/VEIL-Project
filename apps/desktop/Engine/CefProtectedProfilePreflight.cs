// SPDX-License-Identifier: MPL-2.0
using System.IO;

namespace Veil.Desktop;

/// <summary>
/// Read-only preflight for a future protected CEF launcher. Not wired into
/// the development bootstrap: that process initializes CEF before managed code.
/// Does not create directories, redirect TEMP, or authorize product startup.
/// </summary>
internal static class CefProtectedProfilePreflight
{
    internal static CefProfileLayout Inspect(string productRoot, IVolumeBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ProfileGate.RejectElevated();
        var layout = new CefProfileLayout(productRoot);
        var initial = ProfileGate.Verify(layout.ProductRoot, broker, requireProtected: true);

        // Verify existing directories, even if another component created them.
        // Never infer protection from a drive letter or path prefix.
        foreach (string path in layout.Directories)
        {
            var attributes = ExistingAttributes(path);
            if (attributes is null) continue;
            if (!attributes.Value.HasFlag(FileAttributes.Directory) ||
                attributes.Value.HasFlag(FileAttributes.ReparsePoint))
                throw new ProfileLocationException("The CEF profile directory is obstructed or redirected.");
            var location = ProfileGate.Verify(path, broker, requireProtected: true);
            RequireSameVolume(initial, location);
        }
        if (ExistingAttributes(layout.EngineRoot) is not null)
            ProfileGate.RejectRedirectedChildren(layout.EngineRoot);

        var log = ExistingAttributes(layout.LogFile);
        if (log is not null && (log.Value.HasFlag(FileAttributes.Directory) ||
            log.Value.HasFlag(FileAttributes.ReparsePoint)))
            throw new ProfileLocationException("The CEF log path is obstructed or redirected.");

        // Refuse a changed broker state before returning even a planning result.
        RequireSameVolume(initial, ProfileGate.Verify(layout.ProductRoot, broker, requireProtected: true));
        return layout;
    }

    private static void RequireSameVolume(ProfileLocation expected, ProfileLocation actual)
    {
        if (!expected.Protected || !actual.Protected ||
            !string.Equals(expected.VolumeGuid, actual.VolumeGuid, StringComparison.OrdinalIgnoreCase))
            throw new ProfileLocationException("The registered protected volume changed.");
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        // Only explicit absence is accepted. Access denied and I/O errors
        // must propagate rather than be mistaken for a new directory.
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
