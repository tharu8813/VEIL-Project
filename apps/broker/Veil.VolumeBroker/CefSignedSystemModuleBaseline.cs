// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text.Json;
using Veil.Shared;

namespace Veil.VolumeBroker;

// Independent trust inputs, not values supplied by the signed document.
// A future protected installation must derive these from the actual host
// and an authenticated release policy. VerifyCurrentHost observes OS metadata;
internal sealed record CefSystemBaselineHost(string Architecture, int Build, int UpdateRevision);

internal static class CefSignedSystemModuleBaseline
{
    private const int MaxPayloadBytes = 1024 * 1024;
    private const int MaxModules = 1024;

    // Fixed installed input for the disconnected terminal diagnostic.
    // Never accept a client path, caller key, sequence or registry fallback.
    // Reading these bytes is NOT trust: VerifyInstalledTrust is mandatory.
    internal static (byte[] Payload, byte[] Signature) ReadInstalledDiagnosticInputs()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "VEIL", "Broker");
        if (!string.Equals(Environment.ProcessPath, Path.Combine(root, "Veil.VolumeBroker.exe"),
            StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF diagnostic requires installed broker.");
        string payloadPath = Path.Combine(root, "cef-system-baseline.json");
        string signaturePath = Path.Combine(root, "cef-system-baseline.sig");
        using var payload = OpenPinnedInput(payloadPath);
        using var signature = OpenPinnedInput(signaturePath);
        byte[] document = ReadBoundedInput(payload, 1, MaxPayloadBytes);
        byte[] detached = ReadBoundedInput(signature, 384, 512);
        if (detached.Length is not (384 or 512))
            throw new UnauthorizedAccessException("Invalid baseline signature size.");
        RequireInputPin(payload, payloadPath);
        RequireInputPin(signature, signaturePath);
        return (document, detached);
    }

    private static FileStream OpenPinnedInput(string path)
    {
        AdminOnlyFile.RequireProtectedExecutable(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            RequireInputPin(file, path);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static void RequireInputPin(FileStream file, string path)
    {
        AdminOnlyFile.RequireProtectedExecutable(path);
        Native.RequireCanonicalSingleLinkImage(file.SafeFileHandle, path);
    }

    // Bounded exact byte snapshot; no parsing, normalization or trust here.
    internal static byte[] ReadBoundedInput(Stream input, int minimum, int maximum)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (minimum <= 0 || maximum < minimum || !input.CanRead || !input.CanSeek)
            throw new UnauthorizedAccessException("Invalid baseline input bounds.");
        long length = input.Length;
        if (length < minimum || length > maximum)
            throw new UnauthorizedAccessException("Invalid baseline input length.");
        input.Position = 0;
        byte[] bytes = new byte[checked((int)length)];
        input.ReadExactly(bytes);
        if (input.Length != length || input.ReadByte() != -1)
            throw new UnauthorizedAccessException("Baseline input length changed.");
        return bytes;
    }

    // Read-only installed trust path. Not connected to broker IPC/startup.
    internal static CefSystemModuleVerifier VerifyInstalledTrust(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        var installed = CefSystemBaselineTrustRegistry.ReadStable();
        var authority = CefSystemBaselineTrustRegistry.ReadApprovalAuthorityStable();
        return VerifyWithApprovalSnapshot(payload, signature, installed,
            CefSystemBaselineTrustRegistry.ReadStable, authority,
            CefSystemBaselineTrustRegistry.ReadApprovalAuthorityStable);
    }

    internal static CefSystemModuleVerifier VerifyWithApprovalSnapshot(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature,
        CefSystemBaselineTrustState installed, Func<CefSystemBaselineTrustState> readCurrent,
        CefBaselineApprovalAuthority authority, Func<CefBaselineApprovalAuthority> readAuthority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(readAuthority);
        authority.RequireIndependent(installed);
        return VerifyTrustedSnapshot(payload, signature, installed, () =>
        {
            authority.RequireSame(readAuthority());
            return readCurrent();
        });
    }

    internal static CefSystemModuleVerifier VerifyTrustedSnapshot(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature,
        CefSystemBaselineTrustState installed, Func<CefSystemBaselineTrustState> readCurrent)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(readCurrent);
        if (payload.Length is 0 or > MaxPayloadBytes)
            throw new UnauthorizedAccessException("Invalid system baseline payload size.");
        byte[] document = payload.ToArray();
        installed.RequirePayload(document);
        void RequireTrust()
        {
            var current = readCurrent();
            installed.RequireSame(current);
            current.RequireActive();
        }
        RequireTrust();
        return VerifyCurrentHost(document, signature, installed.CopyPublicKey(),
            installed.MinimumSequence, RequireTrust, requireExactSequence: true);
    }

    // Use this path for real-host diagnostics, with independently trusted
    // key and sequence floor. Recheck the host before/after verification
    // and every subsequent module inventory check.
    internal static CefSystemModuleVerifier VerifyCurrentHost(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> trustedPublicKey, long minimumSequence, Action? requireTrustCurrent = null,
        bool requireExactSequence = false)
    {
        var host = CefSystemBaselineHostObserver.Observe();
        void RequireHost()
        {
            requireTrustCurrent?.Invoke();
            CefSystemBaselineHostObserver.RequireCurrent(host, CefSystemBaselineHostObserver.Observe());
        }
        var verifier = VerifyWithSequence(payload, signature, trustedPublicKey, host, minimumSequence, RequireHost);
        if (requireExactSequence && verifier.Sequence != minimumSequence)
            throw new UnauthorizedAccessException("Installed baseline sequence mismatch.");
        RequireHost();
        return verifier.Verifier;
    }

    // Detached RSA-PSS/SHA-256 signature of the exact UTF-8 JSON bytes.
    // The public SPKI, host identity and minimum sequence are supplied
    // independently; the payload cannot replace any of them.
    internal static CefSystemModuleVerifier Verify(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> trustedPublicKey, CefSystemBaselineHost expectedHost,
        long minimumSequence, Action? requireHostCurrent = null) =>
        VerifyWithSequence(payload, signature, trustedPublicKey, expectedHost,
            minimumSequence, requireHostCurrent).Verifier;

    internal static (CefSystemModuleVerifier Verifier, long Sequence) VerifyWithSequence(
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
            long sequence = root.GetProperty("sequence").GetInt64();
            if (root.GetProperty("schema").GetString() != "veil-cef-system-modules-v1" ||
                root.GetProperty("architecture").GetString() != expectedHost.Architecture ||
                root.GetProperty("build").GetInt32() != expectedHost.Build ||
                root.GetProperty("updateRevision").GetInt32() != expectedHost.UpdateRevision ||
                sequence < minimumSequence)
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
            return (new CefSystemModuleVerifier(pins, requireHostCurrent), sequence);
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
