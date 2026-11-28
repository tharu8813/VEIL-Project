// SPDX-License-Identifier: MPL-2.0
namespace Veil.VolumeBroker;

// A provisional token baseline, deliberately NOT a sandbox admission grant.
// The role must eventually come from independent broker observation of the
// same retained process. Command line alone cannot prove sandbox enforcement.
internal enum CefRoleTokenDecision
{
    Rejected,
    UnverifiedRole,
    MatchesProvisionalBaseline
}

internal static class CefRoleTokenPolicy
{
    internal static CefRoleTokenDecision Evaluate(ClientIdentity expected,
        CefObservedRole role, CefProcessTokenObservation observation)
    {
        if (expected is null || observation is null || observation.Identity != expected ||
            expected.ProcessId <= 0 || expected.SessionId <= 0 || expected.CreatedFileTime <= 0 ||
            string.IsNullOrWhiteSpace(expected.UserSid) || string.IsNullOrWhiteSpace(expected.ImagePath) ||
            observation.Elevated || observation.ElevationType != 1)
            return CefRoleTokenDecision.Rejected;

        // Exact known integrity levels only; never interpret an unknown RID
        // as a permitted sandbox level merely because it is below medium.
        const uint untrusted = 0, low = 0x1000, medium = 0x2000;
        if (observation.IntegrityRid is not (untrusted or low or medium))
            return CefRoleTokenDecision.Rejected;

        return role switch
        {
            // Candidate baseline for a separate non-admin desktop account.
            // Group/privilege inventory and launch binding still required.
            CefObservedRole.Browser when observation.IntegrityRid == medium &&
                !observation.Restricted && !observation.AppContainer =>
                CefRoleTokenDecision.MatchesProvisionalBaseline,
            // Restricted low/untrusted renderer was observed in isolated smoke.
            // AppContainer may provide additional restrictions, but neither
            // its presence nor this token baseline proves full sandbox policy.
            CefObservedRole.Renderer when observation.Restricted &&
                observation.IntegrityRid is untrusted or low =>
                CefRoleTokenDecision.MatchesProvisionalBaseline,
            // Do not invent a production permission profile from mere role
            // sightings. Utility subtypes require separate reviewed baselines.
            CefObservedRole.Gpu or CefObservedRole.Utility or CefObservedRole.Network =>
                CefRoleTokenDecision.UnverifiedRole,
            _ => CefRoleTokenDecision.Rejected
        };
    }
}

// Conservative diagnostic eligibility, NOT a reviewed sandbox grant.
internal static class CefRuntimeRolePolicy
{
    internal static bool MatchesProvisionalRole(bool isRoot, CefObservedRole role,
        CefRoleTokenDecision decision) =>
        decision == CefRoleTokenDecision.MatchesProvisionalBaseline &&
        (isRoot ? role == CefObservedRole.Browser : role == CefObservedRole.Renderer);

    internal static void RequireProvisionalRole(bool isRoot, CefObservedRole role,
        CefRoleTokenDecision decision)
    {
        if (!MatchesProvisionalRole(isRoot, role, decision))
            throw new UnauthorizedAccessException("CEF runtime role policy unavailable.");
    }
}
