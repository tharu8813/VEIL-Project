// SPDX-License-Identifier: MPL-2.0
using Microsoft.Win32.SafeHandles;
using Veil.Desktop;

namespace Veil.VolumeBroker;

// Disconnected P2-03 observation. The caller must retain and serialize access
// to both the process object and the independently authenticated registration.
internal static class CefProcessModuleObserver
{
    internal static CefModuleInventoryObservation Observe(SafeProcessHandle process,
        ClientIdentity expected, CefRuntimeRegistrationLease registration,
        CefSystemModuleVerifier systemVerifier)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(registration);
        string image = registration.ImagePath;
        if (!string.Equals(expected.ImagePath, image, StringComparison.OrdinalIgnoreCase) ||
            Native.ObserveProcess(process) != expected)
            throw new UnauthorizedAccessException("CEF module process identity mismatch.");
        ArgumentNullException.ThrowIfNull(systemVerifier);
        systemVerifier.RequireBoundTrust();
        var result = CefModuleInventoryPolicy.Verify(image, Environment.SystemDirectory,
            Native.SnapshotModulePaths(process, expected),
            registration.RequireObservedApplicationModules,
            () => Native.SnapshotModulePaths(process, expected), systemVerifier.RequireModules);
        systemVerifier.RequireBoundTrust();
        if (result.UnverifiedSystemModules != 0)
            throw new UnauthorizedAccessException("Unverified CEF system modules refused.");
        registration.RequireCurrent();
        if (Native.ObserveProcess(process) != expected)
            throw new UnauthorizedAccessException("CEF module process changed.");
        return result;
    }
}

// Combined point-in-time diagnostics only. A matching token baseline and
// pinned disk files are NOT sufficient for a sandbox or product admission.
internal sealed record CefRetainedProcessDiagnostic(ClientIdentity Identity,
    CefObservedRole Role, CefRoleTokenDecision TokenDecision,
    CefModuleInventoryObservation Modules);

internal static class CefNestedProcessDiagnostics
{
    internal static CefRetainedProcessDiagnostic[] Observe(CefBrokerOwnedJob brokerJob,
        CefBrokerOwnedJob peerJob,
        ClientIdentity root, SafeProcessHandle rootHandle,
        CefRuntimeRegistrationLease registration,
        CefSystemModuleVerifier systemVerifier,
        Action<ClientIdentity, SafeProcessHandle, CefRetainedProcessDiagnostic>? inspectMember = null)
    {
        ArgumentNullException.ThrowIfNull(brokerJob);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHandle);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(systemVerifier);
        systemVerifier.RequireBoundTrust();
        // Only the independently retained root and trusted jobs are inputs.
        // The broker collects member handles itself, never from IPC.
        registration.RequireCurrent();
        var rootToken = CefProcessTokenObserver.ObserveRuntimeToken(rootHandle, root);
        var result = brokerJob.ObservePrivateNestedDiagnostics(peerJob, root, rootHandle,
            (identity, handle) =>
            {
                var role = CefCommandRoleObserver.Observe(handle, identity, root);
                var token = CefProcessTokenObserver.ObserveRuntimeToken(handle, identity);
                CefRuntimeTokenSnapshot.RequireLogon(rootToken, token);
                if (identity == root) CefRuntimeTokenSnapshot.RequireSame(rootToken, token);
                var modules = CefProcessModuleObserver.Observe(handle, identity,
                    registration, systemVerifier);
                if (role != CefCommandRoleObserver.Observe(handle, identity, root))
                    throw new UnauthorizedAccessException("CEF role changed during diagnostics.");
                CefRuntimeTokenSnapshot.RequireSame(token,
                    CefProcessTokenObserver.ObserveRuntimeToken(handle, identity));
                registration.RequireCurrent();
                var diagnostic = new CefRetainedProcessDiagnostic(identity, role.Role,
                    CefRoleTokenPolicy.Evaluate(identity, role.Role, token.Token), modules);
                // Callback runs before independently collected member handles
                // are released. No success escapes if later accounting fails.
                inspectMember?.Invoke(identity, handle, diagnostic);
                return diagnostic;
            });
        registration.RequireCurrent();
        systemVerifier.RequireBoundTrust();
        CefRuntimeTokenSnapshot.RequireSame(rootToken,
            CefProcessTokenObserver.ObserveRuntimeToken(rootHandle, root));
        return result;
    }
}

// Prepared broker-internal binding, not an IPC endpoint or launch permit.
// On successful construction, the serialized caller transfers root, both
// Jobs and registration ownership; the authenticated owner handle is borrowed.
// Both Jobs must be privately broker-created and never exported. The actual
// desktop containment is separate; no production admission is enabled here.
internal sealed class CefBoundDiagnosticSession : ICefDiagnosticLifetime
{
    private readonly CefDiagnosticSessionGuard guard;
    private readonly Func<CefDiagnosticSessionContext> readContext;
    private readonly Func<CefRetainedProcessDiagnostic[]> observe;
    private readonly Func<ClientIdentity, SafeProcessHandle, bool> matchesMember;
    private readonly Action<ClientIdentity, SafeProcessHandle> observeMember;
    private readonly Action requirePreparedRoot;
    private readonly CefContainedProcess preparedRoot;
    private readonly SafeProcessHandle retainedRoot;
    private bool activationAttempted;
    private bool activated;
    private readonly Func<int, bool> drain;
    private readonly CefRetainedLifetime? lifetime;
    private readonly CefPreparedCreationLifetime? borrowedCreation;

    internal CefBoundDiagnosticSession(ClientIdentity owner, SafeProcessHandle ownerHandle,
        object generation, CefRuntimeRegistrationLease registration,
        CefBrokerOwnedJob brokerJob, CefBrokerOwnedJob peerJob,
        ClientIdentity root, SafeProcessHandle rootHandle,
        Func<(object Generation, CefRuntimeRegistrationLease RuntimeLease, bool VolumeProtected)> readCurrent,
        CefSystemModuleVerifier systemVerifier, CefContainedProcess preparedRoot,
        CefPreparedCreationLifetime? borrowedCreation = null)
    {
        this.borrowedCreation = borrowedCreation;
        ArgumentNullException.ThrowIfNull(readCurrent);
        ArgumentNullException.ThrowIfNull(preparedRoot);
        preparedRoot.RequireRetainedPreparedRoot(rootHandle);
        this.preparedRoot = preparedRoot;
        retainedRoot = rootHandle;
        ArgumentNullException.ThrowIfNull(systemVerifier);
        systemVerifier.RequireBoundTrust();
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(brokerJob);
        ArgumentNullException.ThrowIfNull(peerJob);
        if (Native.ObserveProcess(ownerHandle) != owner ||
            Native.ObserveProcess(rootHandle) != root ||
            owner.ProcessId == root.ProcessId || owner.SessionId != root.SessionId ||
            !string.Equals(owner.UserSid, root.UserSid, StringComparison.OrdinalIgnoreCase) ||
            root.CreatedFileTime < owner.CreatedFileTime ||
            !string.Equals(root.ImagePath, registration.ImagePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF diagnostic session identity mismatch.");
        var ownerToken = CefProcessTokenObserver.ObserveLaunchToken(ownerHandle, owner);
        var rootToken = CefProcessTokenObserver.ObserveLaunchToken(rootHandle, root);
        CefLaunchTokenPolicy.RequirePair(ownerToken, rootToken);
        // Before accepting ownership, require two private broker-created Jobs.
        // Failure leaves ownership with the caller for stop recovery.
        registration.RequireCurrent();
        brokerJob.RequireOrBindPreparedPrivatePair(peerJob, owner, ownerHandle, root, rootHandle);
        registration.RequireCurrent();
        guard = new(owner, generation, registration);
        readContext = () =>
        {
            systemVerifier.RequireBoundTrust();
            CefLaunchTokenPolicy.RequireSame(ownerToken, CefProcessTokenObserver.ObserveLaunchToken(ownerHandle, owner));
            CefLaunchTokenPolicy.RequireSame(rootToken, CefProcessTokenObserver.ObserveLaunchToken(rootHandle, root));
            var current = readCurrent();
            return new(Native.ObserveProcess(ownerHandle), current.Generation, current.RuntimeLease,
                Native.IsRunning(ownerHandle), current.VolumeProtected);
        };
        requirePreparedRoot = () => brokerJob.RequirePreparedPrivateRoot(peerJob, owner, ownerHandle, root, rootHandle);
        // Both the periodic monitor and IPC must enforce the same role
        // boundary while independently collected member handles are retained.
        // A diagnostics-only observation cannot authorize a runtime sample.
        void RequireMemberRole(ClientIdentity identity, CefRetainedProcessDiagnostic diagnostic) =>
            CefRuntimeRolePolicy.RequireProvisionalRole(identity == root,
                diagnostic.Role, diagnostic.TokenDecision);
        observe = () => CefNestedProcessDiagnostics.Observe(brokerJob, peerJob, root, rootHandle,
            registration, systemVerifier, (identity, _, diagnostic) =>
                RequireMemberRole(identity, diagnostic));
        matchesMember = (identity, pipeClient) =>
            identity.SessionId == root.SessionId &&
            string.Equals(identity.UserSid, root.UserSid, StringComparison.OrdinalIgnoreCase) &&
            identity.CreatedFileTime >= root.CreatedFileTime &&
            string.Equals(identity.ImagePath, root.ImagePath, StringComparison.OrdinalIgnoreCase) &&
            (identity.ProcessId != root.ProcessId ||
                (identity == root && Native.SameLiveProcess(rootHandle, pipeClient))) &&
            brokerJob.ContainsPrivateRuntimeMember(peerJob, rootHandle, identity, pipeClient);
        observeMember = (client, pipeClient) =>
        {
            int matches = 0;
            _ = CefNestedProcessDiagnostics.Observe(brokerJob, peerJob, root, rootHandle,
                registration, systemVerifier, (identity, handle, diagnostic) =>
                {
                    // Provisional role checks are deliberately conservative.
                    // No known-but-unverified GPU/utility/network role passes.
                    RequireMemberRole(identity, diagnostic);
                    if (identity != client) return;
                    if (!Native.SameLiveProcess(handle, pipeClient) || ++matches != 1)
                        throw new UnauthorizedAccessException("CEF runtime member object mismatch.");
                });
            if (matches != 1)
                throw new UnauthorizedAccessException("CEF runtime member was not independently observed.");
        };
        drain = milliseconds =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            if (!brokerJob.TerminateAndVerifyPrivateDrain(peerJob, rootHandle, milliseconds)) return false;
            int remaining = (int)Math.Max(0L, milliseconds - started.ElapsedMilliseconds);
            return preparedRoot.WaitForNaturalJobExit(remaining);
        };
        // A prepared successor borrows the custody-owned original handle.
        // Never create a second disposal owner for that same resource set.
        if (borrowedCreation is null)
            lifetime = new CefRetainedLifetime(registration, preparedRoot, peerJob, brokerJob, rootHandle);
    }

    internal void RequirePreparedForHandoff()
    {
        if (borrowedCreation is null || activationAttempted)
            throw new UnauthorizedAccessException("CEF prepared successor unavailable.");
        _ = guard.Observe(readContext, () =>
        {
            requirePreparedRoot();
            preparedRoot.RequireRetainedPreparedRoot(retainedRoot);
            return true;
        });
    }

    // The slot owns this lifetime before invoking this method. No module
    // inventory is required while the loader has not yet executed; the
    // regular post-resume monitor checks it independently.
    internal void ActivatePreparedRoot()
    {
        // The new broker-created path has no reviewed protected-profile
        // launch environment or real CEF admission yet. Handoff is not
        // permission to run the image.
        if (borrowedCreation is not null)
            throw new UnauthorizedAccessException("CEF prepared successor activation is disabled.");
        if (activationAttempted)
            throw new UnauthorizedAccessException("CEF activation already attempted.");
        activationAttempted = true;
        _ = guard.Observe(readContext, () =>
        {
            requirePreparedRoot();
            preparedRoot.ResumeRetainedRoot(retainedRoot);
            return true;
        });
        activated = true;
    }

    // Runtime member transport boundary. No client role, PID nomination,
    // legacy receipt or acceptance of the old direct launch lifetime.
    internal bool MatchesRuntimeMember(ClientIdentity client, SafeProcessHandle pipeClient)
    {
        if (borrowedCreation is null || !activated) return false;
        try { return matchesMember(client, pipeClient); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal void RequireRuntimeAuthority(ClientIdentity client, SafeProcessHandle pipeClient)
    {
        _ = guard.Observe(readContext, () =>
        {
            if (!MatchesRuntimeMember(client, pipeClient))
                throw new UnauthorizedAccessException("CEF runtime member unavailable.");
            observeMember(client, pipeClient);
            if (!MatchesRuntimeMember(client, pipeClient))
                throw new UnauthorizedAccessException("CEF runtime member changed.");
            return true;
        });
    }

    internal CefRetainedProcessDiagnostic[] Observe()
    {
        if (!activated)
            throw new UnauthorizedAccessException("CEF diagnostic session has not activated.");
        return guard.Observe(readContext, observe);
    }
    public void Revoke()
    {
        guard.Revoke();
        borrowedCreation?.Revoke();
    }
    // Keep this session in the owning broker until true; false or an exception
    // leaves all unconfirmed/unreleased resources retained for retry.
    public bool RevokeAndRelease(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        guard.Revoke();
        if (borrowedCreation is not null)
            return guard.RevokeAndCheckDrain(() => borrowedCreation.RevokeAndRelease(milliseconds));
        return lifetime!.TryRelease(() => guard.RevokeAndCheckDrain(() => drain(milliseconds)));
    }
}
