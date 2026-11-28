// SPDX-License-Identifier: MPL-2.0
using Microsoft.Win32.SafeHandles;
using Veil.Desktop;

namespace Veil.VolumeBroker;

// Serialized, disconnected pre-creation preparation. Owns every prerequisite
// until disposed; no creation, resume, raw token export or admission here.
// The owner process handle is borrowed from the authenticated generation.
internal sealed class CefBrokerLaunchPreparation : IDisposable
{
    private readonly string path;
    private readonly CefImageDigest digest;
    private readonly Func<bool> authorityCurrent;
    private readonly CefRuntimeRegistrationLease registration;
    private readonly FileStream image;
    private readonly CefProcessTokenObserver.LaunchTokenLease token;
    private readonly CefSystemModuleVerifier verifier;
    private bool disposed;

    private CefBrokerLaunchPreparation(string path, CefImageDigest digest,
        Func<bool> authorityCurrent, CefRuntimeRegistrationLease registration,
        FileStream image, CefProcessTokenObserver.LaunchTokenLease token,
        CefSystemModuleVerifier verifier)
    {
        this.path = path;
        this.digest = digest;
        this.authorityCurrent = authorityCurrent;
        this.registration = registration;
        this.image = image;
        this.token = token;
        this.verifier = verifier;
    }

    internal static CefBrokerLaunchPreparation Open(string registeredPath, string sha256,
        ClientIdentity owner, SafeProcessHandle ownerHandle, Func<bool> authorityCurrent,
        ReadOnlySpan<byte> baselinePayload, ReadOnlySpan<byte> baselineSignature)
    {
        ArgumentNullException.ThrowIfNull(authorityCurrent);
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("CEF preparation generation is unavailable.");
        var digest = new CefImageDigest(sha256);
        CefRuntimeRegistrationLease? registration = null;
        FileStream? image = null;
        CefProcessTokenObserver.LaunchTokenLease? token = null;
        try
        {
            registration = CefRuntimeRegistrationLease.OpenInstalled(serializedBrokerAccess: true);
            if (!string.Equals(registration.ImagePath, registeredPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("CEF preparation runtime does not match broker pin.");
            image = CefPinnedImage.OpenVerifiedBeforeCreation(registeredPath, digest);
            var verifier = CefSignedSystemModuleBaseline.VerifyInstalledTrust(baselinePayload, baselineSignature);
            verifier.RequireBoundTrust();
            if (!authorityCurrent())
                throw new UnauthorizedAccessException("CEF preparation generation retired.");
            token = CefProcessTokenObserver.LaunchTokenLease.Acquire(ownerHandle, owner);
            var prepared = new CefBrokerLaunchPreparation(registeredPath, digest, authorityCurrent,
                registration, image, token, verifier);
            prepared.RequireCurrent();
            return prepared;
        }
        catch
        {
            // No process exists at this stage; all partially acquired
            // prerequisite handles can be released immediately.
            try { token?.Dispose(); }
            finally
            {
                try { image?.Dispose(); }
                finally { registration?.Dispose(); }
            }
            throw;
        }
    }

    internal void RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("CEF preparation generation retired.");
        registration.RequireCurrent();
        if (!string.Equals(registration.ImagePath, path, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF preparation runtime path changed.");
        CefPinnedImage.RequireRetainedBeforeCreation(image, path, digest);
        verifier.RequireBoundTrust();
        token.RequireCurrent();
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("CEF preparation generation retired.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { token.Dispose(); }
        finally
        {
            try { image.Dispose(); }
            finally { registration.Dispose(); }
        }
    }
}
