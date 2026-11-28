// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;

namespace Veil.VolumeBroker;

// Prepared update only: callers must supply independently authorized
// enrollment inputs or a protected installed snapshot. No registry writes,
// automatic enrollment or product admission. Rotation requires independent
// authorization of the exact old/new record pair.
internal enum CefBaselinePublicationStatus
{
    NotStartedEnrollment,
    PartialEnrollmentRequiresReview,
    PreviousRecordPresent,
    IntendedRecordPresentUnconfirmed,
    UnexpectedStateRequiresReview
}

// Independent public-key anchor must come from reviewed release policy,
// not from a candidate approval envelope. No production anchor is embedded.
internal sealed class CefBaselineApprovalAuthority
{
    private readonly byte[] key;
    private readonly string keyId;

    internal CefBaselineApprovalAuthority(ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> independentlyTrustedSpkiSha256)
    {
        if (publicKey.Length is 0 or > 4096 || independentlyTrustedSpkiSha256.Length != 32)
            throw new UnauthorizedAccessException("Missing independent approval authority anchor.");
        key = publicKey.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), independentlyTrustedSpkiSha256))
            throw new UnauthorizedAccessException("Approval authority anchor mismatch.");
        keyId = CefSystemBaselineTrustState.KeyId(key);
    }

    internal void RequireApproval(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> signature,
        CefSystemBaselineTrustState previous, CefSystemBaselineTrustState next)
    {
        if (digest.Length != 32 || signature.Length is < 384 or > 512 ||
            keyId == CefSystemBaselineTrustState.KeyId(previous.CopyPublicKey()) ||
            keyId == CefSystemBaselineTrustState.KeyId(next.CopyPublicKey()) ||
            previous.CopyRetiredKeyIds().Contains(keyId, StringComparer.Ordinal) ||
            next.CopyRetiredKeyIds().Contains(keyId, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Approval authority must be independent of baseline keys.");
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(key, out int consumed);
            if (consumed != key.Length || signature.Length != rsa.KeySize / 8 ||
                !rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new UnauthorizedAccessException("Independent approval signature refused.");
        }
        catch (CryptographicException e)
        {
            throw new UnauthorizedAccessException("Independent approval signature refused.", e);
        }
    }
}

internal sealed class CefSystemBaselineTrustUpdate
{
    private readonly CefSystemBaselineTrustState? previous;
    private readonly CefSystemBaselineTrustState next;
    private readonly CefSystemBaselineHost host;

    private CefSystemBaselineTrustUpdate(CefSystemBaselineTrustState? previous,
        CefSystemBaselineTrustState next, CefSystemBaselineHost host)
    {
        this.previous = previous;
        this.next = next;
        this.host = host;
    }

    internal static CefSystemBaselineTrustUpdate PrepareEnrollment(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> independentKeySha256,
        long independentMinimumSequence, CefSystemBaselineHost host)
    {
        if (publicKey.Length is 0 or > 4096 || independentKeySha256.Length != 32)
            throw new UnauthorizedAccessException("Missing independent enrollment anchor.");
        byte[] key = publicKey.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), independentKeySha256))
            throw new UnauthorizedAccessException("Enrollment public key is not independently trusted.");
        return Prepare(null, payload, signature, key, independentMinimumSequence, host);
    }

    internal static CefSystemBaselineTrustUpdate PrepareReplacement(
        CefSystemBaselineTrustState installed, ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature, CefSystemBaselineHost host)
    {
        ArgumentNullException.ThrowIfNull(installed);
        installed.RequireActive();
        if (installed.MinimumSequence == long.MaxValue)
            throw new UnauthorizedAccessException("Baseline sequence exhausted.");
        // Use only the existing installed key. Key rotation is a separate
        // independently authorized operation, never an ordinary update.
        return Prepare(installed, payload, signature, installed.CopyPublicKey(),
            installed.MinimumSequence + 1, host);
    }

    // Emergency stop does not depend on a signature from the compromised key.
    // The independently authenticated authority approves the exact installed
    // record via a detached RSA-PSS signature over the domain-separated digest.
    internal static CefSystemBaselineTrustUpdate PrepareRevocation(
        CefSystemBaselineTrustState installed,
        CefBaselineApprovalAuthority authority, ReadOnlySpan<byte> approvalSignature,
        CefSystemBaselineHost host)
    {
        ArgumentNullException.ThrowIfNull(installed);
        CefSystemBaselineHostObserver.RequireCurrent(host, host);
        ArgumentNullException.ThrowIfNull(authority);
        var revoked = installed.CreateRevokedRecord();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("VEIL-CEF-BASELINE-KEY-REVOCATION-V1"u8);
        hash.AppendData(SHA256.HashData(installed.CopyRecord()));
        authority.RequireApproval(hash.GetHashAndReset(), approvalSignature, installed, revoked);
        return new(installed, revoked, host);
    }

    // Separate emergency/planned rotation: the old signing key may be
    // compromised, so its signature is not sufficient (or required).
    // Authorization must be obtained independently from the release authority,
    // never computed from candidate input by the maintenance caller itself.
    internal static CefSystemBaselineTrustUpdate PrepareKeyRotation(
        CefSystemBaselineTrustState installed, ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature, ReadOnlySpan<byte> newPublicKey,
        CefBaselineApprovalAuthority authority, ReadOnlySpan<byte> approvalSignature,
        CefSystemBaselineHost host)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(authority);
        if (installed.MinimumSequence == long.MaxValue ||
            newPublicKey.Length is 0 or > 4096)
            throw new UnauthorizedAccessException("Invalid key rotation authorization.");
        byte[] key = newPublicKey.ToArray();
        // Compare mathematical RSA identity, not just the SPKI serialization.
        using var oldRsa = RSA.Create();
        using var newRsa = RSA.Create();
        try
        {
            oldRsa.ImportSubjectPublicKeyInfo(installed.CopyPublicKey(), out _);
            newRsa.ImportSubjectPublicKeyInfo(key, out int consumed);
            if (consumed != key.Length || newRsa.KeySize is not (3072 or 4096))
                throw new UnauthorizedAccessException("Invalid rotation public key.");
            var oldParameters = oldRsa.ExportParameters(false);
            var newParameters = newRsa.ExportParameters(false);
            if (CryptographicOperations.FixedTimeEquals(oldParameters.Modulus!, newParameters.Modulus!))
                throw new UnauthorizedAccessException("Rotation must use a different RSA modulus.");
        }
        catch (CryptographicException e)
        {
            throw new UnauthorizedAccessException("Invalid rotation public key.", e);
        }
        var prepared = Prepare(installed, payload, signature, key,
            installed.MinimumSequence + 1, host);
        // Domain-separated, fixed-length encoding: SHA256(domain ||
        // SHA256(old complete record) || SHA256(new complete record)).
        // The new record binds the new key, signed sequence and exact payload.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("VEIL-CEF-BASELINE-KEY-ROTATION-V1"u8);
        hash.AppendData(SHA256.HashData(installed.CopyRecord()));
        hash.AppendData(SHA256.HashData(prepared.next.CopyRecord()));
        authority.RequireApproval(hash.GetHashAndReset(), approvalSignature, installed, prepared.next);
        return prepared;
    }

    private static CefSystemBaselineTrustUpdate Prepare(CefSystemBaselineTrustState? previous,
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature, byte[] key,
        long floor, CefSystemBaselineHost host)
    {
        if (payload.Length is 0 or > 1048576)
            throw new UnauthorizedAccessException("Invalid baseline update payload size.");
        byte[] document = payload.ToArray();
        var validated = CefSignedSystemModuleBaseline.VerifyWithSequence(document, signature, key, host, floor);
        var next = previous is null
            ? CefSystemBaselineTrustState.CreateVerifiedRecord(document, key, validated.Sequence)
            : previous.CreateSuccessor(document, key, validated.Sequence);
        return new(previous, next, host);
    }

    // Must be called under the single administrator writer transaction,
    // immediately before persistence. This check alone is NOT an atomic CAS.
    internal byte[] RequireExpectedAndCopyRecord(CefSystemBaselineTrustState? current,
        CefSystemBaselineHost currentHost)
    {
        CefSystemBaselineHostObserver.RequireCurrent(host, currentHost);
        if (previous is null)
        {
            if (current is not null)
                throw new UnauthorizedAccessException("Baseline enrollment would replace existing trust.");
        }
        else
        {
            if (current is null)
                throw new UnauthorizedAccessException("Installed baseline trust disappeared.");
            previous.RequireSame(current);
        }
        return next.CopyRecord();
    }

    internal CefBaselinePublicationStatus ClassifyPublication(bool keyExists,
        CefSystemBaselineTrustState? observed, CefSystemBaselineHost currentHost)
    {
        CefSystemBaselineHostObserver.RequireCurrent(host, currentHost);
        if (!keyExists && observed is not null)
            throw new UnauthorizedAccessException("Inconsistent baseline observation.");
        if (observed is null)
        {
            if (previous is not null)
                return CefBaselinePublicationStatus.UnexpectedStateRequiresReview;
            return keyExists ? CefBaselinePublicationStatus.PartialEnrollmentRequiresReview
                : CefBaselinePublicationStatus.NotStartedEnrollment;
        }
        if (next.Matches(observed))
            return CefBaselinePublicationStatus.IntendedRecordPresentUnconfirmed;
        if (previous?.Matches(observed) == true)
            return CefBaselinePublicationStatus.PreviousRecordPresent;
        return CefBaselinePublicationStatus.UnexpectedStateRequiresReview;
    }

    internal void RequirePublished(CefSystemBaselineTrustState published,
        CefSystemBaselineHost currentHost)
    {
        CefSystemBaselineHostObserver.RequireCurrent(host, currentHost);
        next.RequireSame(published);
    }
}
