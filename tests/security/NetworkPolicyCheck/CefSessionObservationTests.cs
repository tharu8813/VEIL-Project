// SPDX-License-Identifier: MPL-2.0
using Veil.VolumeBroker;

internal static class CefSessionObservationTests
{
    internal static void Run()
    {
        var owner = new ClientIdentity(10, 1, "synthetic-sid", "desktop.exe", 100);
        var root = new ClientIdentity(20, 1, "synthetic-sid", "cef.exe", 200);
        var renderer = new ClientIdentity(21, 1, "synthetic-sid", "cef.exe", 201);
        var generation = new object();
        var lease = new object();
        var browser = new CefObservedMember(root, CefObservedRole.Browser, true, true, true, true);
        var child = new CefObservedMember(renderer, CefObservedRole.Renderer, true, true, true, true);
        CefSessionObservationPolicy New() => new(owner, root, generation, lease);
        bool Observe(CefSessionObservationPolicy p, IReadOnlyList<CefObservedMember> members,
            int outer = 2, int inner = 2, ClientIdentity? currentOwner = null,
            object? currentGeneration = null, object? currentLease = null,
            bool ownerLive = true, bool protectedVolume = true, bool leaseVerified = true) =>
            p.Observe(currentOwner ?? owner, currentGeneration ?? generation, currentLease ?? lease,
                ownerLive, protectedVolume, leaseVerified, outer, inner, members);
        void Reject(Func<CefSessionObservationPolicy, bool> attempt)
        {
            var p = New();
            Check(!attempt(p));
            Check(!Observe(p, [browser, child])); // No resurrection after failure.
        }

        var good = New();
        Check(Observe(good, [browser, child]));
        Check(Observe(good, [browser], 1, 1));
        Check(Observe(good, [browser, child with { Role = CefObservedRole.Gpu }]));
        good.Revoke();
        Check(!Observe(good, [browser, child]));

        Reject(p => Observe(p, [browser, child], currentGeneration: new object()));
        Reject(p => Observe(p, [browser, child], currentLease: new object()));
        Reject(p => Observe(p, [browser, child], currentOwner: owner with { CreatedFileTime = 101 }));
        Reject(p => Observe(p, [browser, child], ownerLive: false));
        Reject(p => Observe(p, [browser, child], protectedVolume: false));
        Reject(p => Observe(p, [browser, child], leaseVerified: false));
        Reject(p => Observe(p, [browser, child], outer: 3));
        Reject(p => Observe(p, [browser], 2, 2));
        Reject(p => Observe(p, [], 0, 0));
        Reject(p => Observe(p, [child], 1, 1));
        Reject(p => Observe(p, [browser, browser]));
        Reject(p => Observe(p, [browser, child with { Role = CefObservedRole.Unknown }]));
        Reject(p => Observe(p, [browser, child with { Role = CefObservedRole.Browser }]));
        Reject(p => Observe(p, [browser, child with { LiveObjectVerified = false }]));
        Reject(p => Observe(p, [browser, child with { BothJobsVerified = false }]));
        Reject(p => Observe(p, [browser, child with { ImageAndModulesVerified = false }]));
        Reject(p => Observe(p, [browser, child with { RolePolicyVerified = false }]));
        Reject(p => Observe(p, [browser, child with { Identity = renderer with { SessionId = 2 } }]));
        Reject(p => Observe(p, [browser, child with { Identity = renderer with { UserSid = "other" } }]));
        Reject(p => Observe(p, [browser, child with { Identity = renderer with { ImagePath = "other.exe" } }]));
        Reject(p => Observe(p, [browser, child with { Identity = renderer with { ProcessId = owner.ProcessId } }]));
        Reject(p => Observe(p, [browser, child with { Identity = renderer with { CreatedFileTime = 199 } }]));
        Reject(p => Observe(p, [browser with { Identity = root with { CreatedFileTime = 202 } }, child]));
        var active = New();
        Check(Observe(active, [browser, child]));
        Check(!Observe(active, [browser, child], protectedVolume: false));
        Check(!Observe(active, [browser, child]));
        var context = new CefDiagnosticSessionContext(owner, generation, lease, true, true);
        CefDiagnosticSessionGuard NewGuard() => new(owner, generation, lease);
        void RefuseGuard(Action attempt)
        {
            try { attempt(); }
            catch (UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("Invalid diagnostic session accepted.");
        }
        var valid = NewGuard();
        Check(valid.Observe(() => context, () => 42) == 42);
        Check(valid.Observe(() => context, () => 43) == 43);
        foreach (var invalid in new[] {
            context with { Owner = owner with { CreatedFileTime = 101 } },
            context with { Generation = new object() },
            context with { RuntimeLease = new object() },
            context with { OwnerObjectVerified = false },
            context with { VolumeProtected = false } })
        {
            var before = NewGuard();
            bool called = false;
            RefuseGuard(() => before.Observe(() => invalid, () => { called = true; return 1; }));
            Check(!called);
            RefuseGuard(() => before.Observe(() => context, () => 1));
            var after = NewGuard();
            var current = context;
            RefuseGuard(() => after.Observe(() => current, () => { current = invalid; return 1; }));
            RefuseGuard(() => after.Observe(() => context, () => 1));
        }
        var failed = NewGuard();
        RefuseGuard(() => failed.Observe<int>(() => context, () => throw new UnauthorizedAccessException()));
        RefuseGuard(() => failed.Observe(() => context, () => 1));
        var revoked = NewGuard();
        RefuseGuard(() => revoked.Observe(() => context, () => { revoked.Revoke(); return 1; }));
        Check(!revoked.RevokeAndCheckDrain(() => false));
        RefuseGuard(() => revoked.Observe(() => context, () => 1));
        RefuseGuard(() => revoked.RevokeAndCheckDrain(() => throw new UnauthorizedAccessException()));
        Check(revoked.RevokeAndCheckDrain(() => true));
        RefuseGuard(() => revoked.Observe(() => context, () => 1));
        var reentrant = NewGuard();
        RefuseGuard(() => reentrant.Observe(() => context, () =>
        {
            Check(!reentrant.RevokeAndCheckDrain(() => throw new InvalidOperationException("Drain ran during observation.")));
            return 1;
        }));
        Check(reentrant.RevokeAndCheckDrain(() => true));
        foreach (uint eventType in new uint[] { 6, 7 })
        {
            Check(CefWindowsSessionRevocation.IsRevocationEvent(eventType));
            Check(CefWindowsSessionRevocation.TryDecode(eventType, 8, 1, out uint session));
            Check(session == 1 && CefWindowsSessionRevocation.MatchesOwner(owner, session));
            Check(!CefWindowsSessionRevocation.MatchesOwner(owner with { SessionId = 2 }, session));
            Check(!CefWindowsSessionRevocation.MatchesOwner(null, session));
            Check(!CefWindowsSessionRevocation.MatchesOwner(owner with { SessionId = 0 }, session));
            Check(!CefWindowsSessionRevocation.MatchesOwner(owner with { SessionId = -1 }, session));
        }
        foreach (uint eventType in new uint[] { 0, 1, 2, 3, 4, 5, 8, 9, uint.MaxValue })
        {
            Check(!CefWindowsSessionRevocation.IsRevocationEvent(eventType));
            Check(!CefWindowsSessionRevocation.TryDecode(eventType, 8, 1, out uint ignored) && ignored == 0);
        }
        foreach (int size in new[] { -1, 0, 4, 7, 9, 16 })
            Check(!CefWindowsSessionRevocation.TryDecode(7, size, 1, out uint ignored) && ignored == 0);
        foreach (uint sessionId in new uint[] { 0, 0x80000000, uint.MaxValue })
            Check(!CefWindowsSessionRevocation.TryDecode(7, 8, sessionId, out uint ignored) && ignored == 0);
        Check(CefWindowsSessionRevocation.TryDecode(6, 8, int.MaxValue, out uint lastSession) &&
            lastSession == int.MaxValue);
        CefAuthenticatedGeneration NewAuthority() => new(owner, generation);
        var authority = NewAuthority();
        Check(authority.Observe(owner, generation, true, true));
        Check(authority.Observe(owner, generation, true, true));
        authority.Revoke();
        Check(!authority.Observe(owner, generation, true, true));
        foreach (var invalid in new (ClientIdentity? Owner, object? Generation, bool Live, bool Protected)[]
        {
            (null, generation, true, true),
            (owner with { CreatedFileTime = 101 }, generation, true, true),
            (owner, null, true, true),
            (owner, new object(), true, true),
            (owner, generation, false, true),
            (owner, generation, true, false)
        })
        {
            var stale = NewAuthority();
            Check(stale.Observe(owner, generation, true, true));
            Check(!stale.Observe(invalid.Owner, invalid.Generation, invalid.Live, invalid.Protected));
            Check(!stale.Observe(owner, generation, true, true));
        }
        // Only a newly authenticated generation can establish new authority.
        var nextGeneration = new object();
        var next = new CefAuthenticatedGeneration(owner, nextGeneration);
        Check(next.Observe(owner, nextGeneration, true, true));
        Check(!authority.Observe(owner, nextGeneration, true, true));
        var runtimePin = new TrackedResource();
        var jobPin = new TrackedResource { FailNextRelease = true };
        var rootPin = new TrackedResource();
        var lifetime = new CefRetainedLifetime(runtimePin, null, jobPin, rootPin);
        int verificationCalls = 0;
        Check(!lifetime.TryRelease(() => { verificationCalls++; return false; }));
        Check(runtimePin.Releases == 0 && jobPin.Releases == 0 && rootPin.Releases == 0);
        RefuseGuard(() => lifetime.TryRelease(() =>
        {
            verificationCalls++;
            throw new UnauthorizedAccessException();
        }));
        Check(runtimePin.Releases == 0 && jobPin.Releases == 0 && rootPin.Releases == 0);
        RefuseGuard(() => lifetime.TryRelease(() =>
        {
            verificationCalls++;
            Check(!lifetime.TryRelease(() => throw new InvalidOperationException("Reentrant verification.")));
            return true;
        }));
        Check(verificationCalls == 3 && runtimePin.Releases == 1 && jobPin.Releases == 1 && rootPin.Releases == 0);
        Check(lifetime.TryRelease(() => throw new InvalidOperationException("Rechecked already released handles.")));
        Check(runtimePin.Releases == 1 && jobPin.Releases == 2 && rootPin.Releases == 1);
        Check(lifetime.TryRelease(() => throw new InvalidOperationException("Repeated verification.")));
        Check(runtimePin.Releases == 1 && jobPin.Releases == 2 && rootPin.Releases == 1);
        var slot = new CefDiagnosticSessionSlot<TrackedSession>();
        var firstSession = new TrackedSession();
        var secondSession = new TrackedSession();
        Check(!slot.Occupied && slot.TryReleaseRetired(0));
        Check(slot.TryRegister(generation, firstSession));
        Check(slot.Current(generation) == firstSession && slot.Current(nextGeneration) is null);
        Check(!slot.TryRegister(nextGeneration, secondSession));
        Check(!slot.TryReleaseRetired(0) && firstSession.ReleaseAttempts == 0);
        slot.Retire();
        Check(firstSession.Revoked && slot.CleanupPending && slot.Current(generation) is null);
        Check(!slot.TryReleaseRetired(0) && slot.Occupied);
        Check(!slot.TryRegister(nextGeneration, secondSession));
        firstSession.ThrowOnRelease = true;
        RefuseGuard(() => slot.TryReleaseRetired(0));
        Check(slot.CleanupPending && !slot.TryRegister(nextGeneration, secondSession));
        firstSession.ThrowOnRelease = false;
        firstSession.ExitConfirmed = true;
        Check(slot.TryReleaseRetired(0) && !slot.Occupied);
        Check(slot.TryRegister(nextGeneration, secondSession));
        Check(slot.Current(generation) is null && slot.Current(nextGeneration) == secondSession);
        Check(!slot.TryReleaseRetired(0) && secondSession.ReleaseAttempts == 0);
        slot.Retire();
        secondSession.ExitConfirmed = true;
        Check(slot.TryReleaseRetired(0) && !slot.Occupied);
        Console.WriteLine("PASS: diagnostic owner slot retires before release, preserves failed cleanup and rejects replacement");
        Console.WriteLine("PASS: retained resource cleanup waits for verified exit and resumes failed release");
        Console.WriteLine("PASS: broker unlock generation cannot resurrect after owner or protection failure");
        Console.WriteLine("PASS: Windows lock/logoff notification filtering and authenticated owner isolation");
        Console.WriteLine("PASS: diagnostic session pre/post checks, irreversible retirement and retryable drain");
        Console.WriteLine("PASS: pure CEF multi-process observation, generation/lease binding and irreversible revocation");
    }

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("CEF session observation policy regression.");
    }

    private sealed class TrackedSession : ICefDiagnosticLifetime
    {
        internal bool Revoked { get; private set; }
        internal int ReleaseAttempts { get; private set; }
        internal bool ExitConfirmed { get; set; }
        internal bool ThrowOnRelease { get; set; }
        public void Revoke() => Revoked = true;
        public bool RevokeAndRelease(int milliseconds)
        {
            Check(Revoked);
            ReleaseAttempts++;
            if (ThrowOnRelease) throw new UnauthorizedAccessException("Synthetic session cleanup failure.");
            return ExitConfirmed;
        }
    }

    private sealed class TrackedResource : IDisposable
    {
        internal int Releases { get; private set; }
        internal bool FailNextRelease { get; set; }
        public void Dispose()
        {
            Releases++;
            if (FailNextRelease)
            {
                FailNextRelease = false;
                throw new UnauthorizedAccessException("Synthetic release failure.");
            }
        }
    }
}
