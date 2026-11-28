// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;

namespace Veil.VolumeBroker;

// Prepared update only: callers must supply independently authorized
// enrollment inputs or a protected installed snapshot. No registry writes,
// automatic enrollment, key rotation or product admission.
internal enum CefBaselinePublicationStatus
{
    NotStartedEnrollment,
    PartialEnrollmentRequiresReview,
    PreviousRecordPresent,
    IntendedRecordPresentUnconfirmed,
    UnexpectedStateRequiresReview
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
        if (installed.MinimumSequence == long.MaxValue)
            throw new UnauthorizedAccessException("Baseline sequence exhausted.");
        // Use only the existing installed key. Key rotation is a separate
        // independently authorized operation, never an ordinary update.
        return Prepare(installed, payload, signature, installed.CopyPublicKey(),
            installed.MinimumSequence + 1, host);
    }

    private static CefSystemBaselineTrustUpdate Prepare(CefSystemBaselineTrustState? previous,
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature, byte[] key,
        long floor, CefSystemBaselineHost host)
    {
        if (payload.Length is 0 or > 1048576)
            throw new UnauthorizedAccessException("Invalid baseline update payload size.");
        byte[] document = payload.ToArray();
        var validated = CefSignedSystemModuleBaseline.VerifyWithSequence(document, signature, key, host, floor);
        var next = CefSystemBaselineTrustState.CreateVerifiedRecord(document, key, validated.Sequence);
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
