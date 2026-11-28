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
        IReadOnlyList<(ClientIdentity Identity, SafeProcessHandle Handle)> members,
        ClientIdentity root, CefRuntimeRegistrationLease registration,
        CefSystemModuleVerifier? systemVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(brokerJob);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(registration);
        var retained = members.ToArray();
        // root and both Job handles must originate from the serialized broker,
        // not from an IPC request or a client-supplied role claim.
        if (retained.Count(member => member.Identity == root) != 1)
            throw new UnauthorizedAccessException("Missing unique CEF diagnostic root.");
        registration.RequireCurrent();
        var result = brokerJob.ObserveNestedRetainedDiagnostics(desktopJob, retained,
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
