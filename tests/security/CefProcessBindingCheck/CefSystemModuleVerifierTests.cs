// SPDX-License-Identifier: MPL-2.0
using Veil.VolumeBroker;

internal static class CefSystemModuleVerifierTests
{
    internal static void Run()
    {
        string system = Environment.SystemDirectory;
        string path = Path.Combine(system, "kernel32.dll");
        var pin = new CefSystemModulePin("kernel32.dll", new FileInfo(path).Length, new string('0', 64));
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or IOException) { return; }
            throw new InvalidOperationException("Invalid system module baseline accepted.");
        }
        Reject(() => new CefSystemModuleVerifier([]));
        Reject(() => new CefSystemModuleVerifier([pin, pin with { Name = "KERNEL32.DLL" }]));
        Reject(() => new CefSystemModuleVerifier([pin with { Name = "../kernel32.dll" }]));
        Reject(() => new CefSystemModuleVerifier([pin with { Name = "kernel32.exe" }]));
        Reject(() => new CefSystemModuleVerifier([pin with { Bytes = 0 }]));
        Reject(() => new CefSystemModuleVerifier([pin with { Sha256 = "bad" }]));
        var verifier = new CefSystemModuleVerifier([pin]);
        Reject(() => verifier.RequireModules([Path.Combine(system, "unregistered.dll")]));
        Reject(() => verifier.RequireModules([Path.Combine(Path.GetTempPath(), "kernel32.dll")]));
        Reject(() => verifier.RequireModules([path + ":alternate"]));
        // Real system file with intentionally wrong pin must never pass.
        // This test does not establish its signer or a usable OS baseline.
        Reject(() => verifier.RequireModules([path]));
        // Exercise the real native trust calls independently of the ACL/pin
        // rejection path. No catalog is installed or modified by this test.
        string unsigned = Path.Combine(Path.GetTempPath(), "veil-unsigned-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllText(unsigned, "synthetic unsigned file");
            using var file = new FileStream(unsigned, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (CefSystemSignatureTrust.Verify(unsigned, file.SafeFileHandle))
                throw new InvalidOperationException("Unsigned synthetic module was trusted.");
        }
        finally { File.Delete(unsigned); }
        using (var invalid = new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false))
            if (CefSystemSignatureTrust.Verify(path, invalid))
                throw new InvalidOperationException("Invalid module handle was trusted.");
        // Hosted image compatibility diagnostic only, not a required success
        // or a replacement for an independently authenticated OS baseline.
        using (var real = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Console.WriteLine("DIAGNOSTIC: kernel32 Microsoft-root signature = " +
                CefSystemSignatureTrust.Verify(path, real.SafeFileHandle));
        Console.WriteLine("PASS: system module baseline, wrong-pin and unsigned trust refusal");
    }
}
