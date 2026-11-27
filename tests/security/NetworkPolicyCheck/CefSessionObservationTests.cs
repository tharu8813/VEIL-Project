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
        void Check(bool value)
        {
            if (!value) throw new InvalidOperationException("CEF session observation policy regression.");
        }
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
        Console.WriteLine("PASS: pure CEF multi-process observation, generation/lease binding and irreversible revocation");
    }
}
