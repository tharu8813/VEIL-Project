using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace Veil.VolumeBroker;

/// <summary>Event log that records command names, results and process IDs only — never request bytes.</summary>
internal sealed class BrokerLog(string? path)
{
    private readonly object gate = new();

    internal void Write(string name, object? fields = null)
    {
        string line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, @event = name, fields });
        lock (gate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
            if (path is not null) File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}

internal sealed class Broker(BrokerConfig config, IBitLockerBackend backend, BrokerLog log)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private int unlockFailures;
    private int? unlockOwner;

    internal async Task RunAsync(CancellationToken stop)
    {
        // The first instance claims the name exclusively; "ready" is announced only once it exists.
        var pipe = Native.CreatePipe(config.PipeName, config.UserSid, first: true);
        log.Write("ready", new { pipe = config.PipeName, backend = config.Backend, development = config.Development });
        while (!stop.IsCancellationRequested)
        {
            try { await pipe.WaitForConnectionAsync(stop); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            var connected = pipe;
            _ = Task.Run(() => ServeAsync(connected), CancellationToken.None);
            pipe = Native.CreatePipe(config.PipeName, config.UserSid, first: false);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        byte[] buffer = new byte[Protocol.MaxRequestBytes + 1];
        int length = 0;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int newline = -1;
            while (newline < 0 && length < buffer.Length)
            {
                int read = await pipe.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0) break;
                newline = Array.IndexOf(buffer, (byte)'\n', length, read);
                length += read;
            }

            // The bounded line is read first so a rejected client still receives its answer;
            // it is parsed only after the client passes the identity checks.
            ClientIdentity client;
            try { client = Native.Identify(pipe); }
            catch (Exception) { log.Write("client-rejected", new { reason = "identity" }); await Reply(pipe, Protocol.Error("ClientRejected")); return; }
            string? reason = Reject(client);
            if (reason is not null)
            {
                log.Write("client-rejected", new { client.ProcessId, reason });
                await Reply(pipe, Protocol.Error("ClientRejected"));
                return;
            }
            if (newline < 0 || newline > Protocol.MaxRequestBytes)
            {
                log.Write("request-rejected", new { client.ProcessId, code = "BadRequest" });
                await Reply(pipe, Protocol.Error("BadRequest"));
                return;
            }

            BrokerRequest request;
            try { request = Protocol.Parse(buffer.AsSpan(0, newline)); }
            catch (ProtocolException error)
            {
                log.Write("request-rejected", new { client.ProcessId, code = error.Code });
                await Reply(pipe, Protocol.Error(error.Code));
                return;
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }

            byte[] response;
            using (request)
            {
                await serial.WaitAsync();
                try { response = await Execute(request, client); }
                finally { serial.Release(); }
            }
            await Reply(pipe, response);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            log.Write("connection-error", new { type = error.GetType().Name });
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private string? Reject(ClientIdentity client)
    {
        if (client.SessionId == 0) return "session";
        if (!string.Equals(client.UserSid, config.UserSid, StringComparison.OrdinalIgnoreCase)) return "user";
        if (!config.ClientImages.Any(image => string.Equals(Path.GetFullPath(image), client.ImagePath, StringComparison.OrdinalIgnoreCase)))
            return "image";
        if (!(config.Development && config.AllowUnsignedClients) && !Native.IsSigned(client.ImagePath)) return "signature";
        return null;
    }

    private async Task<byte[]> Execute(BrokerRequest request, ClientIdentity client)
    {
        string command = request.Command.ToString();
        if (request.VolumeId != config.VolumeId)
        {
            log.Write("request-rejected", new { client.ProcessId, command, code = "UnknownVolume" });
            return Protocol.Error("UnknownVolume");
        }
        string? code = null;
        try
        {
            var status = backend.Status();
            switch (request.Command)
            {
                case BrokerCommand.Status:
                    break;
                case BrokerCommand.Attach when status.Attached:
                    code = "AlreadyAttached"; break;
                case BrokerCommand.Attach:
                    backend.Attach();
                    status = backend.Status();
                    if (!string.Equals(status.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase) || !status.ProtectorPresent)
                    {
                        // Wrong image content or protector: never leave an unexpected volume attached.
                        backend.Detach();
                        code = "VolumeMismatch";
                    }
                    break;
                case BrokerCommand.Unlock when !status.Attached:
                    code = "NotAttached"; break;
                case BrokerCommand.Unlock when status.Unlocked:
                    break;
                case BrokerCommand.Unlock when !status.FullyEncrypted || !status.ProtectionOn:
                    code = "NotProtected"; break;
                case BrokerCommand.Unlock:
                    if (backend.Unlock(request.SecretSpan))
                    {
                        unlockFailures = 0;
                        Watch(client.ProcessId);
                    }
                    else
                    {
                        unlockFailures++;
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(8000, 250 << Math.Min(unlockFailures, 5))));
                        code = "AccessDenied";
                    }
                    break;
                case BrokerCommand.Lock when !status.Attached:
                    code = "NotAttached"; break;
                case BrokerCommand.Lock when !status.Unlocked:
                    break;
                case BrokerCommand.Lock:
                    code = LockCode(backend.Lock());
                    break;
                case BrokerCommand.Detach when status.Attached && status.Unlocked:
                    code = "MustBeLocked"; break;
                case BrokerCommand.Detach when status.Attached:
                    backend.Detach(); break;
                case BrokerCommand.Detach:
                    break;
            }
            log.Write("command", new { client.ProcessId, command, result = code ?? "ok" });
            return code is null ? Protocol.Ok(backend.Status()) : Protocol.Error(code);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("command", new { client.ProcessId, command, result = "BackendFailure", type = error.GetType().Name });
            return Protocol.Error("BackendFailure");
        }
    }

    private static string? LockCode(LockResult result) => result switch
    {
        LockResult.Locked => null,
        LockResult.HandlesOpen => "HandlesOpen",
        _ => "LockRefused",
    };

    /// <summary>Locks the volume when the client that unlocked it exits without locking.</summary>
    private void Watch(int pid)
    {
        unlockOwner = pid;
        var handle = Native.OpenForWait(pid);
        if (handle.IsInvalid) { log.Write("watch-failed", new { pid }); return; }
        _ = Task.Run(async () =>
        {
            using (handle)
            using (var exited = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle.DangerousGetHandle(), false) })
                await Task.Run(() => exited.WaitOne());
            for (int attempt = 1; attempt <= 30; attempt++)
            {
                await serial.WaitAsync();
                try
                {
                    if (unlockOwner != pid) return;
                    var status = backend.Status();
                    if (!status.Attached || !status.Unlocked) { unlockOwner = null; return; }
                    var result = backend.Lock();
                    log.Write("client-exit-lock", new { pid, attempt, result = LockCode(result) ?? "ok" });
                    if (result == LockResult.Locked) { unlockOwner = null; return; }
                }
                catch (Exception error) { log.Write("client-exit-lock", new { pid, attempt, result = "BackendFailure", type = error.GetType().Name }); }
                finally { serial.Release(); }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        });
    }

    private static async Task Reply(NamedPipeServerStream pipe, byte[] response)
    {
        await pipe.WriteAsync(response);
        await pipe.WriteAsync("\n"u8.ToArray());
        await pipe.FlushAsync();
    }
}
