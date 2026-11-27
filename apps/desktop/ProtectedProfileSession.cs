using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;

namespace Veil.Desktop;

/// <summary>
/// Owns this launch's registered-volume unlock. No browser state is touched before the
/// broker confirms a fresh unlock, and failure never falls back to a plaintext profile.
/// </summary>
internal sealed class ProtectedProfileSession : IDisposable
{
    private readonly NamedPipeVolumeBroker broker;
    private readonly ProtectedSessionNotifications notifications;
    private bool attachedHere;
    private bool unlockAttempted;
    private bool disposed;
    private string? authenticatedVolumeGuid;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;

    private ProtectedProfileSession(NamedPipeVolumeBroker broker, ProtectedSessionNotifications notifications)
    {
        this.broker = broker;
        this.notifications = notifications;
    }

    internal static ProtectedProfileSession? TryOpen(NamedPipeVolumeBroker broker, ProtectedSessionNotifications notifications,
        out char[]? password)
    {
        password = null;
        var session = new ProtectedProfileSession(broker, notifications);
        try
        {
            password = session.Begin();
            if (password is not null) return session;
            session.Dispose(); // Cancelled; detach only if we attached the still-locked volume.
            return null;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private char[]? Begin()
    {
        var state = broker.QueryRegisteredVolume();
        if (state is null)
        {
            // Claim cleanup responsibility before sending Attach: the reply may be lost after success.
            attachedHere = true;
            broker.Attach();
            state = broker.QueryRegisteredVolume();
        }
        if (state is { Unlocked: true })
            throw new ProfileLocationException("The volume is already unlocked; a new browser must authenticate.");
        if (state is not { ReadyToUnlock: true })
            throw new ProfileLocationException("The registered volume cannot be unlocked safely.");

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (notifications.LockRequested) return null;
            char[]? secret = ProfileUnlockDialog.Prompt();
            if (secret is null) return null;
            try
            {
                if (notifications.LockRequested) return null;
                unlockAttempted = true; // Even a lost reply may follow a successful unlock.
                if (broker.Unlock(secret))
                {
                    state = broker.QueryRegisteredVolume();
                    if (state is not { FullyEncrypted: true, ProtectionOn: true, Unlocked: true, ProtectorPresent: true })
                        throw new ProfileLocationException("The volume lost protected status during unlock.");
                    if (string.IsNullOrWhiteSpace(state.VolumeGuid))
                        throw new ProfileLocationException("The authenticated volume identity is missing.");
                    authenticatedVolumeGuid = state.VolumeGuid;
                    var accepted = secret;
                    secret = null; // Transfer ownership to the startup caller for vault unlock.
                    return accepted;
                }
            }
            finally
            {
                ClearPassword(secret);
            }
            if (attempt < 2)
                MessageBox.Show("잠금 해제 암호가 올바르지 않습니다.", "VEIL 보호 프로필",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        throw new ProfileLocationException("Too many failed unlock attempts.");
    }

    // Only a future CEF preparation caller uses this. Existing WebView2 startup
    // is unchanged; call instead of opening a second product profile.lock.
    internal CefPreparationLease AcquireCefPreparationLease()
        => CefPreparationLease.Acquire(this);

    private void RequireCefPreparationAuthority(string root)
    {
        if (Environment.CurrentManagedThreadId != ownerThread ||
            disposed || notifications.LockRequested || authenticatedVolumeGuid is null)
            throw new ProfileLocationException("The authenticated CEF preparation session is unavailable.");
        // Ordinary volume Status does not prove the requesting desktop owns
        // the unlock. Require a live broker owner attestation on both sides
        // of filesystem verification, without changing WebView2 startup.
        RequireBrokerOwner();
        var location = ProfileGate.Verify(root, broker, requireProtected: true);
        if (!string.Equals(location.VolumeGuid, authenticatedVolumeGuid, StringComparison.OrdinalIgnoreCase))
            throw new ProfileLocationException("The authenticated CEF volume changed.");
        RequireBrokerOwner();
        if (disposed || notifications.LockRequested)
            throw new ProfileLocationException("The authenticated CEF session changed.");
    }

    private void RequireBrokerOwner()
    {
        var owner = broker.QueryAuthenticatedOwner();
        if (owner is not { FullyEncrypted: true, ProtectionOn: true, Unlocked: true, ProtectorPresent: true } ||
            !string.Equals(owner.VolumeGuid, authenticatedVolumeGuid, StringComparison.OrdinalIgnoreCase))
            throw new ProfileLocationException("The broker did not attest the authenticated CEF owner.");
    }

    // Owns the exclusive file handle. Same dispatcher thread only.
    // Dispose before disposing the parent session. This is not launch authority.
    internal sealed class CefPreparationLease : IDisposable
    {
        private readonly ProtectedProfileSession session;
        private readonly FileStream handle;
        private bool released;

        internal string ProductRoot { get; }

        internal static CefPreparationLease Acquire(ProtectedProfileSession session)
        {
            var root = session.broker.RegisteredProductProfilePath ??
                throw new ProfileLocationException("CEF requires a registered product profile.");
            ProfileGate.RejectElevated();
            session.RequireCefPreparationAuthority(root);
            ProfileGate.RejectRedirectedChildren(root);
            session.RequireCefPreparationAuthority(root);
            var handle = new FileStream(Path.Combine(root, "profile.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            try
            {
                session.RequireCefPreparationAuthority(root);
                return new CefPreparationLease(session, root, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private CefPreparationLease(ProtectedProfileSession session, string root, FileStream handle)
        {
            this.session = session;
            ProductRoot = root;
            this.handle = handle;
        }

        internal bool StillAuthorized()
        {
            if (Environment.CurrentManagedThreadId != session.ownerThread || released ||
                handle.SafeFileHandle.IsClosed || handle.SafeFileHandle.IsInvalid)
                return false;
            session.RequireCefPreparationAuthority(ProductRoot);
            return true;
        }

        // This plan uses the same authenticated broker as the lease. The
        // caller owns the lease and must dispose it before the parent session.
        // Returned paths/environment are a snapshot, never launch authority.
        internal IReadOnlyDictionary<string, string?> PrepareChildEnvironment()
        {
            RequireCurrentPreparation();
            var layout = CefProtectedProfilePreparation.Prepare(ProductRoot,
                session.broker, StillAuthorized);
            RequireCurrentPreparation();

            var inherited = new List<KeyValuePair<string, string?>>();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string key || entry.Value is not string value)
                    throw new InvalidOperationException("Unexpected process environment entry.");
                inherited.Add(new KeyValuePair<string, string?>(key, value));
            }

            var plan = CefChildEnvironmentPlan.Build(layout, inherited);
            RequireCurrentPreparation();
            return plan;
        }

        // Stage exactly one already-contained suspended child. This does not
        // resume it or authorize CEF; the future product coordinator must
        // bind image trust, redemption, runtime lifetime and shutdown.
        internal void StageSuspendedChild(CefContainedProcess child)
        {
            ArgumentNullException.ThrowIfNull(child);
            try
            {
                RequireCurrentPreparation();
                long nominated = child.NominatedHandle;
                session.broker.StageCefChild(nominated, session.authenticatedVolumeGuid!);
                RequireCurrentPreparation();
                if (child.NominatedHandle != nominated)
                    throw new ProfileLocationException("The nominated CEF child changed.");
            }
            catch
            {
                // Ambiguous reply: never retry or resume. The broker's staged
                // verification expires independently; close the child job now.
                child.Dispose();
                throw;
            }
        }

        private void RequireCurrentPreparation()
        {
            if (!StillAuthorized())
                throw new ProfileLocationException("The CEF preparation lease is no longer available.");
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != session.ownerThread)
                throw new InvalidOperationException("Release the CEF preparation lease on its owning thread.");
            if (released) return;
            released = true;
            handle.Dispose();
        }
    }

    internal static void ClearPassword(char[]? password) =>
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));

    /// <summary>Best-effort non-forced relock; broker's process-exit monitor covers crashes.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        authenticatedVolumeGuid = null;
        if (!attachedHere && !unlockAttempted) return;
        try
        {
            var state = broker.QueryRegisteredVolume();
            if (unlockAttempted && state is { Unlocked: true })
            {
                broker.Lock(); // Refuses if WebView2 still has open handles; never force dismount.
                state = broker.QueryRegisteredVolume();
            }
            if (unlockAttempted && state is not { Unlocked: false })
                throw new ProfileLocationException("The volume lock could not be verified.");
            if (attachedHere && state is { Unlocked: false })
                broker.Detach();
            else if (attachedHere && state is null)
                throw new ProfileLocationException("The attached volume state could not be read.");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or
            InvalidOperationException or System.Text.Json.JsonException)
        {
            // During a Windows lock/suspend, let the process exit so the broker can retry.
            if (notifications.LockRequested) return;
            MessageBox.Show("보호 저장소의 자동 잠금을 확인하지 못했습니다. 저장소 상태를 확인하세요.",
                "VEIL 잠금 확인 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
