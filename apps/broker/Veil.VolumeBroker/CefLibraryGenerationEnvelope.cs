// SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Veil.VolumeBroker;

// Prospective versioned UTF-8 envelope, not an encrypted format, filesystem
// writer, key store or recovery permit. Callers own the input/output buffers.
// The decoded record is returned only after authentication against expected
// store, generation and previous tag; a separate anchor is still required.
internal static class CefLibraryGenerationEnvelope
{
    internal const int MaximumEncodedBytes =
        ((CefLibraryGenerationChain.MaximumPayloadBytes + 2) / 3) * 4 + 512;

    internal static byte[] Encode(Guid storeId, ReadOnlySpan<byte> key, int generation,
        ReadOnlySpan<byte> previousTag, ReadOnlySpan<byte> payload)
    {
        var record = CefLibraryGenerationChain.Seal(storeId, key, generation, previousTag, payload);
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteString("store", storeId.ToString("D"));
                writer.WriteNumber("generation", record.Generation);
                writer.WriteBase64String("previous", record.PreviousTag);
                writer.WriteBase64String("payload", record.Payload);
                writer.WriteBase64String("tag", record.Tag);
                writer.WriteEndObject();
                writer.Flush();
            }
            if (buffer.WrittenCount > MaximumEncodedBytes)
                throw new InvalidDataException("Library envelope exceeds bound.");
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Clear();
            CryptographicOperations.ZeroMemory(record.Payload);
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("Malformed library envelope.");
    }

    private static void Property(ref Utf8JsonReader reader, ReadOnlySpan<byte> name)
    {
        Require(reader.Read() && reader.TokenType == JsonTokenType.PropertyName &&
            !reader.ValueIsEscaped && reader.ValueSpan.SequenceEqual(name));
        Require(reader.Read());
    }

    private static byte[] Base64Value(ref Utf8JsonReader reader, int minimum, int maximum)
    {
        Require(reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped &&
            reader.ValueSpan.Length <= Base64.GetMaxEncodedToUtf8Length(maximum));
        if (!reader.TryGetBytesFromBase64(out var decoded) || decoded is null)
            throw new InvalidDataException("Malformed library base64.");
        byte[]? canonical = null;
        bool accepted = false;
        try
        {
            Require(decoded.Length >= minimum && decoded.Length <= maximum);
            canonical = new byte[Base64.GetMaxEncodedToUtf8Length(decoded.Length)];
            Require(Base64.EncodeToUtf8(decoded, canonical, out int consumed, out int written) == OperationStatus.Done &&
                consumed == decoded.Length && written == canonical.Length &&
                reader.ValueSpan.SequenceEqual(canonical));
            accepted = true;
            return decoded;
        }
        finally
        {
            if (canonical is not null) CryptographicOperations.ZeroMemory(canonical);
            if (!accepted) CryptographicOperations.ZeroMemory(decoded);
        }
    }

    internal static CefLibraryGenerationRecord Decode(Guid storeId, ReadOnlySpan<byte> key,
        int expectedGeneration, ReadOnlySpan<byte> expectedPrevious, ReadOnlySpan<byte> encoded)
    {
        Require(encoded.Length > 0 && encoded.Length <= MaximumEncodedBytes &&
            storeId != Guid.Empty && key.Length == CefLibraryGenerationChain.TagSize &&
            expectedGeneration > 0 && expectedGeneration <= CefLibraryGenerationCatalog.MaximumGenerations &&
            expectedPrevious.Length == CefLibraryGenerationChain.TagSize);
        byte[]? payload = null;
        bool accepted = false;
        try
        {
            var reader = new Utf8JsonReader(encoded, new JsonReaderOptions { MaxDepth = 2 });
            Require(reader.Read() && reader.TokenType == JsonTokenType.StartObject);
            Property(ref reader, "version"u8);
            Require(reader.TokenType == JsonTokenType.Number && reader.ValueSpan.SequenceEqual("1"u8));
            Property(ref reader, "store"u8);
            Require(reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped &&
                reader.ValueTextEquals(storeId.ToString("D")));
            Property(ref reader, "generation"u8);
            Require(reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int generation) &&
                generation == expectedGeneration);
            Property(ref reader, "previous"u8);
            var previous = Base64Value(ref reader, CefLibraryGenerationChain.TagSize, CefLibraryGenerationChain.TagSize);
            Property(ref reader, "payload"u8);
            payload = Base64Value(ref reader, 1, CefLibraryGenerationChain.MaximumPayloadBytes);
            Property(ref reader, "tag"u8);
            var tag = Base64Value(ref reader, CefLibraryGenerationChain.TagSize, CefLibraryGenerationChain.TagSize);
            Require(reader.Read() && reader.TokenType == JsonTokenType.EndObject && !reader.Read());
            var record = new CefLibraryGenerationRecord(expectedGeneration, previous, payload, tag);
            CefLibraryGenerationChain.RequireAuthenticated(storeId, key, expectedGeneration, expectedPrevious, record);
            accepted = true;
            return record;
        }
        catch (JsonException error) { throw new InvalidDataException("Malformed library envelope.", error); }
        finally
        {
            if (!accepted && payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }
}
