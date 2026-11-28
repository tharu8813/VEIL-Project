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
    private CefAuthenticatedGeneration? authenticatedGeneration;
    private SafeProcessHandle? authenticatedOwnerHandle;
    private CefPendingChild? pendingCefChild;
    private readonly CefDiagnosticSessionSlot<ICefDiagnosticLifetime> diagnosticSession = new();
    private sealed record CandidateStop(SafeProcessHandle Child, CefBrokerOwnedJob? Job, IDisposable? Image, IDisposable? Runtime)
    {
        internal CefRetainedLifetime Lifetime { get; } = new(Runtime, Image, Job, Child);
    }
    private readonly List<CandidateStop> unresolvedCefStops = [];
    private bool cefStopRetryRunning;
    private readonly CefDiagnosticRelock diagnosticRelock = new();
    private sealed record CompletedPreflight(object Generation, SafeProcessHandle Child, CefBrokerOwnedJob Job,
        IDisposable? Image, IDisposable? Runtime, long Issued);
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

    // SCM notifications are delivered by a single service-owned worker.
    // Retire only the matching authenticated session under the same serial
    // gate as unlock, redemption, explicit lock and crash cleanup.
    internal async Task RevokeWindowsSessionAsync(uint sessionId)
    {
        await serial.WaitAsync();
        try
        {
            if (!CefWindowsSessionRevocation.MatchesOwner(authenticatedOwner, sessionId))
                return;
            ClearAuthenticatedOwner();
            RetryUnresolvedCefStops();
            log.Write("cef-session-revoked", new { reason = "windows-session" });
            // Do not force a BitLocker lock or detach here. The desktop lock
            // path and crash watcher retain their existing non-forced rules.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("cef-session-revoke-failed", new { type = error.GetType().Name });
        }
        finally { serial.Release(); }
    }

    /// <summary>Serializes non-forced service cleanup with any in-progress client operation.</summary>
    internal async Task SecureVolumeAsync()
    {
        await serial.WaitAsync();
        try
        {
            ClearAuthenticatedOwner(); // Service cleanup retires CEF authority even if locking fails.
            RetryUnresolvedCefStops();
            if (diagnosticSession.Occupied)
                throw new InvalidOperationException("CEF diagnostic lifetime remains unconfirmed; service cleanup refused.");
            if (!TryCompleteDiagnosticRelock())
                throw new InvalidOperationException("CEF diagnostic relock remains unconfirmed; service cleanup refused.");
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

                if (!CandidateAuthorityCurrent(generation) ||
                    !CefChildPreflight.VerifyRedemption(pending, owner, ownerHandle, client, path, hash) ||
                    !CandidateAuthorityCurrent(generation) ||
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
                var pins = pending.DetachFilePins();
                var receipt = new CompletedPreflight(generation, retained, job, pins.Image, pins.Runtime,
                    System.Diagnostics.Stopwatch.GetTimestamp());
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
                ClearAuthenticatedOwner(); // Retire CEF authority before any backend call, even on refusal.
            if (unresolvedCefStops.Count != 0 &&
                request.Command is BrokerCommand.Lock or BrokerCommand.Detach or BrokerCommand.Unlock or BrokerCommand.StageCefChild)
                RetryUnresolvedCefStops();
            if (unresolvedCefStops.Count != 0 &&
                request.Command is BrokerCommand.Detach or BrokerCommand.Unlock or BrokerCommand.StageCefChild)
                return Protocol.Error("CefExitUnconfirmed");
            if (diagnosticSession.CleanupPending)
                RetryUnresolvedCefStops();
            if (diagnosticSession.Occupied &&
                request.Command is BrokerCommand.Lock or BrokerCommand.Detach or BrokerCommand.Unlock or BrokerCommand.StageCefChild)
                return Protocol.Error("CefExitUnconfirmed");
            if (diagnosticRelock.Pending && !TryCompleteDiagnosticRelock() &&
                request.Command != BrokerCommand.Status)
                return Protocol.Error("LockRefused");
            // Non-forced Lock remains available even if a disabled candidate exit is unconfirmed.
            var status = backend.Status();
            RefreshAuthenticatedOwner(status);
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
            // Revocation above can create a new unresolved stop after the
            // first guard. Do not admit a new candidate in that same request.
            if (unresolvedCefStops.Count != 0 &&
                request.Command is BrokerCommand.Detach or BrokerCommand.Unlock or BrokerCommand.StageCefChild)
                return Protocol.Error("CefExitUnconfirmed");
            if (diagnosticSession.Occupied &&
                request.Command is BrokerCommand.Lock or BrokerCommand.Detach or BrokerCommand.Unlock or BrokerCommand.StageCefChild)
                return Protocol.Error("CefExitUnconfirmed");
            if (diagnosticRelock.Pending && request.Command != BrokerCommand.Status)
                return Protocol.Error("LockRefused");
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
                    if (!RefreshAuthenticatedOwner(status) || !Native.IsRunning(preflightOwner))
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
                        request.ChildHandle!.Value, stagePath, stageHash, generation,
                        (child, job, image, runtime) => StopOrRetain(new CandidateStop(child, job, image, runtime)));
                    try
                    {
                        status = backend.Status();
                        if (!RefreshAuthenticatedOwner(status) ||
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
                        if (!RefreshAuthenticatedOwner(status) || Expired(receipt) ||
                            !Native.SameLiveProcess(confirmOwner, pipeClient) || !receipt.Job.HasNoActiveProcesses())
                            code = "CefChildRejected";
                        else confirmed = true;
                    }
                    finally
                    {
                        var resources = new CandidateStop(receipt.Child, receipt.Job, receipt.Image, receipt.Runtime);
                        if (confirmed)
                        {
                            if (!ReleaseCandidate(resources, exitAlreadyConfirmed: true))
                            {
                                RetainForRetry(resources);
                                code = "CefExitUnconfirmed";
                            }
                        }
                        else StopOrRetain(resources);
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
                            var authority = new CefAuthenticatedGeneration(client, unlockOwner!);
                            authenticatedGeneration = authority;
                            if (!RefreshAuthenticatedOwner(status))
                            {
                                log.Write("rejected-unlock-lock", new { result = LockCode(backend.Lock()) ?? "ok" });
                                code = "NotAuthenticatedOwner";
                            }
                            else
                                _ = MonitorAuthenticatedGenerationAsync(authority);
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
            ClearAuthenticatedOwner();
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
    // Prepared internal registration only; no request handler calls this.
    // Caller holds serial, owns the already assigned private pair and pins,
    // and must retain/stop them if registration refuses or throws.
    // Called only under serial by a future reviewed launch coordinator.
    // This preparation does not create a process or authorize IPC/startup.
    private CefBrokerLaunchPreparation PrepareDiagnosticLaunch(object expectedGeneration,
        ReadOnlySpan<byte> baselinePayload, ReadOnlySpan<byte> baselineSignature)
    {
        // Initial admission requires an empty slot. Once the probe owns
        // it, rechecks must allow that occupied slot but never a retired
        // generation or pending relock. All calls remain serialized.
        bool AuthorityCurrent() => !diagnosticRelock.Pending && !config.Development &&
            pendingCefChild is null && completedPreflight is null && unresolvedCefStops.Count == 0 &&
            CandidateAuthorityCurrent(expectedGeneration);
        if (diagnosticSession.Occupied || !AuthorityCurrent() || config.CefChildImage is not { } image ||
            config.CefChildSha256 is not { } digest ||
            authenticatedOwner is not { } owner ||
            authenticatedOwnerHandle is not { } ownerHandle)
            throw new UnauthorizedAccessException("CEF preparation unavailable.");
        return CefBrokerLaunchPreparation.Open(image, digest, owner, ownerHandle,
            AuthorityCurrent, baselinePayload, baselineSignature,
            config.VolumeId, config.VolumeGuid, config.PipeName, backend.Status);
    }

    // Disconnected internal probe: create, verify and drain, NEVER resume.
    // No request, service startup or product caller. All operations hold serial.
    private void RunDiagnosticCreationProbe(object expectedGeneration,
        ReadOnlySpan<byte> baselinePayload, ReadOnlySpan<byte> baselineSignature)
    {
        CefBrokerLaunchPreparation? preparation = null;
        CefBrokerOwnedJob? outer = null, inner = null;
        bool accepted = false;
        try
        {
            preparation = PrepareDiagnosticLaunch(expectedGeneration, baselinePayload, baselineSignature);
            outer = CefBrokerOwnedJob.CreateDisabledCandidate();
            inner = CefBrokerOwnedJob.CreateDisabledCandidate();
            var probe = new CefPreparedCreationLifetime(outer, inner, preparation,
                preparation.CreateDiagnosticCandidate);
            var outcome = diagnosticSession.RegisterAndActivate(expectedGeneration, probe, () =>
            {
                accepted = true; // The slot owns everything before creation.
                probe.Create();
            }, out var failure);
            if (outcome == CefDiagnosticActivationOutcome.Refused)
                throw new UnauthorizedAccessException("CEF creation probe slot unavailable.");
            if (failure is not null) throw new UnauthorizedAccessException("CEF creation probe refused.", failure);
            // Replace the occupied slot in place. The successor borrows
            // the probe's resource lifetime; the probe never releases on
            // successful handoff. Failure retains the original slot.
            if (!diagnosticSession.ReplacePrepared(expectedGeneration, probe,
                () => preparation.PrepareBoundSession(expectedGeneration, probe), out var handoffFailure))
                throw new UnauthorizedAccessException("CEF prepared session handoff refused.", handoffFailure);
            log.Write("cef-creation-probe", new { result = "BoundSuspendedVerified" });
        }
        finally
        {
            if (accepted)
            {
                // Even a successful probe is terminal: revoke, prove exit,
                // release and request non-forced relock. Reentrant revocation
                // during creation cannot release an activating slot.
                diagnosticRelock.Request(expectedGeneration);
                try { ClearAuthenticatedOwner(); }
                finally { EnsureCefStopRetry(); }
            }
            else
            {
                // No native creation could have run before acceptance.
                try { inner?.Dispose(); }
                finally
                {
                    try { outer?.Dispose(); }
                    finally { preparation?.Dispose(); }
                }
            }
        }
    }

    private CefDiagnosticActivationOutcome TryRegisterDiagnosticSession(object expectedGeneration,
        ClientIdentity root, SafeProcessHandle rootHandle,
        CefBrokerOwnedJob brokerJob, CefBrokerOwnedJob peerJob,
        Veil.Desktop.CefRuntimeRegistrationLease registration,
        ReadOnlySpan<byte> baselinePayload, ReadOnlySpan<byte> baselineSignature,
        Veil.Desktop.CefContainedProcess preparedRoot, out bool ownershipTransferred)
    {
        ownershipTransferred = false;
        ArgumentNullException.ThrowIfNull(preparedRoot);
        if (diagnosticRelock.Pending || config.Development || config.CefChildImage is not { } image ||
            config.CefChildSha256 is not { } digest ||
            diagnosticSession.Occupied || pendingCefChild is not null ||
            completedPreflight is not null || unresolvedCefStops.Count != 0 ||
            !CandidateAuthorityCurrent(expectedGeneration) ||
            authenticatedOwner is not { } owner ||
            authenticatedOwnerHandle is not { } ownerHandle)
            return CefDiagnosticActivationOutcome.Refused;
        // The installed runtime lease pins the image throughout the session.
        // Independently verify the broker's signed image/digest before transfer.
        using var verified = CefPinnedImage.OpenVerified(root, rootHandle, image, new CefImageDigest(digest));
        registration.RequireCurrent();
        if (!string.Equals(registration.ImagePath, image, StringComparison.OrdinalIgnoreCase) ||
            !CandidateAuthorityCurrent(expectedGeneration))
            return CefDiagnosticActivationOutcome.Refused;
        // No caller-selected verifier or fallback to unverified System32.
        // Exact payload digest, signing key, approval authority and actual host
        // are obtained/checked against the protected installed trust state.
        var systemVerifier = CefSignedSystemModuleBaseline.VerifyInstalledTrust(baselinePayload, baselineSignature);
        systemVerifier.RequireBoundTrust();
        if (!CandidateAuthorityCurrent(expectedGeneration)) return CefDiagnosticActivationOutcome.Refused;
        var session = new CefBoundDiagnosticSession(owner, ownerHandle, expectedGeneration,
            registration, brokerJob, peerJob, root, rootHandle, () =>
            {
                if (!CandidateAuthorityCurrent(expectedGeneration))
                    throw new UnauthorizedAccessException("CEF diagnostic generation retired.");
                registration.RequireCurrent();
                return (expectedGeneration, registration, true);
            }, systemVerifier, preparedRoot);
        // Transfer before resume. The caller must NOT dispose any retained
        // resources after ownershipTransferred, even if cleanup throws.
        // Still disconnected: only a future reviewed internal launch path
        // may supply the original prepared process object.
        bool accepted = false;
        bool activated = false;
        try
        {
            var outcome = diagnosticSession.RegisterAndActivate(expectedGeneration, session, () =>
            {
                accepted = true;
                session.ActivatePreparedRoot();
            }, out var failure);
            activated = outcome == CefDiagnosticActivationOutcome.Activated;
            if (failure is not null)
                log.Write("cef-diagnostic-activation-refused", new { type = failure.GetType().Name });
            return outcome;
        }
        finally
        {
            ownershipTransferred = accepted;
            if (accepted && !activated)
            {
                // Latch even if the activation's context check already
                // retired the generation or cleanup previously failed.
                diagnosticRelock.Request(expectedGeneration);
                try { ClearAuthenticatedOwner(); }
                finally { EnsureCefStopRetry(); }
            }
        }
    }

    private void ClearAuthenticatedOwner()
    {
        // Retire authorization before stopping candidates: cleanup can fail.
        // Keep unlockOwner for the independent crash-relock watcher.
        // Latch before cleanup: even immediate release cannot erase the
        // obligation to relock the volume after a diagnostic retirement.
        if (diagnosticSession.Occupied)
            diagnosticRelock.Request(unlockOwner);
        authenticatedGeneration?.Revoke();
        authenticatedGeneration = null;
        authenticatedOwner = null;
        authenticatedOwnerHandle = null;
        CefOwnerRevocationCleanup.Run(diagnosticSession.Retire, RevokePendingCefChild,
            RetryDiagnosticSession, () =>
            {
                if (unresolvedCefStops.Count != 0 || diagnosticSession.CleanupPending || diagnosticRelock.Pending)
                    EnsureCefStopRetry();
            });
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
        if (receipt is not null) StopOrRetain(new CandidateStop(receipt.Child, receipt.Job, receipt.Image, receipt.Runtime));
    }

    private void StopPendingChild(CefPendingChild pending)
    {
        var resource = pending.DetachRevokedProcess();
        var job = pending.DetachJob();
        if (resource is SafeProcessHandle child)
        {
            var pins = pending.DetachFilePins();
            StopOrRetain(new CandidateStop(child, job as CefBrokerOwnedJob, pins.Image, pins.Runtime));
        }
        else
        {
            try { job?.Dispose(); }
            finally { resource?.Dispose(); }
        }
    }

    private bool TryStopVerifiedChild(CandidateStop candidate)
    {
        // Job query/termination failure must not suppress the independent
        // process stop. Each action runs even if the other fails.
        bool drained = Attempt(() => candidate.Job is null || candidate.Job.TerminateAndDrain(2000), "job-drain");
        bool exited = Attempt(() => Native.TerminateCefChildAndWait(candidate.Child), "process-exit");
        bool empty = Attempt(() => candidate.Job is null || candidate.Job.HasNoActiveProcesses(), "job-accounting");
        return drained && exited && empty;

        bool Attempt(Func<bool> action, string step)
        {
            try { return action(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log.Write("cef-child-stop", new { result = "ExitNotConfirmed", step, type = error.GetType().Name });
                return false;
            }
        }
    }

    // Owns the actual retained handle even on a failed stop. Never reopen
    // a PID, and never treat a failed termination as a successful admission.
    private void StopOrRetain(CandidateStop candidate)
    {
        if (ReleaseCandidate(candidate)) return;
        RetainForRetry(candidate);
    }

    private void RetainForRetry(CandidateStop candidate)
    {
        unresolvedCefStops.Add(candidate);
        log.Write("cef-child-stop", new { result = "ExitNotConfirmed" });
        EnsureCefStopRetry();
    }

    private void EnsureCefStopRetry()
    {
        if (cefStopRetryRunning) return;
        cefStopRetryRunning = true;
        _ = RetryCefStopsAsync();
    }

    private void RetryDiagnosticSession()
    {
        if (!diagnosticSession.CleanupPending) return;
        try
        {
            if (diagnosticSession.TryReleaseRetired(2000))
                log.Write("cef-diagnostic-stop", new { result = "Released" });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("cef-diagnostic-stop", new { result = "ReleaseUnconfirmed", type = error.GetType().Name });
        }
    }

    private bool ReleaseCandidate(CandidateStop candidate, bool exitAlreadyConfirmed = false)
    {
        try
        {
            return candidate.Lifetime.TryRelease(() => exitAlreadyConfirmed || TryStopVerifiedChild(candidate));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("cef-child-release", new { result = "ReleaseUnconfirmed", type = error.GetType().Name });
            return false;
        }
    }

    private void RetryUnresolvedCefStops()
    {
        RetryDiagnosticSession();
        for (int i = unresolvedCefStops.Count - 1; i >= 0; --i)
        {
            var candidate = unresolvedCefStops[i];
            if (!ReleaseCandidate(candidate)) continue;
            unresolvedCefStops.RemoveAt(i);
            log.Write("cef-child-stop", new { result = "ExitConfirmedAfterRetry" });
        }
    }

    // All callers hold serial. Never lock a different volume or one with
    // unconfirmed CEF lifetimes. The backend lock is non-forced.
    private bool TryCompleteDiagnosticRelock()
    {
        if (!diagnosticRelock.Pending) return true;
        try
        {
            if (!diagnosticRelock.TryComplete(unlockOwner,
                authenticatedGeneration is null && !diagnosticSession.Occupied &&
                unresolvedCefStops.Count == 0 && pendingCefChild is null && completedPreflight is null,
                () =>
                {
                    var status = backend.Status();
                    bool Registered(BackendStatus state) => state.Attached &&
                        string.Equals(state.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase);
                    if (!Registered(status)) return false;
                    if (status.Unlocked && backend.Lock() != LockResult.Locked) return false;
                    status = backend.Status();
                    return Registered(status) && !status.Unlocked && status.ReadyToUnlock;
                }))
                return false;
            unlockOwner = null;
            log.Write("cef-diagnostic-relock", new { result = "LockedVerified" });
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("cef-diagnostic-relock", new { result = "LockUnconfirmed", type = error.GetType().Name });
            return false;
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
            bool continueRetry = false;
            try
            {
                var failure = CefStopRetryPolicy.TryAttempt(RetryUnresolvedCefStops,
                    () => TryCompleteDiagnosticRelock());
                if (failure is not null)
                    log.Write("cef-stop-retry", new { result = "AttemptUnconfirmed", type = failure.GetType().Name });
                bool pending = unresolvedCefStops.Count != 0 || diagnosticSession.CleanupPending || diagnosticRelock.Pending;
                continueRetry = CefStopRetryPolicy.ShouldContinue(attempt, 30, pending, failure);
                if (!continueRetry)
                {
                    if (unresolvedCefStops.Count != 0 || diagnosticSession.CleanupPending)
                        log.Write("cef-child-stop", new { result = "RetryExhausted" });
                    if (diagnosticRelock.Pending)
                        log.Write("cef-diagnostic-relock", new { result = "RetryExhausted" });
                    if (failure is not null)
                        log.Write("cef-stop-retry", new { result = "RetryExhaustedUnconfirmed" });
                    return;
                }
            }
            finally
            {
                // Reset while still holding serial. A request must not
                // observe a finished worker as running or race a new worker.
                if (!continueRetry) cefStopRetryRunning = false;
                serial.Release();
            }
        }
    }

    // All callers hold serial. A failed observation invalidates the owner
    // even if no child was staged yet. Never revive the same unlock generation
    // merely because a later backend snapshot looks protected again.
    private bool RefreshAuthenticatedOwner(BackendStatus state)
    {
        if (authenticatedGeneration is not { } authority) return false;
        bool protectedVolume = state.Attached && state.Unlocked && state.FullyEncrypted &&
            state.ProtectionOn && state.ProtectorPresent &&
            string.Equals(state.VolumeGuid, config.VolumeGuid, StringComparison.OrdinalIgnoreCase);
        bool verified = false;
        try
        {
            verified = authenticatedOwner is { } expected &&
                authenticatedOwnerHandle is { } owner && Native.IsRunning(owner) &&
                Native.ObserveProcess(owner) == expected;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("cef-owner-observation-refused", new { type = error.GetType().Name });
        }
        if (authority.Observe(authenticatedOwner, unlockOwner, verified, protectedVolume))
            return true;
        ClearAuthenticatedOwner();
        return false;
    }

    private bool CandidateAuthorityCurrent(object generation)
    {
        if (!ReferenceEquals(unlockOwner, generation) || authenticatedGeneration is null)
            return false;
        try { return RefreshAuthenticatedOwner(backend.Status()); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ClearAuthenticatedOwner();
            throw;
        }
    }

    // Observe the owner even while no client commands or CEF candidates are
    // active. This is a periodic sample, not continuous protection evidence.
    private async Task MonitorAuthenticatedGenerationAsync(CefAuthenticatedGeneration expected)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await serial.WaitAsync();
            try
            {
                if (!ReferenceEquals(authenticatedGeneration, expected)) return;
                if (!RefreshAuthenticatedOwner(backend.Status())) return;
                // Only an already registered internal diagnostic session can
                // reach this path. Observation failure retires the owner and
                // the session through the existing catch/cleanup path.
                if (diagnosticSession.Current(unlockOwner) is CefBoundDiagnosticSession session)
                    _ = session.Observe();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (ReferenceEquals(authenticatedGeneration, expected))
                    ClearAuthenticatedOwner();
                log.Write("cef-owner-monitor-refused", new { type = error.GetType().Name });
                return;
            }
            finally { serial.Release(); }
        }
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
                    if (diagnosticSession.Occupied)
                        throw new InvalidOperationException("CEF diagnostic lifetime remains unconfirmed; crash relock deferred.");
                    if (!TryCompleteDiagnosticRelock())
                        throw new InvalidOperationException("CEF diagnostic relock remains unconfirmed.");
                    if (!ReferenceEquals(unlockOwner, ownership)) return;
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
