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

    internal static void ClearPassword(char[]? password) =>
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));

    /// <summary>Best-effort non-forced relock; broker's process-exit monitor covers crashes.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
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
