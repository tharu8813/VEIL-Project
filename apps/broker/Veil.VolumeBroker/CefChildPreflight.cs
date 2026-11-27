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

    // Stage retains the independently verified runtime inventory until redemption.
    internal static bool VerifyRedemption(CefPendingChild pending, ClientIdentity owner,
        SafeProcessHandle ownerHandle, ClientIdentity child, string registeredPath, string sha256)
    {
        if (pending.ProcessResource is not SafeProcessHandle process ||
            pending.ImageResource is not FileStream retainedImage ||
            pending.RuntimeResource is not Veil.Desktop.CefRuntimeRegistrationLease runtime ||
            retainedImage.SafeFileHandle.IsClosed || retainedImage.SafeFileHandle.IsInvalid ||
            Native.ObserveProcess(ownerHandle) != owner || Native.ObserveProcess(process) != child)
            return false;
        Native.RequireCanonicalSingleLinkImage(retainedImage.SafeFileHandle, registeredPath);
        using var rechecked = CefPinnedImage.OpenVerified(child, process,
            registeredPath, new CefImageDigest(sha256));
        // Inspect the live loader list against the retained inventory.
        runtime.RequireCurrent();
        if (!string.Equals(runtime.ImagePath, registeredPath, StringComparison.OrdinalIgnoreCase))
            return false;
        var before = Native.SnapshotModulePaths(process, child);
        string application = Path.GetDirectoryName(registeredPath)!;
        string system = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(system)) return false;
        bool host = false, product = false;
        var applicationModules = new List<string>();
        foreach (string module in before)
        {
            if (!Path.IsPathFullyQualified(module) ||
                !string.Equals(module, Path.GetFullPath(module), StringComparison.OrdinalIgnoreCase))
                return false;
            string? directory = Path.GetDirectoryName(module);
            if (string.Equals(directory, application, StringComparison.OrdinalIgnoreCase))
            {
                applicationModules.Add(module);
                host |= string.Equals(module, registeredPath, StringComparison.OrdinalIgnoreCase);
                product |= string.Equals(Path.GetFileName(module), "veil-cef-product.dll", StringComparison.OrdinalIgnoreCase);
            }
            else if (!string.Equals(directory, system, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (!host || !product) return false;
        runtime.RequireObservedApplicationModules(applicationModules);
        runtime.RequireCurrent();
        var after = Native.SnapshotModulePaths(process, child);
        return before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase) &&
            Native.ObserveProcess(ownerHandle) == owner && Native.ObserveProcess(process) == child;
    }

    // Transfers ownership only after all observations succeed. The caller
    // must either install this in its serialized pending slot or dispose it.
    internal static CefPendingChild Stage(ClientIdentity owner, SafeProcessHandle ownerHandle,
        long nominatedHandle, string registeredPath, string sha256, object generation)
    {
        var binding = Native.DuplicateNominatedChild(owner, ownerHandle, new IntPtr(nominatedHandle));
        FileStream? image = null;
        Veil.Desktop.CefRuntimeRegistrationLease? runtime = null;
        SafeProcessHandle? termination = null;
        try
        {
            image = CefPinnedImage.OpenVerified(binding.Identity, binding.Handle,
                registeredPath, new CefImageDigest(sha256));
            // All calls are serialized by the broker semaphore. Hash before
            // creating the short-lived admission, retain the verified files.
            runtime = Veil.Desktop.CefRuntimeRegistrationLease.OpenInstalled(serializedBrokerAccess: true);
            if (!string.Equals(runtime.ImagePath, registeredPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("CEF runtime registration does not match broker pin.");
            runtime.RequireCurrent();
            if (Native.ObserveProcess(ownerHandle) != owner ||
                Native.ObserveProcess(binding.Handle) != binding.Identity)
                throw new UnauthorizedAccessException("CEF staging identity changed.");
            termination = Native.OpenCefTerminationHandle(binding.Identity, binding.Handle);
            var pending = new CefPendingChild(new CefChildAdmission(owner, binding.Identity,
                generation, TimeProvider.System), termination, image, runtime);
            binding.Handle.Dispose();
            return pending;
        }
        catch
        {
            try { runtime?.Dispose(); }
            finally
            {
                try { image?.Dispose(); }
                finally
                {
                    try { termination?.Dispose(); }
                    finally { binding.Handle.Dispose(); }
                }
            }
            throw;
        }
    }
}

