// SPDX-License-Identifier: MPL-2.0
using Veil.VolumeBroker;

internal static class CefCommandRoleTests
{
    internal static void Run()
    {
        void Expect(CefObservedRole role, bool root, params string[] switches)
        {
            string[] argv = ["cef.exe", .. switches];
            if (CefCommandRoleObserver.Classify(argv, root) != role)
                throw new InvalidOperationException("Unexpected CEF command role.");
        }
        const CefObservedRole unknown = CefObservedRole.Unknown;
        Expect(CefObservedRole.Browser, true);
        Expect(CefObservedRole.Renderer, false, "--type=renderer", "/prefetch:1");
        Expect(CefObservedRole.Gpu, false, "--type=gpu-process");
        Expect(CefObservedRole.Network, false, "--type=utility",
            "--utility-sub-type=network.mojom.NetworkService", "--service-sandbox-type=network");
        Expect(CefObservedRole.Utility, false, "--type=utility", "--utility-sub-type=audio.mojom.AudioService");
        Expect(unknown, false);
        Expect(unknown, true, "--type=renderer");
        Expect(unknown, false, "--type=unknown");
        Expect(unknown, false, "--type=utility");
        Expect(unknown, false, "--type=renderer", "--type=renderer");
        Expect(unknown, false, "--TYPE=renderer");
        Expect(unknown, false, "/type=renderer");
        Expect(unknown, false, "--type", "renderer");
        Expect(unknown, false, "--type=renderer", "--utility-sub-type=x");
        Expect(unknown, false, "--type=utility", "--utility-sub-type=x", "--utility-sub-type=y");
        Expect(unknown, false, "--type=utility", "--utility-sub-type=x",
            "--service-sandbox-type=network", "--service-sandbox-type=network");
        foreach (string option in new[] {
            "--no-sandbox", "--NO-SANDBOX=false", "/no-sandbox", "-no-sandbox",
            "--disable-gpu-sandbox", "--disable-renderer-sandbox", "--single-process",
            "--in-process-gpu", "--disable-win32k-lockdown", "--allow-no-sandbox-job",
            "--disable-features=NetworkServiceSandbox", "--disable-features=RendererAppContainer",
            "--", "unexpected-positional"
        })
        {
            Expect(unknown, true, option);
            Expect(unknown, false, "--type=renderer", option);
        }
        foreach (string sandbox in new[] { "none", "no-sandbox", "unsandboxed", "none_and_elevated" })
            Expect(unknown, false, "--type=utility", "--utility-sub-type=x", "--service-sandbox-type=" + sandbox);
        Console.WriteLine("PASS: CEF role classification and known unsafe switch refusal");
    }
}
