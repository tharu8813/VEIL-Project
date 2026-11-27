// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using Veil.Desktop;

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
    { return; }
    throw new InvalidOperationException("Expected runtime refusal.");
}

// Real Windows file handles and hash verification. Admin ACL and signed
// pin provenance are deliberately not exercised on the hosted test runner.
string root = Path.Combine(Path.GetTempPath(), "veil-runtime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var pins = new List<CefRuntimeFilePin>();
    foreach (string name in new[] { "veil-cef-product.exe", "veil-cef-product.dll", "libcef.dll", "icudtl.dat", "locales/en-US.pak" })
    {
        string path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("synthetic " + name);
        File.WriteAllBytes(path, bytes);
        pins.Add(new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
    }
    string registration = System.Text.Json.JsonSerializer.Serialize(new
    {
        kind = "cef-admin-runtime-pins", format = 1, architecture = "win-x64",
        sourceCommit = new string('a', 40),
        cefArchiveSha256 = "1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a",
        startupEnabled = false,
        files = pins.Select(p => new { path = p.Path, bytes = p.Bytes, sha256 = p.Sha256 })
    });
    IReadOnlyList<CefRuntimeFilePin> Parse(string json) =>
        CefRuntimeRegistrationLease.Parse(System.Text.Encoding.UTF8.GetBytes(json));
    Check(Parse(registration).SequenceEqual(pins), "Runtime registration roundtrip failed.");
    foreach (string bad in new[] {
        registration.Replace("\"format\":1", "\"format\":1,\"format\":1"),
        registration.Replace("\"format\":1", "\"format\":1.0"),
        registration.Replace("\"startupEnabled\":false", "\"startupEnabled\":true"),
        registration.Replace("cef-admin-runtime-pins", "cef-signed-candidate-evidence"),
        registration.Replace("win-x64", "win-arm64"),
        registration.Replace("veil-cef-product.exe", "../outside.exe"),
        registration.Replace("libcef.dll", "veil-cef-product.dll"),
        registration.Replace("icudtl.dat", "other.dat"),
        registration.Replace("\"bytes\":", "\"extra\":0,\"bytes\":"),
        registration.Replace(new string('a',40), "bad"),
        registration.Replace("1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a", new string('0',64))
    })
        Reject(() => Parse(bad));
    Console.WriteLine("PASS: strict disabled runtime registration parsing");
    CefRuntimeFileLease Open(IEnumerable<CefRuntimeFilePin> input) => CefRuntimeFileLease.Open(root, input, _ => { });
    using (var lease = Open(pins))
    {
        Check(lease.ImagePath == Path.Combine(root, "veil-cef-product.exe"), "Wrong image path.");
        lease.RequireCurrent();
        string dll = Path.Combine(root, "libcef.dll");
        lease.RequireObservedApplicationModule(dll);
        lease.RequireObservedApplicationModule(lease.ImagePath);
        string system = Environment.SystemDirectory;
        string systemDll = Path.Combine(system, "kernel32.dll");
        string[] modules = [lease.ImagePath, dll, systemDll];
        Veil.VolumeBroker.CefModuleInventoryObservation Verify(string[] before, string[]? after = null) =>
            Veil.VolumeBroker.CefModuleInventoryPolicy.Verify(lease.ImagePath, system, before,
                lease.RequireObservedApplicationModules, () => after ?? before);
        var observed = Verify(modules, modules.Reverse().ToArray());
        Check(observed.ApplicationModules == 2 && observed.UnverifiedSystemModules == 1,
            "System DLL incorrectly counted as a pinned application module.");
        int systemChecks = 0;
        var withSystemVerifier = Veil.VolumeBroker.CefModuleInventoryPolicy.Verify(
            lease.ImagePath, system, modules, lease.RequireObservedApplicationModules,
            () => modules, paths =>
            {
                Check(paths.SequenceEqual(new[] { systemDll }), "Incorrect system verification input.");
                systemChecks++;
            });
        Check(systemChecks == 1 && withSystemVerifier.ApplicationModules == 2 &&
            withSystemVerifier.UnverifiedSystemModules == 0 && withSystemVerifier.PinnedSignedSystemModules == 1,
            "System verification result not separated from application pins.");
        Reject(() => Veil.VolumeBroker.CefModuleInventoryPolicy.Verify(
            lease.ImagePath, system, modules, lease.RequireObservedApplicationModules,
            () => modules, _ => throw new UnauthorizedAccessException("Synthetic signature refusal.")));
        Reject(() => Veil.VolumeBroker.CefModuleInventoryPolicy.Verify(
            lease.ImagePath, system, modules, lease.RequireObservedApplicationModules,
            () => [lease.ImagePath, dll], _ => { }));

        Reject(() => Verify([lease.ImagePath, dll, dll]));
        Reject(() => Verify([lease.ImagePath, systemDll]));
        Reject(() => Verify([dll, systemDll]));
        Reject(() => Verify([lease.ImagePath, dll, Path.Combine(root, "unregistered.dll")]));
        Reject(() => Verify([lease.ImagePath, dll, Path.Combine(root, "icudtl.dat")]));
        Reject(() => Verify([lease.ImagePath, dll, Path.Combine(system, "sub", "kernel32.dll")]));
        Reject(() => Verify([lease.ImagePath, dll, Path.Combine(Path.GetTempPath(), "outside.dll")]));
        Reject(() => Verify([lease.ImagePath, dll, systemDll + ":alternate"]));
        Reject(() => Verify(modules, [lease.ImagePath, dll]));
        Reject(() => Verify(modules, [lease.ImagePath, dll, dll]));
        Reject(() => Verify(modules, [lease.ImagePath, dll, Path.Combine(system, "other.dll")]));
        Console.WriteLine("PASS: observed module inventory, retained pins and unverified system separation");
        Reject(() => lease.RequireObservedApplicationModule(Path.Combine(root, "icudtl.dat")));
        Reject(() => lease.RequireObservedApplicationModule(Path.Combine(root, "unregistered.dll")));
        Reject(() => lease.RequireObservedApplicationModule(Path.Combine(root, "locales", "en-US.pak")));
        Reject(() => lease.RequireObservedApplicationModule(Path.Combine(Path.GetTempPath(), "libcef.dll")));
        Reject(() => File.AppendAllText(dll, "modified"));
        Reject(() => File.Delete(dll));
        string extra = Path.Combine(root, "extra.dll");
        File.WriteAllText(extra, "synthetic");
        Reject(lease.RequireCurrent);
        File.Delete(extra);
        lease.RequireCurrent();
    }
    using (var brokerLease = CefRuntimeFileLease.Open(root, pins, _ => { }, serializedBrokerAccess: true))
        Task.Run(() => brokerLease.RequireObservedApplicationModules([
            Path.Combine(root, "libcef.dll"), Path.Combine(root, "veil-cef-product.exe")
        ])).GetAwaiter().GetResult();
    using (var desktopLease = Open(pins))
        Task.Run(() => Reject(desktopLease.RequireCurrent)).GetAwaiter().GetResult();
    Reject(() => Open(pins.Append(pins[0])).Dispose());
    Reject(() => Open(pins.Select(p => p.Path == "libcef.dll" ? p with { Sha256 = new string('0', 64) } : p)).Dispose());
    Reject(() => Open(pins.Where(p => p.Path != "libcef.dll")).Dispose());
    Reject(() => Open(pins.Append(new CefRuntimeFilePin("../outside.dll", 1, new string('0',64)))).Dispose());
    File.AppendAllText(Path.Combine(root, "libcef.dll"), "modified");
    Reject(() => Open(pins).Dispose());
    // Failure must release already retained handles.
    File.Delete(Path.Combine(root, "veil-cef-product.exe"));
    Console.WriteLine("PASS: synthetic Windows runtime inventory, digest, file-share and refusal checks");
    return 0;
}
finally { Directory.Delete(root, recursive: true); }
