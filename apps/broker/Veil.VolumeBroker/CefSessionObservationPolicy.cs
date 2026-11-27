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
