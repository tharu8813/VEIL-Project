// SPDX-License-Identifier: MPL-2.0
using System.IO;
using Microsoft.Win32.SafeHandles;
using Veil.Shared;

namespace Veil.VolumeBroker;

/// <summary>
/// Independently pin the registered executable before creating a process,
/// then verify the exact live process identity. Neither operation grants
/// permission to execute or redeem a candidate.
/// </summary>
internal static class CefPinnedImage
{
    private static void RequireRegisteredPath(string registeredPath)
    {
        if (string.IsNullOrWhiteSpace(registeredPath) ||
            !Path.IsPathFullyQualified(registeredPath) ||
            !string.Equals(registeredPath, Path.GetFullPath(registeredPath), StringComparison.OrdinalIgnoreCase) ||
            registeredPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(registeredPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF executable path did not match registration.");
    }

    internal static FileStream OpenVerifiedBeforeCreation(string registeredPath, CefImageDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        RequireRegisteredPath(registeredPath);
        AdminOnlyFile.RequireProtectedExecutable(registeredPath);
        var image = new FileStream(registeredPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            RequireRetainedBeforeCreation(image, registeredPath, digest);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static void RequireRetainedBeforeCreation(FileStream image, string registeredPath, CefImageDigest digest)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(digest);
        RequireRegisteredPath(registeredPath);
        AdminOnlyFile.RequireProtectedExecutable(registeredPath);
        image.Position = 0;
        Native.RequireCanonicalSingleLinkImage(image.SafeFileHandle, registeredPath);
        if (!digest.Matches(image) || !Native.IsSigned(registeredPath))
            throw new UnauthorizedAccessException("CEF executable digest or signature did not match.");
        AdminOnlyFile.RequireProtectedExecutable(registeredPath);
        Native.RequireCanonicalSingleLinkImage(image.SafeFileHandle, registeredPath);
        image.Position = 0;
    }

    internal static FileStream OpenVerified(ClientIdentity child, SafeProcessHandle process,
        string registeredPath, CefImageDigest digest)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(process);
        RequireRegisteredPath(registeredPath);
        if (!string.Equals(child.ImagePath, registeredPath, StringComparison.OrdinalIgnoreCase) ||
            !Native.MatchesLiveProcessImage(process, child))
            throw new UnauthorizedAccessException("CEF executable identity did not match registration.");

        var image = OpenVerifiedBeforeCreation(registeredPath, digest);
        try
        {
            if (!Native.MatchesLiveProcessImage(process, child))
                throw new UnauthorizedAccessException("CEF executable process changed during verification.");
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
