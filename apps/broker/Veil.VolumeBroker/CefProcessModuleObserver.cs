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
        CefSystemModuleVerifier? systemVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(registration);
        string image = registration.ImagePath;
        if (!string.Equals(expected.ImagePath, image, StringComparison.OrdinalIgnoreCase) ||
            Native.ObserveProcess(process) != expected)
            throw new UnauthorizedAccessException("CEF module process identity mismatch.");
        Action<IEnumerable<string>>? verifySystem = systemVerifier is null ? null : systemVerifier.RequireModules;
        var result = CefModuleInventoryPolicy.Verify(image, Environment.SystemDirectory,
            Native.SnapshotModulePaths(process, expected),
            registration.RequireObservedApplicationModules,
            () => Native.SnapshotModulePaths(process, expected), verifySystem);
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
        CefSystemModuleVerifier? systemVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(brokerJob);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHandle);
        ArgumentNullException.ThrowIfNull(registration);
        // Only the independently retained root and trusted jobs are inputs.
        // The broker collects member handles itself, never from IPC.
        registration.RequireCurrent();
        var result = brokerJob.ObservePrivateNestedDiagnostics(peerJob, root, rootHandle,
            (identity, handle) =>
            {
                var role = CefCommandRoleObserver.Observe(handle, identity, root);
                var token = CefProcessTokenObserver.Observe(handle, identity);
                var modules = CefProcessModuleObserver.Observe(handle, identity,
                    registration, systemVerifier);
                if (role != CefCommandRoleObserver.Observe(handle, identity, root) ||
                    token != CefProcessTokenObserver.Observe(handle, identity))
                    throw new UnauthorizedAccessException("CEF role or token fields changed during diagnostics.");
                registration.RequireCurrent();
                return new CefRetainedProcessDiagnostic(identity, role.Role,
                    CefRoleTokenPolicy.Evaluate(identity, role.Role, token), modules);
            });
        registration.RequireCurrent();
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
    private readonly Func<int, bool> drain;
    private readonly CefRetainedLifetime lifetime;

    internal CefBoundDiagnosticSession(ClientIdentity owner, SafeProcessHandle ownerHandle,
        object generation, CefRuntimeRegistrationLease registration,
        CefBrokerOwnedJob brokerJob, CefBrokerOwnedJob peerJob,
        ClientIdentity root, SafeProcessHandle rootHandle,
        Func<(object Generation, CefRuntimeRegistrationLease RuntimeLease, bool VolumeProtected)> readCurrent,
        CefSystemModuleVerifier? systemVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(readCurrent);
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
        // Before accepting ownership, require two private broker-created Jobs.
        // Failure leaves ownership with the caller for stop recovery.
        registration.RequireCurrent();
        brokerJob.RequirePrivateNestedBinding(peerJob, owner, ownerHandle, root, rootHandle);
        registration.RequireCurrent();
        guard = new(owner, generation, registration);
        readContext = () =>
        {
            var current = readCurrent();
            return new(Native.ObserveProcess(ownerHandle), current.Generation, current.RuntimeLease,
                Native.IsRunning(ownerHandle), current.VolumeProtected);
        };
        observe = () => CefNestedProcessDiagnostics.Observe(brokerJob, peerJob, root, rootHandle,
            registration, systemVerifier);
        drain = milliseconds => brokerJob.TerminateAndVerifyPrivateDrain(peerJob, rootHandle, milliseconds);
        lifetime = new CefRetainedLifetime(registration, peerJob, brokerJob, rootHandle);
    }

    internal CefRetainedProcessDiagnostic[] Observe() => guard.Observe(readContext, observe);
    public void Revoke() => guard.Revoke();
    // Keep this session in the owning broker until true; false or an exception
    // leaves all unconfirmed/unreleased resources retained for retry.
    public bool RevokeAndRelease(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        guard.Revoke();
        return lifetime.TryRelease(() => guard.RevokeAndCheckDrain(() => drain(milliseconds)));
    }
}
