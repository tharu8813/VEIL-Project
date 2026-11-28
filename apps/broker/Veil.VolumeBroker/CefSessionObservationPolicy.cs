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

// Serialized resource ownership for both the disabled candidate and the
// future nested session. No finalizer or Dispose shortcut: callers must
// retain this object until verified exit AND successful resource release.
internal sealed class CefRetainedLifetime(params IDisposable?[] resources)
{
    private readonly IDisposable?[] retained = resources.ToArray();
    private bool exitConfirmed;
    private bool releasing;
    private int next;

    // If verification fails or throws, no resource is released. After proof,
    // resume a failed release at the same resource without rechecking closed
    // handles. Only a private broker-owned, permanently retired job may use
    // this helper; no new process assignment after the exit proof.
    internal bool TryRelease(Func<bool> verifyExit)
    {
        ArgumentNullException.ThrowIfNull(verifyExit);
        if (releasing) return false;
        releasing = true;
        try
        {
            if (!exitConfirmed)
            {
                if (!verifyExit()) return false;
                exitConfirmed = true;
            }
            while (next < retained.Length)
            {
                retained[next]?.Dispose();
                retained[next++] = null;
            }
            return true;
        }
        finally { releasing = false; }
    }
}

// Single owner slot, serialized by Broker.serial. No IPC surface and no
// ownership transfer on a refused registration. An occupied retired slot
// continues to block admission until its entire retained lifetime is released.
internal interface ICefDiagnosticLifetime
{
    void Revoke();
    bool RevokeAndRelease(int milliseconds);
}

internal enum CefDiagnosticActivationOutcome { Refused, Activated, Retired }

internal sealed class CefDiagnosticSessionSlot<T> where T : class, ICefDiagnosticLifetime
{
    private T? session;
    private object? generation;
    private bool retired;
    private bool releasing;
    private bool activating;
    internal bool Occupied => session is not null;
    internal bool CleanupPending => Occupied && retired;

    internal bool TryRegister(object expectedGeneration, T candidate)
    {
        ArgumentNullException.ThrowIfNull(expectedGeneration);
        ArgumentNullException.ThrowIfNull(candidate);
        if (Occupied || releasing || activating) return false;
        session = candidate;
        generation = expectedGeneration;
        retired = false;
        return true;
    }

    // Prepared lifetime is transferred BEFORE the one-shot resume callback.
    // Refused means the caller still owns it. Activated and Retired both
    // mean the slot owns it, including when resume failed after execution
    // may have started. Caller must arrange cleanup of a retired slot.
    // No reentrant observation, release or replacement during activation.
    internal CefDiagnosticActivationOutcome RegisterAndActivate(object expectedGeneration, T candidate,
        Action activate, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(activate);
        failure = null;
        if (!TryRegister(expectedGeneration, candidate))
            return CefDiagnosticActivationOutcome.Refused;
        activating = true;
        bool committed = false;
        try
        {
            activate();
            if (retired)
                throw new UnauthorizedAccessException("CEF session retired during activation.");
            committed = true;
            return CefDiagnosticActivationOutcome.Activated;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error;
            return CefDiagnosticActivationOutcome.Retired;
        }
        finally
        {
            // Even an unexpected exception cannot leave a partially
            // activated session observable or its handles unowned.
            try { if (!committed) Retire(); }
            finally { activating = false; }
        }
    }

    // Construct a borrowing successor while the old slot remains occupied.
    // Only the exact current owner/generation may replace it. The successor
    // must delegate resource release to the old owner; no release on swap.
    // A refusal before the factory leaves the old slot untouched. A failure
    // during construction retires the original slot, not a partial successor.
    internal bool ReplacePrepared(object expectedGeneration, T expected,
        Func<T> build, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(expectedGeneration);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(build);
        failure = null;
        if (retired || releasing || activating ||
            !ReferenceEquals(generation, expectedGeneration) || !ReferenceEquals(session, expected))
            return false;
        activating = true;
        bool committed = false;
        try
        {
            var successor = build();
            if (successor is null || ReferenceEquals(successor, expected) || retired)
                throw new UnauthorizedAccessException("CEF successor refused.");
            session = successor;
            committed = true;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error;
            return false;
        }
        finally
        {
            try { if (!committed) Retire(); }
            finally { activating = false; }
        }
    }

    internal T? Current(object? expectedGeneration) =>
        !retired && !activating && ReferenceEquals(generation, expectedGeneration) ? session : null;

    internal void Retire()
    {
        if (session is null) return;
        retired = true;
        session.Revoke();
    }

    internal bool TryReleaseRetired(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        if (session is null) return true;
        if (!retired || releasing || activating) return false;
        releasing = true;
        try
        {
            if (!session.RevokeAndRelease(milliseconds)) return false;
            session = null;
            generation = null;
            return true;
        }
        finally { releasing = false; }
    }
}


// Serialized, generation-bound relock obligation. Drain completion alone
// cannot clear it. A stale generation or uncertain lock remains blocked.
internal sealed class CefDiagnosticRelock
{
    private object? generation;
    internal bool Pending { get; private set; }

    internal void Request(object? ownerGeneration)
    {
        if (Pending) return;
        generation = ownerGeneration;
        Pending = true;
    }

    internal bool TryComplete(object? currentGeneration, bool cleanupConfirmed, Func<bool> lockAndVerify)
    {
        ArgumentNullException.ThrowIfNull(lockAndVerify);
        if (!Pending) return true;
        if (generation is null || !ReferenceEquals(generation, currentGeneration) || !cleanupConfirmed)
            return false;
        if (!lockAndVerify()) return false;
        Pending = false;
        generation = null;
        return true;
    }
}

// One retry iteration under the broker's serial gate. A cleanup exception
// is not exit proof: defer relock until the next safe attempt. A relock
// exception likewise leaves the existing generation-bound obligation intact.
// This policy does not modify ownership, unlock state or the retry flag.
internal static class CefStopRetryPolicy
{
    internal static Exception? TryAttempt(Action cleanup, Action relock)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(relock);
        try
        {
            cleanup();
            relock();
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return error;
        }
    }

    internal static bool ShouldContinue(int attempt, int maximumAttempts, bool pending, Exception? failure)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        if (attempt >= maximumAttempts) throw new ArgumentOutOfRangeException(nameof(attempt));
        return attempt < maximumAttempts - 1 && (pending || failure is not null);
    }
}

// Once the caller has revoked authentication and latched any relock
// obligation, no recoverable cleanup failure may skip a later phase.
// Preserve the first failure for the request/monitor; do not claim that
// a failed phase released its resources. All callbacks run under serial.
internal static class CefOwnerRevocationCleanup
{
    internal static void Run(Action retire, Action revokePending, Action retry, Action schedule)
    {
        ArgumentNullException.ThrowIfNull(retire);
        ArgumentNullException.ThrowIfNull(revokePending);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(schedule);
        Exception? failure = null;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure ??= error;
            }
        }

        Attempt(retire);
        Attempt(revokePending);
        Attempt(retry);
        Attempt(schedule);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
