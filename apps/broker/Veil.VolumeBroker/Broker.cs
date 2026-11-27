using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;

namespace Veil.VolumeBroker;

internal sealed class Broker(BrokerConfig config, IBitLockerBackend backend, BrokerLog log)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private int unlockFailures;
    private object? unlockOwner;
    private ClientIdentity? authenticatedOwner;
    private SafeProcessHandle? authenticatedOwnerHandle;
    private CefPendingChild? pendingCefChild;
    private sealed record CandidateStop(SafeProcessHandle Child, CefBrokerOwnedJob? Job);
    private readonly List<CandidateStop> unresolvedCefStops = [];
    private bool cefStopRetryRunning;
    private sealed record CompletedPreflight(object Generation, SafeProcessHandle Child, CefBrokerOwnedJob Job, long Issued);
    private CompletedPreflight? completedPreflight;
    private static bool Expired(CompletedPreflight receipt) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(receipt.Issued) >= TimeSpan.FromSeconds(15);

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
            RevokePendingCefChild();
            RetryUnresolvedCefStops();
            var status = backend.Status();
            if (!status.Attached) return;
            if (!string.Equals(status.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The attached volume does not match its registration.");
            if (status.Unlocked && backend.Lock() != LockResult.Locked)
                throw new InvalidOperationException("The registered volume refused the non-forced lock.");
            status = backend.Status();
            if (!status.Attached || status.Unlocked || !string.Equals(status.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The registered volume lock could not be verified.");
            if (unresolvedCefStops.Count != 0)
                throw new InvalidOperationException("CEF candidate exit remains unconfirmed; detach refused.");
            backend.Detach();
            if (backend.Status().Attached) throw new InvalidOperationException("The registered image remains attached.");
            unlockOwner = null;
            ClearAuthenticatedOwner();
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
            (ClientIdentity Identity, SafeProcessHandle Handle) binding;
            try { binding = Native.Identify(pipe); }
            catch (Exception) { log.Write("client-rejected", new { reason = "identity" }); await Reply(pipe, Protocol.Error("ClientRejected")); return; }
            using var pipeClient = binding.Handle;
            ClientIdentity client = binding.Identity;
            string? reason = Reject(client);
            // A separately pinned image may reach ONLY the redemption parser,
            // never the ordinary command dispatcher. Actual identity is checked
            // against the retained child under serial, not just its path.
            bool redemptionCandidate = reason == "image" && !config.Development &&
                config.CefChildImage is { } pinnedChild &&
                string.Equals(client.ImagePath, pinnedChild, StringComparison.OrdinalIgnoreCase);
            if (reason is not null && !redemptionCandidate)
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
                    if (!Native.MatchesPipeClient(pipe, pipeClient, client))
                        response = Protocol.Error("ClientRejected");
                    else if (redemptionCandidate)
                        response = request.Command == BrokerCommand.RedeemCefChild
                            ? RedeemCefChild(request, client, pipeClient) : Protocol.Error("ClientRejected");
                    else if (request.Command == BrokerCommand.RedeemCefChild)
                        response = Protocol.Error("ClientRejected");
                    else
                        response = await Execute(request, client, pipeClient);
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

    // Called only for separately pinned child candidates, while holding serial.
    // The response is a one-shot verification receipt; product startup remains disabled.
    private byte[] RedeemCefChild(BrokerRequest request, ClientIdentity client, SafeProcessHandle pipeClient)
    {
        if (request.VolumeId != config.VolumeId ||
            pendingCefChild is not { } pending || !pending.MatchesChild(client) ||
            pending.ProcessResource is not SafeProcessHandle nominated ||
            !Native.SameLiveProcess(nominated, pipeClient))
            return Protocol.Error("CefChildRejected");

        // Only the designated generation can consume the slot. Once it tries,
        // success, refusal, exception or a lost reply cannot make it reusable.
        pendingCefChild = null;
        using (pending)
        {
            try
            {
                if (config.Development || config.CefChildImage is not { } path ||
                    config.CefChildSha256 is not { } hash ||
                    authenticatedOwner is not { } owner || unlockOwner is not { } generation ||
                    authenticatedOwnerHandle is not { } ownerHandle || !Native.IsRunning(ownerHandle))
                    return Protocol.Error("CefChildRejected");

                bool Protected(BackendStatus state) => state.Attached && state.Unlocked &&
                    state.FullyEncrypted && state.ProtectionOn && state.ProtectorPresent &&
                    string.Equals(state.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase);
                if (!Protected(backend.Status()) ||
                    !CefChildPreflight.VerifyRedemption(pending, owner, ownerHandle, client, path, hash) ||
                    !Protected(backend.Status()) ||
                    pending.ProcessResource is not SafeProcessHandle childHandle ||
                    Native.ObserveProcess(ownerHandle) != owner ||
                    Native.ObserveProcess(childHandle) != client ||
                    !Native.SameLiveProcess(childHandle, pipeClient) ||
                    pending.JobResource is not CefBrokerOwnedJob job || !job.ContainsVerifiedCandidate(client, childHandle) ||
                    !pending.TryRedeem(owner, client, generation,
                        Native.IsRunning(ownerHandle), Native.IsRunning(childHandle), true, true))
                    return Protocol.Error("CefChildRejected");
                // Keep an independent child object after pending admission is
                // disposed. The owner must confirm this exact exited object.
                var retained = Native.OpenCefTerminationHandle(client, childHandle);
                if (completedPreflight is not null) { retained.Dispose(); return Protocol.Error("CefChildRejected"); }
                if (!ReferenceEquals(pending.DetachJob(), job))
                {
                    retained.Dispose();
                    return Protocol.Error("CefChildRejected");
                }
                var receipt = new CompletedPreflight(generation, retained, job, System.Diagnostics.Stopwatch.GetTimestamp());
                completedPreflight = receipt;
                _ = MonitorCompletedPreflightAsync(receipt);
                return Protocol.CefChildVerified();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log.Write("cef-child-rejected", new { type = error.GetType().Name });
                return Protocol.Error("CefChildRejected");
            }
            finally
            {
                // A rejected redemption never leaves the verified candidate
                // running merely because the one-shot slot was consumed.
                if (completedPreflight is null)
                    StopPendingChild(pending);
            }
        }
    }

    private async Task<byte[]> Execute(BrokerRequest request, ClientIdentity client, SafeProcessHandle pipeClient)
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
            if (request.Command is BrokerCommand.Lock or BrokerCommand.Detach)
                RevokePendingCefChild(); // Retire before any backend call, including a failed status.
            if (unresolvedCefStops.Count != 0 &&
                request.Command is BrokerCommand.Lock or BrokerCommand.Detach or BrokerCommand.Unlock or BrokerCommand.StageCefChild)
                RetryUnresolvedCefStops();
            if (unresolvedCefStops.Count != 0 &&
                request.Command is BrokerCommand.Detach or BrokerCommand.Unlock or BrokerCommand.StageCefChild)
                return Protocol.Error("CefExitUnconfirmed");
            // Non-forced Lock remains available even if an exit is unconfirmed.
            var status = backend.Status();
            bool Registered(BackendStatus current) => current.Attached &&
                string.Equals(current.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase);
            if (pendingCefChild is { } pending && (pending.IsExpired || !Registered(status) ||
                !status.Unlocked || !status.FullyEncrypted || !status.ProtectionOn ||
                !status.ProtectorPresent || authenticatedOwnerHandle is not { } active ||
                !Native.IsRunning(active)))
                RevokePendingCefChild();
            if (completedPreflight is { } completed && (Expired(completed) || !Registered(status) ||
                !status.Unlocked || !status.FullyEncrypted || !status.ProtectionOn || !status.ProtectorPresent ||
                authenticatedOwnerHandle is not { } receiptOwner || !Native.IsRunning(receiptOwner)))
                RevokeCompletedPreflight();
            if (status.Attached && !Registered(status))
                return Protocol.Error("VolumeMismatch");
            switch (request.Command)
            {
                case BrokerCommand.Status:
                    break;
                case BrokerCommand.AuthenticatedOwner:
                    // Status alone does not establish that this process unlocked
                    // the volume. Never grant this to another allowed image/PID.
                    if (!Registered(status) || !status.Unlocked || !status.FullyEncrypted ||
                        !status.ProtectionOn || !status.ProtectorPresent ||
                        unlockOwner is null || authenticatedOwner != client ||
                        authenticatedOwnerHandle is not { } liveHandle || !Native.SameLiveProcess(liveHandle, pipeClient))
                        code = "NotAuthenticatedOwner";
                    break;
                case BrokerCommand.CheckCefChild:
                    // This is an owner-only preflight, not a grant or launch
                    // command. No child is added to the normal pipe allowlist.
                    if (!Registered(status) || !status.Unlocked || !status.FullyEncrypted ||
                        !status.ProtectionOn || !status.ProtectorPresent ||
                        unlockOwner is null || authenticatedOwner != client ||
                        authenticatedOwnerHandle is not { } preflightOwner || !Native.SameLiveProcess(preflightOwner, pipeClient))
                    {
                        code = "NotAuthenticatedOwner";
                        break;
                    }
                    if (config.Development || config.CefChildImage is not { } childPath ||
                        config.CefChildSha256 is not { } childHash)
                    {
                        code = "CefUnavailable";
                        break;
                    }
                    CefChildPreflight.Check(client, preflightOwner, request.ChildHandle!.Value,
                        childPath, childHash);
                    status = backend.Status();
                    if (!Registered(status) || !status.Unlocked || !status.FullyEncrypted ||
                        !status.ProtectionOn || !status.ProtectorPresent ||
                        !Native.IsRunning(preflightOwner))
                        code = "NotAuthenticatedOwner";
                    break;
                case BrokerCommand.StageCefChild:
                    if (!Registered(status) || !status.Unlocked || !status.FullyEncrypted ||
                        !status.ProtectionOn || !status.ProtectorPresent ||
                        unlockOwner is not { } generation || authenticatedOwner != client ||
                        authenticatedOwnerHandle is not { } stagingOwner || !Native.SameLiveProcess(stagingOwner, pipeClient))
                    {
                        code = "NotAuthenticatedOwner";
                        break;
                    }
                    if (config.Development || config.CefChildImage is not { } stagePath ||
                        config.CefChildSha256 is not { } stageHash)
                    {
                        code = "CefUnavailable";
                        break;
                    }
                    if (pendingCefChild is not null || completedPreflight is not null)
                    {
                        code = "CefChildPending";
                        break;
                    }
                    var candidate = CefChildPreflight.Stage(client, stagingOwner,
                        request.ChildHandle!.Value, stagePath, stageHash, generation);
                    try
                    {
                        status = backend.Status();
                        if (!Registered(status) || !status.Unlocked || !status.FullyEncrypted ||
                            !status.ProtectionOn || !status.ProtectorPresent ||
                            !Native.IsRunning(stagingOwner) || candidate.IsExpired)
                        {
                            code = "NotAuthenticatedOwner";
                            break;
                        }
                        pendingCefChild = candidate;
                        _ = MonitorPendingCefChildAsync(candidate, generation);
                    }
                    finally
                    {
                        if (!ReferenceEquals(pendingCefChild, candidate))
                        {
                            try { StopPendingChild(candidate); }
                            finally { candidate.Dispose(); }
                        }
                    }
                    break;
                case BrokerCommand.ConfirmCefPreflight:
                    if (config.Development || !Registered(status) || !status.Unlocked ||
                        !status.FullyEncrypted || !status.ProtectionOn || !status.ProtectorPresent ||
                        authenticatedOwner != client || authenticatedOwnerHandle is not { } confirmOwner ||
                        !Native.SameLiveProcess(confirmOwner, pipeClient) ||
                        completedPreflight is not { } receipt)
                    {
                        code = "CefChildRejected";
                        break;
                    }
                    // Only the authenticated owner can consume a receipt.
                    // Any attempt by that owner is one-shot, even on refusal.
                    completedPreflight = null;
                    bool confirmed = false;
                    try
                    {
                        if (Expired(receipt) || !ReferenceEquals(receipt.Generation, unlockOwner) ||
                            !Native.MatchesExitedNomination(client, confirmOwner, request.ChildHandle!.Value, receipt.Child, 29) ||
                            !receipt.Job.HasNoActiveProcesses())
                        {
                            code = "CefChildRejected";
                            break;
                        }
                        status = backend.Status();
                        if (!Registered(status) || !status.Unlocked || !status.FullyEncrypted ||
                            !status.ProtectionOn || !status.ProtectorPresent || Expired(receipt) ||
                            !Native.SameLiveProcess(confirmOwner, pipeClient) || !receipt.Job.HasNoActiveProcesses())
                            code = "CefChildRejected";
                        else confirmed = true;
                    }
                    finally
                    {
                        if (confirmed)
                        {
                            try { receipt.Job.Dispose(); }
                            finally { receipt.Child.Dispose(); }
                        }
                        else StopOrRetain(new CandidateStop(receipt.Child, receipt.Job));
                    }
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
                    var ownerHandle = Native.OpenForWait(client, pipeClient);
                    if (ownerHandle.IsInvalid)
                    {
                        ownerHandle.Dispose();
                        code = "ClientWatchUnavailable";
                        break;
                    }
                    bool authenticated;
                    try { authenticated = backend.Unlock(request.SecretSpan); }
                    catch
                    {
                        // The OS may have unlocked the volume before the backend lost its reply.
                        // Transfer the process handle even on an ambiguous outcome.
                        Watch(client.ProcessId, ownerHandle);
                        throw;
                    }
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
                        else
                        {
                            // Only a fully verified, successful unlock grants ownership.
                            // The watcher owns the handle; a signaled/closed handle refuses.
                            authenticatedOwner = client;
                            authenticatedOwnerHandle = ownerHandle;
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
                    ClearAuthenticatedOwner();
                    unlockOwner = null;
                    break;
                case BrokerCommand.Lock:
                    code = LockCode(backend.Lock());
                    if (code is null)
                    {
                        status = backend.Status();
                        if (!Registered(status) || status.Unlocked) code = "LockRefused";
                        else { unlockOwner = null; ClearAuthenticatedOwner(); }
                    }
                    break;
                case BrokerCommand.Detach when status.Attached && status.Unlocked:
                    code = "MustBeLocked"; break;
                case BrokerCommand.Detach when status.Attached:
                    ClearAuthenticatedOwner();
                    unlockOwner = null;
                    backend.Detach();
                    status = backend.Status();
                    break;
                case BrokerCommand.Detach:
                    ClearAuthenticatedOwner();
                    unlockOwner = null;
                    break;
            }
            log.Write("command", new { client.ProcessId, command, result = code ?? "ok" });
            return code is null ? Protocol.Ok(status) : Protocol.Error(code);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RevokePendingCefChild();
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
    private void ClearAuthenticatedOwner()
    {
        RevokePendingCefChild();
        authenticatedOwner = null;
        authenticatedOwnerHandle = null;
    }

    private void RevokePendingCefChild()
    {
        var pending = pendingCefChild;
        pendingCefChild = null;
        try
        {
            if (pending is not null)
            {
                try { StopPendingChild(pending); }
                finally { pending.Dispose(); }
            }
        }
        finally { RevokeCompletedPreflight(); }
    }

    private void RevokeCompletedPreflight()
    {
        var receipt = completedPreflight;
        completedPreflight = null;
        if (receipt is not null) StopOrRetain(new CandidateStop(receipt.Child, receipt.Job));
    }

    private void StopPendingChild(CefPendingChild pending)
    {
        var resource = pending.DetachRevokedProcess();
        var job = pending.DetachJob();
        if (resource is SafeProcessHandle child)
            StopOrRetain(new CandidateStop(child, job as CefBrokerOwnedJob));
        else
        {
            try { job?.Dispose(); }
            finally { resource?.Dispose(); }
        }
    }

    private bool TryStopVerifiedChild(CandidateStop candidate)
    {
        try
        {
            // Both job accounting and the original process exit signal are
            // required. Failure retains both handles for the existing retry.
            bool drained = candidate.Job is null || candidate.Job.TerminateAndDrain(2000);
            bool exited = Native.TerminateCefChildAndWait(candidate.Child);
            return drained && exited && (candidate.Job is null || candidate.Job.HasNoActiveProcesses());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("cef-child-stop", new { result = "ExitNotConfirmed", type = error.GetType().Name });
            return false;
        }
    }

    // Owns the actual retained handle even on a failed stop. Never reopen
    // a PID, and never treat a failed termination as a successful admission.
    private void StopOrRetain(CandidateStop candidate)
    {
        if (TryStopVerifiedChild(candidate))
        {
            DisposeStoppedCandidate(candidate);
            return;
        }
        unresolvedCefStops.Add(candidate);
        log.Write("cef-child-stop", new { result = "ExitNotConfirmed" });
        if (!cefStopRetryRunning)
        {
            cefStopRetryRunning = true;
            _ = RetryCefStopsAsync();
        }
    }

    private static void DisposeStoppedCandidate(CandidateStop candidate)
    {
        try { candidate.Job?.Dispose(); }
        finally { candidate.Child.Dispose(); }
    }

    private void RetryUnresolvedCefStops()
    {
        for (int i = unresolvedCefStops.Count - 1; i >= 0; --i)
        {
            var candidate = unresolvedCefStops[i];
            if (!TryStopVerifiedChild(candidate)) continue;
            unresolvedCefStops.RemoveAt(i);
            DisposeStoppedCandidate(candidate);
            log.Write("cef-child-stop", new { result = "ExitConfirmedAfterRetry" });
        }
    }

    private async Task RetryCefStopsAsync()
    {
        // Bounded automatic attempts. If the OS still refuses, keep the
        // object for explicit lock/cleanup retries until service exit.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await serial.WaitAsync();
            try
            {
                RetryUnresolvedCefStops();
                if (unresolvedCefStops.Count == 0 || attempt == 29)
                {
                    cefStopRetryRunning = false;
                    if (unresolvedCefStops.Count != 0)
                        log.Write("cef-child-stop", new { result = "RetryExhausted" });
                    return;
                }
            }
            finally { serial.Release(); }
        }
    }

    private bool CandidateAuthorityCurrent(object generation)
    {
        if (!ReferenceEquals(unlockOwner, generation) ||
            authenticatedOwnerHandle is not { } owner || !Native.IsRunning(owner))
            return false;
        var state = backend.Status();
        return state.Attached && state.Unlocked && state.FullyEncrypted &&
            state.ProtectionOn && state.ProtectorPresent &&
            string.Equals(state.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase);
    }

    private async Task MonitorCompletedPreflightAsync(CompletedPreflight expected)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await serial.WaitAsync();
            try
            {
                if (!ReferenceEquals(completedPreflight, expected)) return;
                if (!Expired(expected) && CandidateAuthorityCurrent(expected.Generation) &&
                    (Native.IsRunning(expected.Child)
                        ? expected.Job.ContainsVerifiedCandidate(Native.ObserveProcess(expected.Child), expected.Child)
                        : expected.Job.HasNoActiveProcesses())) continue;
                RevokeCompletedPreflight();
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log.Write("cef-child-monitor", new { type = error.GetType().Name });
                if (ReferenceEquals(completedPreflight, expected)) RevokeCompletedPreflight();
                return;
            }
            finally { serial.Release(); }
        }
    }

    private async Task MonitorPendingCefChildAsync(CefPendingChild expected, object generation)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await serial.WaitAsync();
            try
            {
                if (!ReferenceEquals(pendingCefChild, expected)) return;
                if (!expected.IsExpired && expected.ProcessResource is SafeProcessHandle child &&
                    Native.IsRunning(child) && expected.JobResource is CefBrokerOwnedJob job &&
                    job.ContainsVerifiedCandidate(Native.ObserveProcess(child), child) &&
                    CandidateAuthorityCurrent(generation)) continue;
                RevokePendingCefChild();
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log.Write("cef-child-monitor", new { type = error.GetType().Name });
                if (ReferenceEquals(pendingCefChild, expected)) RevokePendingCefChild();
                return;
            }
            finally { serial.Release(); }
        }
    }

    private void Watch(int pid, SafeProcessHandle handle)
    {
        ClearAuthenticatedOwner();
        var ownership = new object();
        unlockOwner = ownership;
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
                    if (!ReferenceEquals(unlockOwner, ownership)) return;
                    ClearAuthenticatedOwner();
                    RetryUnresolvedCefStops();
                    var status = backend.Status();
                    if (!status.Attached) { unlockOwner = null; return; }
                    if (!string.Equals(status.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unexpected volume during crash relock.");
                    if (!status.Unlocked) { unlockOwner = null; return; }
                    var result = backend.Lock();
                    string? code = LockCode(result);
                    if (result == LockResult.Locked)
                    {
                        status = backend.Status();
                        if (!status.Attached || (!status.Unlocked &&
                            string.Equals(status.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase)))
                        {
                            log.Write("client-exit-lock", new { pid, attempt, result = "ok" });
                            unlockOwner = null;
                            return;
                        }
                        code = "LockRefused";
                    }
                    log.Write("client-exit-lock", new { pid, attempt, result = code ?? "LockRefused" });
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
