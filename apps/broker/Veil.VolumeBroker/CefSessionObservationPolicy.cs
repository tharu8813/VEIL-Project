// SPDX-License-Identifier: MPL-2.0
namespace Veil.VolumeBroker;

// Pure P2-03 model only. No IPC, process creation, or authority to start CEF.
// All observations must eventually originate from a serialized broker holding
// actual process/job/runtime handles, not client claims or PID-only samples.
internal enum CefObservedRole { Unknown, Browser, Renderer, Gpu, Utility, Network }
internal sealed record CefObservedMember(ClientIdentity Identity, CefObservedRole Role,
    bool LiveObjectVerified, bool BothJobsVerified, bool ImageAndModulesVerified,
    bool RolePolicyVerified);

internal sealed class CefSessionObservationPolicy
{
    private readonly ClientIdentity owner;
    private readonly ClientIdentity root;
    private readonly object generation;
    private readonly object runtimeLease;
    private bool retired;

    internal CefSessionObservationPolicy(ClientIdentity owner, ClientIdentity root,
        object generation, object runtimeLease)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(runtimeLease);
        if (!ValidIdentity(owner) || !ValidIdentity(root) || owner.ProcessId == root.ProcessId ||
            !SameAccount(owner, root) || root.CreatedFileTime < owner.CreatedFileTime)
            throw new ArgumentException("Invalid session identity.");
        this.owner = owner;
        this.root = root;
        this.generation = generation;
        this.runtimeLease = runtimeLease;
    }

    private static bool ValidIdentity(ClientIdentity id) => id.ProcessId > 0 && id.SessionId > 0 &&
        id.CreatedFileTime > 0 && !string.IsNullOrWhiteSpace(id.UserSid) &&
        !string.IsNullOrWhiteSpace(id.ImagePath);

    private static bool SameAccount(ClientIdentity a, ClientIdentity b) =>
        a.SessionId == b.SessionId && string.Equals(a.UserSid, b.UserSid, StringComparison.OrdinalIgnoreCase);

    // A matching snapshot is not proof of full lifetime coverage. The future
    // broker must separately enforce atomic containment and observe churn.
    // Any inconsistent or incomplete observation retires this generation.
    internal bool Observe(ClientIdentity observedOwner, object currentGeneration,
        object currentRuntimeLease, bool ownerObjectVerified, bool volumeProtected,
        bool runtimeLeaseVerified, int outerActive, int innerActive,
        IReadOnlyList<CefObservedMember> members)
    {
        if (retired) return false;
        retired = true; // Exceptions and all rejected observations fail closed.
        if (owner != observedOwner || !ReferenceEquals(generation, currentGeneration) ||
            !ReferenceEquals(runtimeLease, currentRuntimeLease) || !ownerObjectVerified ||
            !volumeProtected || !runtimeLeaseVerified || members is null ||
            outerActive <= 0 || outerActive != innerActive || members.Count != outerActive)
            return false;

        var pids = new HashSet<int>();
        int roots = 0;
        foreach (var member in members)
        {
            if (member is null || member.Identity is null) return false;
            var id = member.Identity;
            if (!ValidIdentity(id) || !SameAccount(owner, id) || id.ProcessId == owner.ProcessId ||
                id.CreatedFileTime < root.CreatedFileTime || !pids.Add(id.ProcessId) ||
                !string.Equals(id.ImagePath, root.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                !member.LiveObjectVerified || !member.BothJobsVerified ||
                !member.ImageAndModulesVerified || !member.RolePolicyVerified)
                return false;
            if (member.Role == CefObservedRole.Browser)
            {
                if (id != root || ++roots != 1) return false;
            }
            else if (id.ProcessId == root.ProcessId ||
                member.Role is not (CefObservedRole.Renderer or CefObservedRole.Gpu or
                    CefObservedRole.Utility or CefObservedRole.Network))
                return false;
        }
        if (roots != 1) return false;
        retired = false;
        return true;
    }

    internal void Revoke() => retired = true;
}

// Broker-internal context, never a wire message or client assertion.
// The actual service must derive it from retained owner/volume/lease state.
internal sealed record CefDiagnosticSessionContext(ClientIdentity Owner, object Generation,
    object RuntimeLease, bool OwnerObjectVerified, bool VolumeProtected);

internal sealed class CefDiagnosticSessionGuard
{
    private readonly ClientIdentity owner;
    private readonly object generation;
    private readonly object runtimeLease;
    private bool retired;
    private bool observing;
    private bool draining;

    internal CefDiagnosticSessionGuard(ClientIdentity owner, object generation, object runtimeLease)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
        this.runtimeLease = runtimeLease ?? throw new ArgumentNullException(nameof(runtimeLease));
    }

    private void RequireContext(CefDiagnosticSessionContext current)
    {
        if (retired || current is null || current.Owner != owner ||
            !ReferenceEquals(current.Generation, generation) ||
            !ReferenceEquals(current.RuntimeLease, runtimeLease) ||
            !current.OwnerObjectVerified || !current.VolumeProtected)
            throw new UnauthorizedAccessException("CEF diagnostic session is unavailable.");
    }

    // Serialized broker access required. Results are diagnostics only, not
    // an admission verdict. No partial result escapes an invalidated session.
    internal T Observe<T>(Func<CefDiagnosticSessionContext> readContext, Func<T> observe)
    {
        if (retired || observing || draining)
        {
            retired = true;
            throw new UnauthorizedAccessException("CEF diagnostic session is retired or busy.");
        }
        observing = true;
        try
        {
            RequireContext(readContext());
            T result = observe();
            RequireContext(readContext());
            return result;
        }
        catch
        {
            retired = true;
            throw;
        }
        finally { observing = false; }
    }

    internal void Revoke() => retired = true;

    // Revoke BEFORE attempting cleanup; a failure/exception never permits
    // observation again. Keep ownership and retry until the caller verifies
    // actual exit. Even true is not permission to relock the volume by itself.
    internal bool RevokeAndCheckDrain(Func<bool> terminateAndVerify)
    {
        retired = true;
        if (observing || draining) return false;
        draining = true;
        try { return terminateAndVerify(); }
        finally { draining = false; }
    }
}

// Pure SCM notification filtering shared by service and broker tests.
// Neither a notification nor a session number grants authentication.
internal static class CefWindowsSessionRevocation
{
    internal static bool IsRevocationEvent(uint eventType) => eventType is 6 or 7; // LOGOFF, LOCK

    internal static bool TryDecode(uint eventType, int notificationSize, uint sessionId,
        out uint revokeSession)
    {
        revokeSession = 0;
        if (!IsRevocationEvent(eventType) || notificationSize != 8 ||
            sessionId == 0 || sessionId > int.MaxValue)
            return false;
        revokeSession = sessionId;
        return true;
    }

    internal static bool MatchesOwner(ClientIdentity? owner, uint sessionId) =>
        owner is { SessionId: > 0 } && (uint)owner.SessionId == sessionId;
}

// Bound to one successful broker unlock. A failed live observation retires
// it permanently; a later matching volume snapshot cannot restore authority.
internal sealed class CefAuthenticatedGeneration(ClientIdentity owner, object generation)
{
    private bool retired;

    internal bool Observe(ClientIdentity? currentOwner, object? currentGeneration,
        bool ownerObjectVerified, bool volumeProtected)
    {
        if (retired) return false;
        if (currentOwner == owner && ReferenceEquals(generation, currentGeneration) &&
            ownerObjectVerified && volumeProtected)
            return true;
        retired = true;
        return false;
    }

    internal void Revoke() => retired = true;
}
