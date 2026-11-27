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
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
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
    CefRuntimeFileLease Open(IEnumerable<CefRuntimeFilePin> input) => CefRuntimeFileLease.Open(root, input, _ => { });
    using (var lease = Open(pins))
    {
        Check(lease.ImagePath == Path.Combine(root, "veil-cef-product.exe"), "Wrong image path.");
        lease.RequireCurrent();
        string dll = Path.Combine(root, "libcef.dll");
        Reject(() => File.AppendAllText(dll, "modified"));
        Reject(() => File.Delete(dll));
        string extra = Path.Combine(root, "extra.dll");
        File.WriteAllText(extra, "synthetic");
        Reject(lease.RequireCurrent);
        File.Delete(extra);
        lease.RequireCurrent();
    }
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
