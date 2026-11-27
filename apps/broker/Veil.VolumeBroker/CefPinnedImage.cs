// SPDX-License-Identifier: MPL-2.0
using System.IO;
using Microsoft.Win32.SafeHandles;
using Veil.Shared;

namespace Veil.VolumeBroker;

/// <summary>
/// Read-only, disconnected prerequisite for a future CEF child admission.
/// A successful result retains the inspected executable file handle until
/// the caller disposes it. This does not grant permission to launch or redeem.
/// </summary>
internal static class CefPinnedImage
{
    internal static FileStream OpenVerified(ClientIdentity child, SafeProcessHandle process,
        string registeredPath, CefImageDigest digest)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(digest);
        if (string.IsNullOrWhiteSpace(registeredPath) ||
            !Path.IsPathFullyQualified(registeredPath) ||
            !string.Equals(registeredPath, Path.GetFullPath(registeredPath), StringComparison.OrdinalIgnoreCase) ||
            registeredPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(registeredPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(child.ImagePath, registeredPath, StringComparison.OrdinalIgnoreCase) ||
            !Native.MatchesLiveProcessImage(process, child))
            throw new UnauthorizedAccessException("CEF executable identity did not match registration.");

        // Do not resolve or accept junctions, user-writable install directories,
        // or a file that can be replaced while its digest/signature is checked.
        AdminOnlyFile.RequireProtectedExecutable(registeredPath);
        var image = new FileStream(registeredPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Native.RequireCanonicalSingleLinkImage(image.SafeFileHandle, registeredPath);
            if (!digest.Matches(image) || !Native.IsSigned(registeredPath))
                throw new UnauthorizedAccessException("CEF executable digest or signature did not match.");
            AdminOnlyFile.RequireProtectedExecutable(registeredPath);
            Native.RequireCanonicalSingleLinkImage(image.SafeFileHandle, registeredPath);
            if (!Native.MatchesLiveProcessImage(process, child))
                throw new UnauthorizedAccessException("CEF executable process changed during verification.");
            image.Position = 0;
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
