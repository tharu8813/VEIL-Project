// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text.Json;

namespace Veil.VolumeBroker;

// Independent trust inputs, not values supplied by the signed document.
// A future protected installation must derive these from the actual host
// and an authenticated release policy. VerifyCurrentHost observes OS metadata;
internal sealed record CefSystemBaselineHost(string Architecture, int Build, int UpdateRevision);

internal static class CefSignedSystemModuleBaseline
{
    private const int MaxPayloadBytes = 1024 * 1024;
    private const int MaxModules = 1024;

    // Use this path for real-host diagnostics, with independently trusted
    // key and sequence floor. Recheck the host before/after verification
    // and every subsequent module inventory check.
    internal static CefSystemModuleVerifier VerifyCurrentHost(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> trustedPublicKey, long minimumSequence)
    {
        var host = CefSystemBaselineHostObserver.Observe();
        void RequireHost() => CefSystemBaselineHostObserver.RequireCurrent(host,
            CefSystemBaselineHostObserver.Observe());
        var verifier = Verify(payload, signature, trustedPublicKey, host, minimumSequence, RequireHost);
        RequireHost();
        return verifier;
    }

    // Detached RSA-PSS/SHA-256 signature of the exact UTF-8 JSON bytes.
    // The public SPKI, host identity and minimum sequence are supplied
    // independently; the payload cannot replace any of them.
    internal static CefSystemModuleVerifier Verify(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> trustedPublicKey, CefSystemBaselineHost expectedHost,
        long minimumSequence, Action? requireHostCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(expectedHost);
        if (expectedHost.Architecture != "x64" || expectedHost.Build <= 0 ||
            expectedHost.UpdateRevision < 0 || minimumSequence <= 0 ||
            payload.Length is 0 or > MaxPayloadBytes ||
            trustedPublicKey.Length is 0 or > 4096 ||
            signature.Length is < 384 or > 512)
            throw new UnauthorizedAccessException("Invalid system baseline trust inputs.");

        // Copy once: the bytes verified are exactly those parsed.
        byte[] document = payload.ToArray();
        using var key = RSA.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(trustedPublicKey, out int consumed);
            if (consumed != trustedPublicKey.Length ||
                key.KeySize is not (3072 or 4096) || signature.Length != key.KeySize / 8 ||
                !key.VerifyData(document, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new UnauthorizedAccessException("System baseline signature refused.");
        }
        catch (CryptographicException e)
        {
            throw new UnauthorizedAccessException("System baseline signature refused.", e);
        }

        try
        {
            using var parsed = JsonDocument.Parse(document, new JsonDocumentOptions { MaxDepth = 5 });
            var root = parsed.RootElement;
            RequireFields(root, "schema", "architecture", "build", "updateRevision", "sequence", "modules");
            if (root.GetProperty("schema").GetString() != "veil-cef-system-modules-v1" ||
                root.GetProperty("architecture").GetString() != expectedHost.Architecture ||
                root.GetProperty("build").GetInt32() != expectedHost.Build ||
                root.GetProperty("updateRevision").GetInt32() != expectedHost.UpdateRevision ||
                root.GetProperty("sequence").GetInt64() < minimumSequence)
                throw new UnauthorizedAccessException("System baseline host or sequence mismatch.");

            var modules = root.GetProperty("modules");
            if (modules.ValueKind != JsonValueKind.Array || modules.GetArrayLength() is 0 or > MaxModules)
                throw new UnauthorizedAccessException("System baseline module count refused.");
            var pins = new List<CefSystemModulePin>(modules.GetArrayLength());
            foreach (var module in modules.EnumerateArray())
            {
                RequireFields(module, "name", "bytes", "sha256", "signerSha256");
                pins.Add(new CefSystemModulePin(
                    module.GetProperty("name").GetString() ?? "",
                    module.GetProperty("bytes").GetInt64(),
                    module.GetProperty("sha256").GetString() ?? "",
                    module.GetProperty("signerSha256").GetString() ?? ""));
            }
            // Existing verifier independently validates every name, length,
            // digest, signer pin and case-insensitive uniqueness.
            return new CefSystemModuleVerifier(pins, requireHostCurrent);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new UnauthorizedAccessException("Malformed authenticated system baseline.", e);
        }
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new UnauthorizedAccessException("Expected system baseline object.");
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!remaining.Remove(field.Name))
                throw new UnauthorizedAccessException("Unknown or duplicate system baseline field.");
        if (remaining.Count != 0)
            throw new UnauthorizedAccessException("Missing system baseline field.");
    }
}
