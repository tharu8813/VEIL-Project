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
        var owner = new CefLaunchTokenSnapshot(browser, 11, 12, 13, false, true);
        var root = owner with { Token = browser with { Identity = identity with { ProcessId = 101, CreatedFileTime = 124 } },
            TokenId = 14, ModifiedId = 15 };
        CefLaunchTokenPolicy.RequirePair(owner, root);
        CefLaunchTokenPolicy.RequireSame(owner, owner);
        void Refuse(Action action)
        {
            try { action(); }
            catch (UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("Invalid launch token accepted.");
        }
        Refuse(() => CefLaunchTokenPolicy.RequirePair(owner, root with { AuthenticationId = 99 }));
        Refuse(() => CefLaunchTokenPolicy.RequirePair(owner, owner));
        Refuse(() => CefLaunchTokenPolicy.RequirePair(owner, root with { AdministratorGroupPresent = true }));
        Refuse(() => CefLaunchTokenPolicy.RequirePair(owner, root with { OnlyStandardPrivileges = false }));
        Refuse(() => CefLaunchTokenPolicy.RequireStandard(owner with { Token = browser with { Elevated = true } }));
        Refuse(() => CefLaunchTokenPolicy.RequireStandard(owner with { Token = browser with { ElevationType = 3 } }));
        Refuse(() => CefLaunchTokenPolicy.RequireStandard(owner with { Token = browser with { Identity = identity with { UserSid = "S-1-5-18" } } }));
        Refuse(() => CefLaunchTokenPolicy.RequireSame(owner, owner with { ModifiedId = 99 }));
        Refuse(() => CefLaunchTokenPolicy.RequireSame(owner, owner with { TokenId = 99 }));
        var duplicate = owner with { TokenId = 22, ModifiedId = 23 };
        CefLaunchTokenPolicy.RequireDuplicate(owner, duplicate);
        Refuse(() => CefLaunchTokenPolicy.RequireDuplicate(owner, owner));
        Refuse(() => CefLaunchTokenPolicy.RequireDuplicate(owner, duplicate with { TokenId = 0 }));
        Refuse(() => CefLaunchTokenPolicy.RequireDuplicate(owner, duplicate with { AuthenticationId = 99 }));
        Refuse(() => CefLaunchTokenPolicy.RequireDuplicate(owner, duplicate with { AdministratorGroupPresent = true }));
        Refuse(() => CefLaunchTokenPolicy.RequireDuplicate(owner, duplicate with { OnlyStandardPrivileges = false }));
        Refuse(() => CefLaunchTokenPolicy.RequireDuplicate(owner, duplicate with { Token = root.Token }));
        Console.WriteLine("PASS: primary token duplication policy (synthetic)");
        Console.WriteLine("PASS: separate standard-user launch token and logon binding policy (synthetic)");
        Console.WriteLine("PASS: conservative CEF role token baselines and unknown/unverified role refusal");
    }
}
