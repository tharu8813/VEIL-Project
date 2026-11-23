using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Veil.Shared;

namespace Veil.Desktop;

internal sealed record BrokerClientConfig
{
    public required string PipeName { get; init; }
    public required string VolumeId { get; init; }
    /// <summary>Expected full path of the broker executable; required outside development.</summary>
    public string? BrokerImage { get; init; }
    public bool Development { get; init; }
    /// <summary>Profile directory on the registered volume for product launches.</summary>
    public string? ProfilePath { get; init; }

    internal static readonly string ProductPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VEIL", "client.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static BrokerClientConfig Load(string path, bool development)
    {
        string text = development ? File.ReadAllText(path) : AdminOnlyFile.ReadAllText(path);
        var config = JsonSerializer.Deserialize<BrokerClientConfig>(text, Options)
            ?? throw new InvalidDataException("Empty broker client configuration.");
        if (config.Development != development || (!development && config.BrokerImage is null) ||
            config.PipeName.Length is 0 or > 128 || config.PipeName.Contains('\\') || config.VolumeId.Length is 0 or > 64)
            throw new InvalidDataException("Invalid broker client configuration.");
        return config;
    }
}

/// <summary>Queries the volume broker's Status over its named pipe (ADR-0003). Any failure stops startup.</summary>
internal sealed class NamedPipeVolumeBroker(BrokerClientConfig config) : IVolumeBroker
{
    public VolumeStatus? QueryRegisteredVolume()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", config.PipeName, PipeDirection.InOut, PipeOptions.None,
                TokenImpersonationLevel.Identification);
            pipe.Connect(2000);
            VerifyServer(pipe);
            pipe.Write(JsonSerializer.SerializeToUtf8Bytes(new { command = "Status", volumeId = config.VolumeId }));
            pipe.WriteByte((byte)'\n');
            pipe.Flush();
            var response = new MemoryStream();
            int value;
            while ((value = pipe.ReadByte()) is >= 0 and not '\n')
            {
                response.WriteByte((byte)value);
                if (response.Length > 4096) throw new InvalidDataException("Broker response too large.");
            }
            using var document = JsonDocument.Parse(response.ToArray());
            var root = document.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
                throw new ProfileLocationException($"The volume broker refused the status request ({root.GetProperty("error").GetString()}).");
            var status = root.GetProperty("status");
            if (!status.GetProperty("attached").GetBoolean()) return null;
            return new VolumeStatus(status.GetProperty("volumeGuid").GetString() ?? "",
                status.GetProperty("fullyEncrypted").GetBoolean(), status.GetProperty("protectionOn").GetBoolean(),
                status.GetProperty("unlocked").GetBoolean());
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException or Win32Exception)
        {
            if (error is ProfileLocationException) throw;
            throw new ProfileLocationException($"The volume broker is unavailable ({error.GetType().Name}).");
        }
    }

    private void VerifyServer(NamedPipeClientStream pipe)
    {
        if (config.BrokerImage is null) return;
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new Win32Exception();
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception();
        var image = new StringBuilder(1024);
        int length = image.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, image, ref length)) throw new Win32Exception();
        if (!string.Equals(image.ToString(), Path.GetFullPath(config.BrokerImage), StringComparison.OrdinalIgnoreCase))
            throw new ProfileLocationException("The volume broker pipe is served by an unexpected process.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
}
