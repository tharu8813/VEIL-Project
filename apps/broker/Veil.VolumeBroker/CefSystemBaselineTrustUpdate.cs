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

    internal byte[] CopyPublicKey() => (byte[])key.Clone();

    internal static CefBaselineApprovalAuthority ParseActiveRecord(ReadOnlySpan<byte> record)
    {
        if (record.StartsWith("VEILAPR1"u8))
            throw new UnauthorizedAccessException("Baseline approval authority is stopped.");
        return new CefBaselineApprovalAuthority(record, SHA256.HashData(record));
    }

    // A stop is a one-way local administrative operation, not an approval
    // signed by a possibly compromised authority. Preserve the exact SPKI.
    internal byte[] CreateStoppedRecord() => [.. "VEILAPR1"u8, .. key];

    internal void RequireStoppedRecord(ReadOnlySpan<byte> record)
    {
        if (!CryptographicOperations.FixedTimeEquals(CreateStoppedRecord(), record))
            throw new UnauthorizedAccessException("Stopped approval authority record mismatch.");
    }

    // Initial authority provisioning is allowed only before either machine
    // record exists. Partial keys are not absence. Never use this for recovery.
    internal void RequireFreshInstallation(bool authorityExists, bool baselineExists)
    {
        if (authorityExists || baselineExists)
            throw new UnauthorizedAccessException("Existing approval or baseline registration requires review.");
    }

    internal void RequireSame(CefBaselineApprovalAuthority current)
    {
        if (current is null || !CryptographicOperations.FixedTimeEquals(key, current.key))
            throw new UnauthorizedAccessException("Baseline approval authority changed.");
    }

    internal void RequireIndependent(CefSystemBaselineTrustState state)
    {
        if (keyId == CefSystemBaselineTrustState.KeyId(state.CopyPublicKey()) ||
            state.CopyRetiredKeyIds().Contains(keyId, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Approval authority must be independent of baseline keys.");
    }

    internal void RequireApproval(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> signature,
        CefSystemBaselineTrustState previous, CefSystemBaselineTrustState next)
    {
        RequireIndependent(previous);
        RequireIndependent(next);
        if (digest.Length != 32 || signature.Length is < 384 or > 512)
            throw new UnauthorizedAccessException("Invalid approval signature size.");
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
    private readonly CefBaselineApprovalAuthority? approvedBy;

    private CefSystemBaselineTrustUpdate(CefSystemBaselineTrustState? previous,
        CefSystemBaselineTrustState next, CefSystemBaselineHost host,
        CefBaselineApprovalAuthority? approvedBy = null)
    {
        this.previous = previous;
        this.next = next;
        this.host = host;
        this.approvedBy = approvedBy;
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

    // Initial diagnostic enrollment additionally pins the exact release
    // document and sequence and binds the independently anchored authority.
    internal static CefSystemBaselineTrustUpdate PrepareDiagnosticEnrollment(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> independentKeySha256,
        ReadOnlySpan<byte> independentPayloadSha256, long independentSequence,
        CefSystemBaselineHost host, CefBaselineApprovalAuthority authority)
    {
        if (payload.Length is 0 or > 1048576 || independentPayloadSha256.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), independentPayloadSha256))
            throw new UnauthorizedAccessException("Independent diagnostic payload digest mismatch.");
        var prepared = PrepareEnrollment(payload, signature, publicKey, independentKeySha256,
            independentSequence, host);
        if (prepared.next.MinimumSequence != independentSequence)
            throw new UnauthorizedAccessException("Independent diagnostic sequence mismatch.");
        authority.RequireIndependent(prepared.next);
        return new(null, prepared.next, host, authority);
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
        return new(installed, revoked, host, authority);
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
        return new(installed, prepared.next, host, authority);
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

    // All persistent publications require an independently provisioned
    // authority, including enrollment and ordinary replacement. Rotation
    // and revocation additionally bind the exact authority used to approve.
    internal void RequirePublicationAuthority(CefBaselineApprovalAuthority current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is not null) current.RequireIndependent(previous);
        current.RequireIndependent(next);
        approvedBy?.RequireSame(current);
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


// Deliberately disconnected recovery plan and ordered maintenance transaction. The offline recovery anchor is
// obtained out of band from independent release policy, never from the
// compromised authority or candidate package. No product admission here.
internal enum CefApprovalRecoveryStatus
{
    StoppedOriginal,
    StoppedBaselineReplaced,
    IntendedPairPresentUnconfirmed,
    UnexpectedRequiresReview
}

internal sealed class CefApprovalAuthorityRecovery
{
    private readonly byte[] stoppedAuthority;
    private readonly byte[] newAuthority;
    private readonly CefSystemBaselineTrustState previous;
    private readonly CefSystemBaselineTrustState next;
    private readonly CefSystemBaselineHost host;

    private CefApprovalAuthorityRecovery(byte[] stoppedAuthority, byte[] newAuthority,
        CefSystemBaselineTrustState previous, CefSystemBaselineTrustState next, CefSystemBaselineHost host)
    {
        this.stoppedAuthority = stoppedAuthority;
        this.newAuthority = newAuthority;
        this.previous = previous;
        this.next = next;
        this.host = host;
    }

    internal static CefApprovalAuthorityRecovery Prepare(
        ReadOnlySpan<byte> stoppedAuthorityRecord, CefSystemBaselineTrustState installed,
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> baselineSignature,
        ReadOnlySpan<byte> newBaselinePublicKey, ReadOnlySpan<byte> newApprovalPublicKey,
        ReadOnlySpan<byte> recoveryPublicKey, ReadOnlySpan<byte> independentRecoverySpkiSha256,
        ReadOnlySpan<byte> recoverySignature, CefSystemBaselineHost host)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (stoppedAuthorityRecord.Length is <= 8 or > 4104 ||
            !stoppedAuthorityRecord.StartsWith("VEILAPR1"u8) ||
            installed.MinimumSequence == long.MaxValue ||
            payload.Length is 0 or > 1048576 ||
            newBaselinePublicKey.Length is 0 or > 4096 ||
            newApprovalPublicKey.Length is 0 or > 4096)
            throw new UnauthorizedAccessException("Invalid authority recovery inputs.");

        byte[] stopped = stoppedAuthorityRecord.ToArray();
        byte[] oldApprovalKey = stopped[8..];
        byte[] baselineKey = newBaselinePublicKey.ToArray();
        byte[] approvalKey = newApprovalPublicKey.ToArray();
        byte[] document = payload.ToArray();
        var recovery = new CefBaselineApprovalAuthority(recoveryPublicKey, independentRecoverySpkiSha256);
        string recoveryId = CefSystemBaselineTrustState.KeyId(recovery.CopyPublicKey());
        string oldApprovalId = CefSystemBaselineTrustState.KeyId(oldApprovalKey);
        string oldBaselineId = CefSystemBaselineTrustState.KeyId(installed.CopyPublicKey());
        string baselineId = CefSystemBaselineTrustState.KeyId(baselineKey);
        string approvalId = CefSystemBaselineTrustState.KeyId(approvalKey);
        var excluded = new HashSet<string>(installed.CopyRetiredKeyIds(), StringComparer.Ordinal);
        excluded.Add(oldApprovalId);
        excluded.Add(oldBaselineId);
        if (!excluded.Add(baselineId) || !excluded.Add(approvalId) || !excluded.Add(recoveryId))
            throw new UnauthorizedAccessException("Recovery key roles must be distinct and not retired.");

        var validated = CefSignedSystemModuleBaseline.VerifyWithSequence(document, baselineSignature,
            baselineKey, host, installed.MinimumSequence + 1);
        // Carry both compromised key identities in the shared retired RSA
        // history, so neither can return as a baseline or approval key.
        var history = installed.CopyRetiredKeyIds().Append(oldBaselineId).Append(oldApprovalId)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var replacement = CefSystemBaselineTrustState.CreateVerifiedRecord(document, baselineKey,
            validated.Sequence, history);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("VEIL-CEF-APPROVAL-RECOVERY-V1"u8);
        hash.AppendData(SHA256.HashData(stopped));
        hash.AppendData(SHA256.HashData(installed.CopyRecord()));
        hash.AppendData(SHA256.HashData(approvalKey));
        hash.AppendData(SHA256.HashData(replacement.CopyRecord()));
        recovery.RequireApproval(hash.GetHashAndReset(), recoverySignature, installed, replacement);
        return new(stopped, approvalKey, installed, replacement, host);
    }

    internal CefApprovalRecoveryStatus Classify(ReadOnlySpan<byte> authorityRecord,
        CefSystemBaselineTrustState? baseline, CefSystemBaselineHost currentHost)
    {
        CefSystemBaselineHostObserver.RequireCurrent(host, currentHost);
        if (CryptographicOperations.FixedTimeEquals(stoppedAuthority, authorityRecord))
        {
            if (previous.Matches(baseline)) return CefApprovalRecoveryStatus.StoppedOriginal;
            if (next.Matches(baseline)) return CefApprovalRecoveryStatus.StoppedBaselineReplaced;
        }
        if (CryptographicOperations.FixedTimeEquals(newAuthority, authorityRecord) && next.Matches(baseline))
            return CefApprovalRecoveryStatus.IntendedPairPresentUnconfirmed;
        return CefApprovalRecoveryStatus.UnexpectedRequiresReview;
    }

    // Persistent transaction must hold the common writer lock. Baseline first,
    // while the approval authority is still stopped; then verify/flush both
    // records before publishing the new active authority LAST.
    internal byte[] RequireBaselineStage(ReadOnlySpan<byte> authorityRecord,
        CefSystemBaselineTrustState? baseline, CefSystemBaselineHost currentHost)
    {
        if (Classify(authorityRecord, baseline, currentHost) != CefApprovalRecoveryStatus.StoppedOriginal)
            throw new UnauthorizedAccessException("Unexpected original recovery state.");
        return next.CopyRecord();
    }

    // Caller must hold the shared writer lock for the entire operation.
    // Explicit restart is allowed only with the exact same prepared plan.
    // No catch-and-retry, rollback, deletion or activation on uncertain data.
    internal void ExecuteOrdered(
        Func<(byte[] Authority, CefSystemBaselineTrustState Baseline, CefSystemBaselineHost Host)> read,
        Action<byte[]> writeBaseline, Action flushBaseline,
        Action<byte[]> writeAuthority, Action flushAuthority)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(writeBaseline);
        ArgumentNullException.ThrowIfNull(flushBaseline);
        ArgumentNullException.ThrowIfNull(writeAuthority);
        ArgumentNullException.ThrowIfNull(flushAuthority);

        CefApprovalRecoveryStatus Status() {
            var observed = read();
            return Classify(observed.Authority, observed.Baseline, observed.Host);
        }
        void RequireStatus(CefApprovalRecoveryStatus expected) {
            if (Status() != expected)
                throw new UnauthorizedAccessException("Recovery record pair changed.");
        }

        var initial = Status();
        if (initial == CefApprovalRecoveryStatus.UnexpectedRequiresReview)
            throw new UnauthorizedAccessException("Unexpected authority recovery state.");
        if (initial == CefApprovalRecoveryStatus.IntendedPairPresentUnconfirmed)
        {
            // Confirmation only: do not rewrite an already active pair.
            flushBaseline();
            RequireStatus(initial);
            flushAuthority();
            RequireStatus(initial);
            return;
        }

        // Ensure the stopped record is flushed before any baseline write.
        flushAuthority();
        RequireStatus(initial);
        if (initial == CefApprovalRecoveryStatus.StoppedOriginal)
        {
            var current = read();
            writeBaseline(RequireBaselineStage(current.Authority, current.Baseline, current.Host));
            RequireStatus(CefApprovalRecoveryStatus.StoppedBaselineReplaced);
        }

        // Also flush when resuming the exact intermediate pair: a previous
        // run may have stopped after SetValue but before successful flush.
        flushBaseline();
        RequireStatus(CefApprovalRecoveryStatus.StoppedBaselineReplaced);
        var intermediate = read();
        writeAuthority(RequireAuthorityStage(intermediate.Authority, intermediate.Baseline, intermediate.Host));
        RequireStatus(CefApprovalRecoveryStatus.IntendedPairPresentUnconfirmed);
        flushAuthority();
        RequireStatus(CefApprovalRecoveryStatus.IntendedPairPresentUnconfirmed);
    }

    internal byte[] RequireAuthorityStage(ReadOnlySpan<byte> authorityRecord,
        CefSystemBaselineTrustState? baseline, CefSystemBaselineHost currentHost)
    {
        if (Classify(authorityRecord, baseline, currentHost) != CefApprovalRecoveryStatus.StoppedBaselineReplaced)
            throw new UnauthorizedAccessException("Replacement baseline not present under stopped authority.");
        return (byte[])newAuthority.Clone();
    }
}
