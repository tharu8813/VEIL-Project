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

    // Transfers ownership only after all observations succeed. The caller
    // must either install this in its serialized pending slot or dispose it.
    internal static CefPendingChild Stage(ClientIdentity owner, SafeProcessHandle ownerHandle,
        long nominatedHandle, string registeredPath, string sha256, object generation)
    {
        var binding = Native.DuplicateNominatedChild(owner, ownerHandle, new IntPtr(nominatedHandle));
        FileStream? image = null;
        try
        {
            image = CefPinnedImage.OpenVerified(binding.Identity, binding.Handle,
                registeredPath, new CefImageDigest(sha256));
            if (Native.ObserveProcess(ownerHandle) != owner ||
                Native.ObserveProcess(binding.Handle) != binding.Identity)
                throw new UnauthorizedAccessException("CEF staging identity changed.");
            return new CefPendingChild(new CefChildAdmission(owner, binding.Identity,
                generation, TimeProvider.System), binding.Handle, image);
        }
        catch
        {
            try { image?.Dispose(); }
            finally { binding.Handle.Dispose(); }
            throw;
        }
    }
}

