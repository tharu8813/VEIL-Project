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
        SafeFileHandle desktopJob,
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
        var result = brokerJob.ObserveCollectedNestedDiagnostics(desktopJob, root, rootHandle,
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
// The serialized caller retains all handles and the registration until drain.
internal sealed class CefBoundDiagnosticSession
{
    private readonly CefDiagnosticSessionGuard guard;
    private readonly Func<CefDiagnosticSessionContext> readContext;
    private readonly Func<CefRetainedProcessDiagnostic[]> observe;
    private readonly Func<int, bool> drain;

    internal CefBoundDiagnosticSession(ClientIdentity owner, SafeProcessHandle ownerHandle,
        object generation, CefRuntimeRegistrationLease registration,
        CefBrokerOwnedJob brokerJob, SafeFileHandle desktopJob,
        ClientIdentity root, SafeProcessHandle rootHandle,
        Func<(object Generation, CefRuntimeRegistrationLease RuntimeLease, bool VolumeProtected)> readCurrent,
        CefSystemModuleVerifier? systemVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(readCurrent);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(brokerJob);
        ArgumentNullException.ThrowIfNull(desktopJob);
        if (Native.ObserveProcess(ownerHandle) != owner ||
            Native.ObserveProcess(rootHandle) != root ||
            owner.ProcessId == root.ProcessId || owner.SessionId != root.SessionId ||
            !string.Equals(owner.UserSid, root.UserSid, StringComparison.OrdinalIgnoreCase) ||
            root.CreatedFileTime < owner.CreatedFileTime ||
            !string.Equals(root.ImagePath, registration.ImagePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF diagnostic session identity mismatch.");
        guard = new(owner, generation, registration);
        readContext = () =>
        {
            var current = readCurrent();
            return new(Native.ObserveProcess(ownerHandle), current.Generation, current.RuntimeLease,
                Native.IsRunning(ownerHandle), current.VolumeProtected);
        };
        observe = () => CefNestedProcessDiagnostics.Observe(brokerJob, desktopJob, root, rootHandle,
            registration, systemVerifier);
        drain = milliseconds => brokerJob.TerminateAndVerifyNestedDrain(desktopJob, rootHandle, milliseconds);
    }

    internal CefRetainedProcessDiagnostic[] Observe() => guard.Observe(readContext, observe);
    internal void Revoke() => guard.Revoke();
    internal bool RevokeAndDrain(int milliseconds) =>
        guard.RevokeAndCheckDrain(() => drain(milliseconds));
}
