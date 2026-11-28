// SPDX-License-Identifier: MPL-2.0
using Microsoft.Win32.SafeHandles;
using Veil.Desktop;

namespace Veil.VolumeBroker;

// Serialized, disconnected preparation and single-root suspended creation.
// No resume, raw token export or admission here. The caller must retain
// its custody and both private Jobs until confirmed drain or transfer.
// The owner process handle is borrowed from the authenticated generation.
internal sealed class CefBrokerLaunchPreparation : IDisposable
{
    private readonly string path;
    private readonly ClientIdentity owner;
    private readonly SafeProcessHandle ownerHandle;
    private readonly CefImageDigest digest;
    private readonly Func<bool> authorityCurrent;
    private readonly CefRuntimeRegistrationLease registration;
    private readonly FileStream image;
    private readonly CefProcessTokenObserver.LaunchTokenLease token;
    private readonly CefRegisteredProfileLease profile;
    private readonly CefSystemModuleVerifier verifier;
    private bool disposed;
    private bool creationAttempted;

    private CefBrokerLaunchPreparation(string path, CefImageDigest digest,
        Func<bool> authorityCurrent, CefRuntimeRegistrationLease registration,
        FileStream image, CefProcessTokenObserver.LaunchTokenLease token,
        CefSystemModuleVerifier verifier, ClientIdentity owner, SafeProcessHandle ownerHandle,
        CefRegisteredProfileLease profile)
    {
        this.path = path;
        this.owner = owner;
        this.ownerHandle = ownerHandle;
        this.digest = digest;
        this.authorityCurrent = authorityCurrent;
        this.registration = registration;
        this.image = image;
        this.token = token;
        this.profile = profile;
        this.verifier = verifier;
    }

    internal static CefBrokerLaunchPreparation Open(string registeredPath, string sha256,
        ClientIdentity owner, SafeProcessHandle ownerHandle, Func<bool> authorityCurrent,
        ReadOnlySpan<byte> baselinePayload, ReadOnlySpan<byte> baselineSignature,
        string volumeId, string volumeGuid, string pipeName, Func<BackendStatus> readStatus)
    {
        ArgumentNullException.ThrowIfNull(authorityCurrent);
        ArgumentNullException.ThrowIfNull(readStatus);
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("CEF preparation generation is unavailable.");
        var digest = new CefImageDigest(sha256);
        CefRuntimeRegistrationLease? registration = null;
        FileStream? image = null;
        CefProcessTokenObserver.LaunchTokenLease? token = null;
        CefRegisteredProfileLease? profile = null;
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
            // Acquire the independent installed profile and exclusive lock
            // before registering a native creation lifetime. The same
            // authority callback remains valid while that slot is occupied.
            profile = CefRegisteredProfileLease.OpenInstalled(volumeId, volumeGuid, pipeName,
                readStatus, authorityCurrent);
            var prepared = new CefBrokerLaunchPreparation(registeredPath, digest, authorityCurrent,
                registration, image, token, verifier, owner, ownerHandle, profile);
            prepared.RequireCurrent();
            return prepared;
        }
        catch
        {
            // No process exists at this stage; all partially acquired
            // prerequisite handles can be released immediately.
            try { profile?.Dispose(); }
            finally
            {
                try { token?.Dispose(); }
                finally
                {
                    try { image?.Dispose(); }
                    finally { registration?.Dispose(); }
                }
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
        profile.RequireCurrent();
        token.RequireCurrent();
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("CEF preparation generation retired.");
    }

    // Only the installed profile's explicit, token-verified environment.
    // No inherited variables or system-only fallback. The disabled
    // candidate must not be resumed. The slot already owns all resources.
    internal void CreateDiagnosticCandidate(CefBrokerOwnedJob outer, CefBrokerOwnedJob inner,
        CefContainedProcess.CreationCustody custody)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(custody);
        if (creationAttempted)
            throw new UnauthorizedAccessException("CEF preparation already consumed.");
        creationAttempted = true;
        RequireCurrent();
        var environment = profile.GetEnvironmentSnapshot(token);
        outer.CreateRetainedDiagnosticPair(inner, token, path, environment, custody);
        custody.VerifyRetainedRoot(root =>
        {
            var identity = token.RequireCreatedRoot(root, path);
            using var verified = CefPinnedImage.OpenVerified(identity, root, path, digest);
            RequireCurrent();
            // Bind both private Jobs to the original creation handle while
            // custody still owns it. A partially bound pair on refusal is
            // terminal and can only be drained by the existing owner.
            outer.BindPreparedPrivatePair(inner, owner, ownerHandle, identity, root);
            // Recheck access using the retained launch token after native
            // creation, with the original root still suspended in custody.
            _ = profile.GetEnvironmentSnapshot(token);
            RequireCurrent();
            outer.RequirePreparedPrivateRoot(inner, owner, ownerHandle, identity, root);
        });
        // Keep preparation, Jobs and custody alive. No ownership transfer,
        // activation or cleanup/relock authorization occurs here.
    }

    // Build a bound diagnostic session without transferring raw ownership.
    // The existing creation lifetime retains all pins, Jobs and handles;
    // the successor delegates drain and release to it.
    internal CefBoundDiagnosticSession PrepareBoundSession(object generation,
        CefPreparedCreationLifetime creation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(creation);
        RequireCurrent();
        return creation.PrepareSuccessor((outer, inner, child, root) =>
        {
            RequireCurrent();
            var identity = token.RequireCreatedRoot(root, path);
            using var verified = CefPinnedImage.OpenVerified(identity, root, path, digest);
            outer.RequirePreparedPrivateRoot(inner, owner, ownerHandle, identity, root);
            var session = new CefBoundDiagnosticSession(owner, ownerHandle, generation,
                registration, outer, inner, identity, root, () =>
                {
                    RequireCurrent();
                    return (generation, registration, true);
                }, verifier, child, borrowedCreation: creation);
            session.RequirePreparedForHandoff();
            RequireCurrent();
            return session;
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // The creation slot calls this only after root and all Jobs
        // are confirmed empty (or before any native creation on refusal).
        try { profile.Dispose(); }
        finally
        {
            try { token.Dispose(); }
            finally
            {
                try { image.Dispose(); }
                finally { registration.Dispose(); }
            }
        }
    }
}
