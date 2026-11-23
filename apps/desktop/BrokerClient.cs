using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
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
    public bool RequireWindowsService { get; init; }
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
        if (config.Development != development ||
            string.IsNullOrWhiteSpace(config.PipeName) || config.PipeName.Length > 128 ||
            config.PipeName.Contains('\\') || string.IsNullOrWhiteSpace(config.VolumeId) ||
            config.VolumeId.Length > 64 ||
            (!development && (!config.RequireWindowsService ||
                string.IsNullOrWhiteSpace(config.BrokerImage) ||
                !Path.IsPathFullyQualified(config.BrokerImage) ||
                string.IsNullOrWhiteSpace(config.ProfilePath) ||
                !Path.IsPathFullyQualified(config.ProfilePath))))
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
            using var reply = SendCommand("Status");
            var root = reply.RootElement;
            RequireSuccess(root, "Status");
            var status = root.GetProperty("status");
            if (!status.GetProperty("attached").GetBoolean()) return null;
            return new VolumeStatus(status.GetProperty("volumeGuid").GetString() ?? "",
                status.GetProperty("fullyEncrypted").GetBoolean(), status.GetProperty("protectionOn").GetBoolean(),
                status.GetProperty("unlocked").GetBoolean(), status.GetProperty("readyToUnlock").GetBoolean(),
                status.GetProperty("protectorPresent").GetBoolean());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ProfileLocationException($"Invalid volume broker status ({error.GetType().Name}).");
        }
    }

    private void VerifyServer(NamedPipeClientStream pipe)
    {
        if (config.BrokerImage is null) return;
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new Win32Exception();
        if (config.RequireWindowsService)
        {
            using var manager = OpenSCManagerW(null, null, 1); // SC_MANAGER_CONNECT
            if (manager.IsInvalid) throw new Win32Exception();
            using var service = OpenServiceW(manager, "Veil.VolumeBroker", 4); // SERVICE_QUERY_STATUS
            if (service.IsInvalid || !QueryServiceStatusEx(service, 0, out var status,
                Marshal.SizeOf<ServiceProcessStatus>(), out _)) throw new Win32Exception();
            if (status.State != 4 || status.ProcessId != pid)
                throw new ProfileLocationException("The volume broker pipe does not belong to the running Windows service.");
            return;
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceProcessStatus
    {
        public uint Type, State, Accepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }

    private sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int level, out ServiceProcessStatus status, int size, out int needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>Only fixed registered-volume commands are allowed; there is no path argument.</summary>
    internal void Attach() { using var response = SendCommand("Attach"); RequireSuccess(response.RootElement, "Attach"); }
    internal void Lock() { using var response = SendCommand("Lock"); RequireSuccess(response.RootElement, "Lock"); }
    internal void Detach() { using var response = SendCommand("Detach"); RequireSuccess(response.RootElement, "Detach"); }

    internal bool Unlock(ReadOnlySpan<char> secret)
    {
        using var response = SendCommand("Unlock", secret);
        var root = response.RootElement;
        if (!root.GetProperty("ok").GetBoolean() &&
            root.GetProperty("error").GetString() == "AccessDenied") return false;
        RequireSuccess(root, "Unlock");
        return root.GetProperty("status").GetProperty("unlocked").GetBoolean();
    }

    private static void RequireSuccess(JsonElement reply, string command)
    {
        if (!reply.GetProperty("ok").GetBoolean())
            throw new ProfileLocationException(
                $"The volume broker refused {command} ({reply.GetProperty("error").GetString()}).");
    }

    private JsonDocument SendCommand(string command) => SendCommand(command, ReadOnlySpan<char>.Empty);

    private JsonDocument SendCommand(string command, ReadOnlySpan<char> secret)
    {
        using var request = new MemoryStream();
        using (var writer = new Utf8JsonWriter(request))
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteString("volumeId", config.VolumeId);
            if (command == "Unlock") writer.WriteString("secret", secret);
            writer.WriteEndObject();
        }
        byte[] wire = request.GetBuffer();
        try
        {
            if (request.Length > 4096) throw new InvalidDataException("Broker request too long.");
            using var pipe = new NamedPipeClientStream(".", config.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            pipe.Connect(2000);
            VerifyServer(pipe);
            // A mutation can take longer than a status query, especially when Windows
            // mounts a cold VHDX and BitLocker discovers the volume.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(command == "Status" ? 8 : 45));
            pipe.WriteAsync(wire.AsMemory(0, checked((int)request.Length)), timeout.Token)
                .AsTask().GetAwaiter().GetResult();
            byte[] newline = [10];
            pipe.WriteAsync(newline.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
            pipe.FlushAsync(timeout.Token).GetAwaiter().GetResult();
            using var response = new MemoryStream();
            byte[] byteBuffer = new byte[1];
            while (true)
            {
                int read = pipe.ReadAsync(byteBuffer.AsMemory(), timeout.Token)
                    .AsTask().GetAwaiter().GetResult();
                if (read == 0) throw new IOException("Broker response ended prematurely.");
                if (byteBuffer[0] == 10) break;
                if (response.Length >= 4096) throw new InvalidDataException("Broker response too large.");
                response.WriteByte(byteBuffer[0]);
            }
            return JsonDocument.Parse(response.ToArray());
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or InvalidDataException or Win32Exception or OperationCanceledException)
        {
            if (error is ProfileLocationException) throw;
            throw new ProfileLocationException($"The volume broker is unavailable ({error.GetType().Name}).");
        }
        finally { CryptographicOperations.ZeroMemory(wire); }
    }
}
