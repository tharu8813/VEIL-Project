// SPDX-License-Identifier: MPL-2.0
using Veil.VolumeBroker;

internal static class CefRoleTokenPolicyTests
{
    internal static void Run()
    {
        var identity = new ClientIdentity(100, 1, "synthetic-sid", "cef.exe", 123);
        var browser = new CefProcessTokenObservation(identity, 0x2000, false, false, false, 1);
        var renderer = browser with { IntegrityRid = 0x1000, Restricted = true };
        void Expect(CefObservedRole role, CefProcessTokenObservation token, CefRoleTokenDecision result)
        {
            if (CefRoleTokenPolicy.Evaluate(identity, role, token) != result)
                throw new InvalidOperationException("Unexpected CEF role token baseline verdict.");
        }
        const CefRoleTokenDecision match = CefRoleTokenDecision.MatchesProvisionalBaseline;
        const CefRoleTokenDecision reject = CefRoleTokenDecision.Rejected;
        const CefRoleTokenDecision unverified = CefRoleTokenDecision.UnverifiedRole;
        Expect(CefObservedRole.Browser, browser, match);
        Expect(CefObservedRole.Renderer, renderer, match);
        Expect(CefObservedRole.Renderer, renderer with { IntegrityRid = 0 }, match);
        Expect(CefObservedRole.Renderer, renderer with { AppContainer = true }, match);
        Expect(CefObservedRole.Renderer, browser, reject);
        Expect(CefObservedRole.Renderer, renderer with { Restricted = false }, reject);
        Expect(CefObservedRole.Renderer, renderer with { IntegrityRid = 0x2000 }, reject);
        Expect(CefObservedRole.Renderer, renderer with { IntegrityRid = 0x0800 }, reject);
        Expect(CefObservedRole.Browser, browser with { IntegrityRid = 0x3000 }, reject);
        Expect(CefObservedRole.Browser, browser with { AppContainer = true }, reject);
        Expect(CefObservedRole.Browser, browser with { Restricted = true }, reject);
        Expect(CefObservedRole.Browser, browser with { Elevated = true }, reject);
        Expect(CefObservedRole.Browser, browser with { ElevationType = 2 }, reject);
        Expect(CefObservedRole.Renderer, renderer with { ElevationType = 3 }, reject);
        Expect(CefObservedRole.Renderer, renderer with { ElevationType = 0 }, reject);
        Expect(CefObservedRole.Renderer, renderer with { Identity = identity with { CreatedFileTime = 124 } }, reject);
        Expect(CefObservedRole.Renderer, renderer with { Identity = identity with { SessionId = 2 } }, reject);
        Expect(CefObservedRole.Unknown, renderer, reject);
        Expect((CefObservedRole)999, renderer, reject);
        foreach (var role in new[] { CefObservedRole.Gpu, CefObservedRole.Utility, CefObservedRole.Network })
        {
            Expect(role, renderer, unverified);
            Expect(role, browser, unverified);
            Expect(role, browser with { Elevated = true }, reject);
        }
        Console.WriteLine("PASS: conservative CEF role token baselines and unknown/unverified role refusal");
    }
}
