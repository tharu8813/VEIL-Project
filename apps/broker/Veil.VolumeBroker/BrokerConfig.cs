using System.Text.Json;
using System.Text.Json.Serialization;
using Veil.Shared;

namespace Veil.VolumeBroker;

/// <summary>The single registered volume and the clients allowed to manage it (ADR-0003).</summary>
internal sealed record BrokerConfig
{
    public required string VolumeId { get; init; }
    public required string ImagePath { get; init; }
    public required string VolumeGuid { get; init; }
    public required string ProtectorId { get; init; }
    public required string UserSid { get; init; }
    public required string[] ClientImages { get; init; }
    public string PipeName { get; init; } = "VEIL.VolumeBroker";
    public string Backend { get; init; } = "bitlocker";
    public bool Development { get; init; }
    public bool AllowUnsignedClients { get; init; }
    public string? LogPath { get; init; }
    public FakeBackendConfig? Fake { get; init; }

    internal static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VEIL", "broker.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <param name="development">True only for the explicit development switch; skips the admin-only ACL check.</param>
    internal static BrokerConfig Load(string path, bool development)
    {
        string text = development ? File.ReadAllText(path) : AdminOnlyFile.ReadAllText(path);
        var config = JsonSerializer.Deserialize<BrokerConfig>(text, Options)
            ?? throw new InvalidDataException("Empty broker configuration.");
        config.Validate(development);
        return config;
    }

    private void Validate(bool development)
    {
        if (Development != development)
            throw new InvalidDataException("The configuration's development flag does not match the start mode.");
        if (!development && (Backend != "bitlocker" || AllowUnsignedClients || Fake is not null || LogPath is not null))
            throw new InvalidDataException("Production configuration allows only the BitLocker backend and signed clients.");
        if (Backend is not ("bitlocker" or "fake") || (Backend == "fake") != (Fake is not null))
            throw new InvalidDataException("Unknown or incomplete backend configuration.");
        if (string.IsNullOrWhiteSpace(VolumeId) || VolumeId.Length > 64 ||
            !VolumeGuid.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || !VolumeGuid.EndsWith('\\') ||
            !Path.IsPathFullyQualified(ImagePath) || !Guid.TryParse(ProtectorId, out _) ||
            !UserSid.StartsWith("S-1-", StringComparison.Ordinal) || ClientImages.Length == 0 ||
            ClientImages.Any(image => !Path.IsPathFullyQualified(image)) ||
            PipeName.Length is 0 or > 128 || PipeName.Contains('\\'))
            throw new InvalidDataException("Invalid broker configuration value.");
    }
}

internal sealed record FakeBackendConfig
{
    /// <summary>Hex SHA-256 of the UTF-8 test secret.</summary>
    public required string SecretSha256 { get; init; }
    /// <summary>While this file exists the fake volume reports open handles and refuses a non-forced lock.</summary>
    public required string HandleMarker { get; init; }
    /// <summary>Volume GUID the fake reports after attach; defaults to the registered one.</summary>
    public string? ReportedVolumeGuid { get; init; }
    public bool ReportedProtectorPresent { get; init; } = true;
}
