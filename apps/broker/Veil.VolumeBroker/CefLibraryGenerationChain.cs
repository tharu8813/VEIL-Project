// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Veil.VolumeBroker;

// Prospective authenticated content model, not a disk format or key store.
// The caller must independently protect the per-profile key, store identity,
// and rollback anchor. No file or runtime authority is granted here.
internal sealed record CefLibraryGenerationRecord(int Generation, byte[] PreviousTag,
    byte[] Payload, byte[] Tag);
internal sealed record CefLibraryGenerationAnchor(int Generation, byte[] Tag);
internal sealed record CefLibraryChainObservation(int AuthenticatedThrough, int AnchoredThrough,
    bool RecoveryRequired);

internal static class CefLibraryGenerationChain
{
    internal const int TagSize = 32;
    internal const int MaximumPayloadBytes = 4 * 1024 * 1024;
    internal const long MaximumChainBytes = 64L * 1024 * 1024;
    private static ReadOnlySpan<byte> Domain => "VEIL-CEF-LIBRARY-GENERATION-v1\0"u8;

    private static void RequireContext(Guid storeId, ReadOnlySpan<byte> key)
    {
        if (storeId == Guid.Empty || key.Length != TagSize)
            throw new ArgumentException("CEF library authentication context unavailable.");
    }

    private static byte[] Authenticate(Guid storeId, ReadOnlySpan<byte> key, int generation,
        ReadOnlySpan<byte> previousTag, ReadOnlySpan<byte> payload)
    {
        RequireContext(storeId, key);
        if (generation <= 0 || generation > CefLibraryGenerationCatalog.MaximumGenerations ||
            previousTag.Length != TagSize || payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new InvalidDataException("Invalid library generation content.");
        // Length-delimited, fixed-order, domain-separated transcript.
        // GUID uses the documented .NET byte ordering; integers are LE.
        int header = Domain.Length + 16 + 4 + TagSize + 4;
        byte[] transcript = new byte[header + payload.Length];
        try
        {
            Domain.CopyTo(transcript);
            int offset = Domain.Length;
            if (!storeId.TryWriteBytes(transcript.AsSpan(offset, 16)))
                throw new InvalidOperationException("Cannot encode store identity.");
            offset += 16;
            BinaryPrimitives.WriteInt32LittleEndian(transcript.AsSpan(offset, 4), generation);
            offset += 4;
            previousTag.CopyTo(transcript.AsSpan(offset, TagSize));
            offset += TagSize;
            BinaryPrimitives.WriteInt32LittleEndian(transcript.AsSpan(offset, 4), payload.Length);
            payload.CopyTo(transcript.AsSpan(header));
            return HMACSHA256.HashData(key, transcript);
        }
        finally { CryptographicOperations.ZeroMemory(transcript); }
    }

    internal static CefLibraryGenerationRecord Seal(Guid storeId, ReadOnlySpan<byte> key,
        int generation, ReadOnlySpan<byte> previousTag, ReadOnlySpan<byte> payload)
    {
        // Bound untrusted lengths before allocating detached copies.
        RequireContext(storeId, key);
        if (generation <= 0 || generation > CefLibraryGenerationCatalog.MaximumGenerations ||
            previousTag.Length != TagSize || payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new InvalidDataException("Invalid library generation content.");
        var prior = previousTag.ToArray();
        var content = payload.ToArray();
        try
        {
            return new(generation, prior, content, Authenticate(storeId, key, generation, prior, content));
        }
        catch { CryptographicOperations.ZeroMemory(content); throw; }
    }

    // Validate a detached record against the caller's expected generation
    // and prior tag. This is not an independent rollback anchor.
    internal static void RequireAuthenticated(Guid storeId, ReadOnlySpan<byte> key,
        int expectedGeneration, ReadOnlySpan<byte> expectedPrevious, CefLibraryGenerationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Generation != expectedGeneration || expectedPrevious.Length != TagSize ||
            record.PreviousTag is not { Length: TagSize } || record.Tag is not { Length: TagSize } ||
            record.Payload is not { Length: > 0 and <= MaximumPayloadBytes } ||
            !CryptographicOperations.FixedTimeEquals(record.PreviousTag, expectedPrevious) ||
            !CryptographicOperations.FixedTimeEquals(record.Tag,
                Authenticate(storeId, key, record.Generation, record.PreviousTag, record.Payload)))
            throw new InvalidDataException("Library generation authentication failed.");
    }

    // Read one bounded record at a time from the caller's verified snapshot.
    // Only an independently anchored prefix is authoritative. A valid suffix
    // or pending stage is evidence requiring recovery, never automatic replay.
    // No payload is returned or applied, and no anchor is advanced here.
    internal static CefLibraryChainObservation Inspect(Guid storeId, ReadOnlySpan<byte> key,
        CefLibraryGenerationSnapshot catalog, CefLibraryGenerationAnchor anchor,
        Func<int, CefLibraryGenerationRecord?> read)
    {
        RequireContext(storeId, key);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(read);
        int last = catalog.Committed;
        if (last < 0 || last > CefLibraryGenerationCatalog.MaximumGenerations ||
            anchor.Generation < 0 || anchor.Generation > last ||
            anchor.Tag is not { Length: TagSize } ||
            catalog.RecoveryRequired != (catalog.Pending is not null) ||
            (catalog.Pending is int pending && (last >= CefLibraryGenerationCatalog.MaximumGenerations || pending != last + 1)) ||
            catalog.Next != (catalog.Pending is null && last < CefLibraryGenerationCatalog.MaximumGenerations ? last + 1 : (int?)null))
            throw new InvalidDataException("Invalid library catalog or rollback anchor.");
        byte[] expectedAnchor = anchor.Tag.ToArray();
        byte[] previous = new byte[TagSize];
        if (anchor.Generation == 0 && !CryptographicOperations.FixedTimeEquals(previous, expectedAnchor))
            throw new InvalidDataException("Invalid library genesis anchor.");

        long total = 0;
        for (int generation = 1; generation <= last; generation++)
        {
            var record = read(generation);
            if (record is null || record.Generation != generation ||
                record.PreviousTag is not { Length: TagSize } ||
                record.Tag is not { Length: TagSize } ||
                record.Payload is not { Length: > 0 and <= MaximumPayloadBytes })
                throw new InvalidDataException("Missing or malformed library generation.");
            total += record.Payload.Length;
            if (total > MaximumChainBytes)
                throw new InvalidDataException("Library authentication work exceeds bound.");
            // Compare and authenticate detached copies, not mutable caller arrays.
            byte[] prior = record.PreviousTag.ToArray();
            byte[] tag = record.Tag.ToArray();
            byte[] payload = record.Payload.ToArray();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(prior, previous) ||
                    !CryptographicOperations.FixedTimeEquals(tag, Authenticate(storeId, key, generation, prior, payload)))
                    throw new InvalidDataException("Library generation authentication failed.");
                if (generation == anchor.Generation &&
                    !CryptographicOperations.FixedTimeEquals(tag, expectedAnchor))
                    throw new InvalidDataException("Library rollback anchor does not match.");
                previous = tag;
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        return new(last, anchor.Generation, catalog.RecoveryRequired || last != anchor.Generation);
    }
}
