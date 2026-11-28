// SPDX-License-Identifier: MPL-2.0
using Veil.VolumeBroker;

internal static class CefMitigationTests
{
    internal static void Run()
    {
        var observed = new List<CefMitigationPolicy>();
        var zero = CefMitigationSnapshot.Read(policy => { observed.Add(policy); return 0; });
        var policies = new[] { CefMitigationPolicy.Aslr, CefMitigationPolicy.DynamicCode,
            CefMitigationPolicy.SystemCallDisable, CefMitigationPolicy.ControlFlowGuard,
            CefMitigationPolicy.ImageLoad, CefMitigationPolicy.ChildProcess };
        if (!observed.SequenceEqual(policies) || zero != new CefMitigationSnapshot(0, 0, 0, 0, 0, 0))
            throw new InvalidOperationException("Unexpected mitigation policy query sequence.");
        CefMitigationSnapshot.RequireSame(zero, zero);
        var raw = CefMitigationSnapshot.Read(policy => 0x80000000u | (uint)policy);
        if (raw != new CefMitigationSnapshot(0x80000001u, 0x80000002u, 0x80000004u,
                0x80000007u, 0x8000000Au, 0x8000000Du))
            throw new InvalidOperationException("Raw mitigation flags were altered.");
        foreach (var changed in policies)
        {
            var different = CefMitigationSnapshot.Read(policy => policy == changed ? 1u : 0u);
            try { CefMitigationSnapshot.RequireSame(zero, different); }
            catch (UnauthorizedAccessException) { continue; }
            throw new InvalidOperationException("Changed mitigation was accepted.");
        }
        foreach (var failed in policies)
        {
            try
            {
                _ = CefMitigationSnapshot.Read(policy => policy == failed
                    ? throw new IOException("Synthetic query refusal.") : 0u);
            }
            catch (IOException) { continue; }
            throw new InvalidOperationException("Mitigation query refusal became evidence.");
        }
        Console.WriteLine("PASS: mitigation raw flags, change refusal and failed query refusal (synthetic)");
    }
}
