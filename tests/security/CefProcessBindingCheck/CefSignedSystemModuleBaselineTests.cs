// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Veil.VolumeBroker;

internal static class CefSignedSystemModuleBaselineTests
{
    internal static void Run()
    {
        using var signing = RSA.Create(3072);
        using var wrongKey = RSA.Create(3072);
        using var weakKey = RSA.Create(2048);
        byte[] publicKey = signing.ExportSubjectPublicKeyInfo();
        var host = new CefSystemBaselineHost("x64", 26100, 1234);
        var module = new { name = "kernel32.dll", bytes = 123L,
            sha256 = new string('a', 64), signerSha256 = new string('b', 64) };
        byte[] Payload(int build = 26100, int revision = 1234, long sequence = 7,
            string architecture = "x64", object? modules = null) =>
            JsonSerializer.SerializeToUtf8Bytes(new { schema = "veil-cef-system-modules-v1",
                architecture, build, updateRevision = revision, sequence,
                modules = modules ?? new[] { module } });
        byte[] Sign(byte[] data) => signing.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        void Reject(Action action)
        {
            try { action(); }
            catch (UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("Invalid signed system baseline accepted.");
        }
        void RejectSigned(byte[] data) =>
            Reject(() => CefSignedSystemModuleBaseline.Verify(data, Sign(data), publicKey, host, 7));

        byte[] good = Payload();
        byte[] signature = Sign(good);
        _ = CefSignedSystemModuleBaseline.Verify(good, signature, publicKey, host, 7);
        byte[] changed = (byte[])good.Clone();
        changed[^2] ^= 1;
        Reject(() => CefSignedSystemModuleBaseline.Verify(changed, signature, publicKey, host, 7));
        byte[] changedSignature = (byte[])signature.Clone();
        changedSignature[0] ^= 1;
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, changedSignature, publicKey, host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, signature, wrongKey.ExportSubjectPublicKeyInfo(), host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, signature, [.. publicKey, 0], host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good,
            weakKey.SignData(good, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            weakKey.ExportSubjectPublicKeyInfo(), host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good,
            signing.SignData(good, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            publicKey, host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, signature, publicKey, host, 8));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, signature, publicKey, host, 0));
        RejectSigned(Payload(build: 26101));
        RejectSigned(Payload(revision: 1235));
        RejectSigned(Payload(architecture: "arm64"));
        RejectSigned(Payload(sequence: 6));
        RejectSigned(Payload(modules: Array.Empty<object>()));
        RejectSigned(Payload(modules: new[] { module, module }));
        RejectSigned(Payload(modules: new[] { module, new { name = "KERNEL32.DLL",
            bytes = module.bytes, sha256 = module.sha256, signerSha256 = module.signerSha256 } }));
        RejectSigned(Payload(modules: new[] { new { name = module.name,
            bytes = module.bytes, sha256 = module.sha256, signerSha256 = "bad" } }));
        RejectSigned(Payload(modules: new[] { new { name = module.name,
            bytes = module.bytes, sha256 = module.sha256 } }));
        string json = Encoding.UTF8.GetString(good);
        RejectSigned(Encoding.UTF8.GetBytes(json.Insert(1, "\"sequence\":7,")));
        RejectSigned(Encoding.UTF8.GetBytes(json.Insert(1, "\"publicKey\":\"untrusted\",")));
        RejectSigned(Encoding.UTF8.GetBytes(json.Replace("\"name\":", "\"name\":\"duplicate.dll\",\"name\":")));
        RejectSigned(Encoding.UTF8.GetBytes(json.Replace("\"sequence\":7", "\"sequence\":\"7\"")));
        RejectSigned(Encoding.UTF8.GetBytes(json.Replace("veil-cef-system-modules-v1", "other-schema")));
        RejectSigned(Encoding.UTF8.GetBytes(json[..^1]));
        RejectSigned(new byte[1024 * 1024 + 1]);
        Console.WriteLine("PASS: signed system module baseline, host binding, sequence and malformed input refusal");
    }
}
