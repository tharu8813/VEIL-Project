// SPDX-License-Identifier: MPL-2.0
namespace Veil.VolumeBroker;

/// <summary>
/// Pure, one-shot admission policy for a future broker-owned CEF child grant.
/// Only trusted broker observations may be supplied to this policy. It is
/// deliberately not exposed by the pipe protocol or product startup.
/// Caller must serialize operations and hold both original process handles.
/// </summary>
internal sealed class CefChildAdmission
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);
    private readonly TimeProvider clock;
    private readonly long issuedAt;
    private readonly ClientIdentity owner;
    private readonly ClientIdentity child;
    private readonly object unlockGeneration;
    private bool retired;

    internal CefChildAdmission(ClientIdentity owner, ClientIdentity child,
        object unlockGeneration, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(unlockGeneration);
        ArgumentNullException.ThrowIfNull(clock);
        if (owner.ProcessId <= 0 || child.ProcessId <= 0 ||
            owner.ProcessId == child.ProcessId ||
            owner.CreatedFileTime <= 0 || child.CreatedFileTime < owner.CreatedFileTime ||
            owner.SessionId <= 0 || owner.SessionId != child.SessionId ||
            string.IsNullOrWhiteSpace(owner.UserSid) ||
            !string.Equals(owner.UserSid, child.UserSid, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(owner.ImagePath) || string.IsNullOrWhiteSpace(child.ImagePath))
            throw new ArgumentException("Invalid CEF child identity.");

        this.owner = owner;
        this.child = child;
        this.unlockGeneration = unlockGeneration;
        this.clock = clock;
        issuedAt = clock.GetTimestamp();
    }

    // A true result means only the modeled admission conditions were met.
    // The future broker must independently observe image signature/hash,
    // actual launch binding, live process handles and current volume status.
    internal bool TryRedeem(ClientIdentity observedOwner, ClientIdentity observedChild,
        object currentUnlockGeneration, bool ownerAlive, bool childAlive,
        bool childImageVerified, bool protectedVolume)
    {
        if (retired) return false;
        // Every attempt retires this grant, even a rejected attempt. There
        // is no retry, fallback, or transferable token.
        retired = true;
        var elapsed = clock.GetElapsedTime(issuedAt);
        return elapsed >= TimeSpan.Zero && elapsed < Lifetime &&
            ReferenceEquals(unlockGeneration, currentUnlockGeneration) &&
            owner == observedOwner && child == observedChild &&
            ownerAlive && childAlive && childImageVerified && protectedVolume;
    }

    internal bool IsExpired => clock.GetElapsedTime(issuedAt) is var elapsed &&
        (elapsed < TimeSpan.Zero || elapsed >= Lifetime);

    internal bool MatchesChild(ClientIdentity observed) => !retired && child == observed;

    internal void Revoke() => retired = true;
}
