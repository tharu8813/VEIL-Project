using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
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

    internal async Task RunAsync(CancellationToken stop, Action? ready = null)
    {
        // The first instance claims the name exclusively; "ready" is announced only once it exists.
        var pipe = Native.CreatePipe(config.PipeName, config.UserSid, first: true);
        log.Write("ready", new { pipe = config.PipeName, backend = config.Backend, development = config.Development });
        try
        {
            ready?.Invoke();
            while (!stop.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stop);
                var connected = pipe;
                pipe = Native.CreatePipe(config.PipeName, config.UserSid, first: false);
                _ = Task.Run(() => ServeAsync(connected, stop), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await pipe.DisposeAsync(); }
    }

    /// <summary>Serializes non-forced service cleanup with any in-progress client operation.</summary>
    internal async Task SecureVolumeAsync()
    {
        await serial.WaitAsync();
        try
        {
            var status = backend.Status();
            if (!status.Attached) return;
            if (!string.Equals(status.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The attached volume does not match its registration.");
            if (status.Unlocked && backend.Lock() != LockResult.Locked)
                throw new InvalidOperationException("The registered volume refused the non-forced lock.");
            status = backend.Status();
            if (!status.Attached || status.Unlocked || !string.Equals(status.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The registered volume lock could not be verified.");
            backend.Detach();
            if (backend.Status().Attached) throw new InvalidOperationException("The registered image remains attached.");
            unlockOwner = null;
        }
        finally { serial.Release(); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using var _ = pipe;
        byte[] buffer = new byte[Protocol.MaxRequestBytes + 1];
        int length = 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
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
                await serial.WaitAsync(stop);
                try
                {
                    stop.ThrowIfCancellationRequested();
                    response = await Execute(request, client);
                }
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
        if (!(config.Development && config.AllowUnsignedClients) &&
            (!Native.IsSigned(client.ImagePath) || (!config.Development &&
                !Native.MatchesSha256(client.ImagePath, config.ClientSha256!)))) return "signature";
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
            bool Registered(BackendStatus current) => current.Attached &&
                string.Equals(current.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase);
            if (status.Attached && !Registered(status))
                return Protocol.Error("VolumeMismatch");
            switch (request.Command)
            {
                case BrokerCommand.Status:
                    break;
                case BrokerCommand.Attach when status.Attached:
                    code = "AlreadyAttached"; break;
                case BrokerCommand.Attach:
                    backend.Attach();
                    status = backend.Status();
                    if (!Registered(status) || (!status.ReadyToUnlock && !status.Unlocked))
                    {
                        // Wrong image content or protector: never leave an unexpected volume attached.
                        backend.Detach();
                        code = "VolumeMismatch";
                    }
                    break;
                case BrokerCommand.Unlock when !status.Attached:
                    code = "NotAttached"; break;
                case BrokerCommand.Unlock when status.Unlocked:
                    code = "AlreadyUnlocked"; break;
                case BrokerCommand.Unlock when !status.ReadyToUnlock:
                    code = "NotProtected"; break;
                case BrokerCommand.Unlock:
                    // Acquire the crash-relock handle BEFORE opening protected data.
                    // If this process cannot be watched, no new unlock is allowed.
                    var ownerHandle = Native.OpenForWait(client.ProcessId);
                    if (ownerHandle.IsInvalid)
                    {
                        ownerHandle.Dispose();
                        code = "ClientWatchUnavailable";
                        break;
                    }
                    bool authenticated;
                    try { authenticated = backend.Unlock(request.SecretSpan); }
                    catch { ownerHandle.Dispose(); throw; }
                    if (authenticated)
                    {
                        unlockFailures = 0;
                        Watch(client.ProcessId, ownerHandle);
                        status = backend.Status();
                        if (!Registered(status) || !status.Unlocked || !status.FullyEncrypted ||
                            !status.ProtectionOn || !status.ProtectorPresent)
                        {
                            // The requester gets no successful unlock result unless verification passes.
                            if (Registered(status) && status.Unlocked)
                                log.Write("rejected-unlock-lock", new { result = LockCode(backend.Lock()) ?? "ok" });
                            code = "NotProtected";
                        }
                    }
                    else
                    {
                        ownerHandle.Dispose();
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
                    if (code is null) status = backend.Status();
                    break;
                case BrokerCommand.Detach when status.Attached && status.Unlocked:
                    code = "MustBeLocked"; break;
                case BrokerCommand.Detach when status.Attached:
                    backend.Detach();
                    status = backend.Status();
                    break;
                case BrokerCommand.Detach:
                    break;
            }
            log.Write("command", new { client.ProcessId, command, result = code ?? "ok" });
            return code is null ? Protocol.Ok(status) : Protocol.Error(code);
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
    private void Watch(int pid, SafeProcessHandle handle)
    {
        unlockOwner = pid;
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
