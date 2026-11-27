using System.Security.Cryptography;
using System.Text.Json;

namespace Veil.VolumeBroker;

internal enum BrokerCommand { Status, Attach, Unlock, Lock, Detach, AuthenticatedOwner, CheckCefChild, StageCefChild }

/// <summary>One parsed request. The secret is held in a clearable buffer, never in a string.</summary>
internal sealed class BrokerRequest : IDisposable
{
    internal BrokerCommand Command { get; init; }
    internal required string VolumeId { get; init; }
    internal char[]? Secret { get; init; }
    internal int SecretLength { get; init; }
    internal long? ChildHandle { get; init; }
    internal ReadOnlySpan<char> SecretSpan => Secret.AsSpan(0, SecretLength);

    public void Dispose()
    {
        if (Secret is not null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Secret.AsSpan()));
    }
}

internal sealed class ProtocolException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal static class Protocol
{
    internal const int MaxRequestBytes = 4096;
    internal const int MaxSecretChars = 256;

    /// <summary>Strict parser: exactly the fields the command needs, no paths or extra members.</summary>
    internal static BrokerRequest Parse(ReadOnlySpan<byte> json)
    {
        string? command = null, volumeId = null;
        char[]? secret = null;
        int secretLength = 0;
        long? childHandle = null;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 2 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new ProtocolException("BadRequest");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new ProtocolException("BadRequest");
                string name = reader.GetString()!;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new ProtocolException("BadRequest");
                switch (name)
                {
                    case "command" when command is null: command = reader.GetString(); break;
                    case "volumeId" when volumeId is null: volumeId = reader.GetString(); break;
                    case "childHandle" when childHandle is null:
                        // Decimal string avoids JSON numeric precision loss in clients.
                        string raw = reader.GetString()!;
                        if (raw.Length is 0 or > 19 || raw[0] == '0' ||
                            !raw.All(c => c is >= '0' and <= '9') ||
                            !long.TryParse(raw, System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out long handle) || handle <= 0)
                            throw new ProtocolException("BadRequest");
                        childHandle = handle;
                        break;
                    case "secret" when secret is null:
                        if (reader.HasValueSequence || reader.ValueSpan.Length > MaxSecretChars * 6)
                            throw new ProtocolException("BadRequest");
                        secret = new char[reader.ValueSpan.Length];
                        secretLength = reader.CopyString(secret);
                        if (secretLength is 0 or > MaxSecretChars) throw new ProtocolException("BadRequest");
                        break;
                    default: throw new ProtocolException("BadRequest");
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) throw new ProtocolException("BadRequest");
            if (!Enum.TryParse<BrokerCommand>(command, ignoreCase: false, out var parsed) ||
                !Enum.IsDefined(parsed) || command != parsed.ToString() || volumeId is null)
                throw new ProtocolException("BadRequest");
            if ((parsed == BrokerCommand.Unlock) != (secret is not null)) throw new ProtocolException("BadRequest");
            if ((parsed is BrokerCommand.CheckCefChild or BrokerCommand.StageCefChild) != childHandle.HasValue) throw new ProtocolException("BadRequest");
            return new BrokerRequest { Command = parsed, VolumeId = volumeId, Secret = secret,
                SecretLength = secretLength, ChildHandle = childHandle };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ProtocolException)
        {
            if (secret is not null) Array.Clear(secret);
            throw error as ProtocolException ?? new ProtocolException("BadRequest");
        }
    }

    internal static byte[] Error(string code) =>
        JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = code });

    internal static byte[] Ok(BackendStatus status) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        ok = true,
        status = new
        {
            attached = status.Attached,
            volumeGuid = status.VolumeGuid,
            fullyEncrypted = status.FullyEncrypted,
            protectionOn = status.ProtectionOn,
            unlocked = status.Unlocked,
            readyToUnlock = status.ReadyToUnlock,
            protectorPresent = status.ProtectorPresent,
        },
    });
}
