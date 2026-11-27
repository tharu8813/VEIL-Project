// SPDX-License-Identifier: MPL-2.0
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

/// <summary>Owner-only observation. No retained grant, child command or launch permission.</summary>
internal static class CefChildPreflight
{
    internal static void Check(ClientIdentity owner, SafeProcessHandle ownerHandle,
        long nominatedHandle, string registeredPath, string sha256)
    {
        var binding = Native.DuplicateNominatedChild(owner, ownerHandle, new IntPtr(nominatedHandle));
        using var child = binding.Handle;
        using var image = CefPinnedImage.OpenVerified(binding.Identity, child,
            registeredPath, new CefImageDigest(sha256));
        if (Native.ObserveProcess(ownerHandle) != owner ||
            Native.ObserveProcess(child) != binding.Identity)
            throw new UnauthorizedAccessException("CEF preflight process identity changed.");
    }
}
