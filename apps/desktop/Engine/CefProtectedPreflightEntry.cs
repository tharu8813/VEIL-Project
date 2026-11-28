// SPDX-License-Identifier: MPL-2.0
#if VEIL_CEF_PREFLIGHT_ONLY
using System.IO;
using System.Windows;

namespace Veil.Desktop;

// Only compiled in the separate CefPreflight configuration. No product
// browsing, external activation forwarding, development broker or fallback.
internal static class CefProtectedPreflightEntry
{
    // Distinct exit status: layout provisioning is not a verified candidate.
    internal const uint ProfilePreparedOnly = 30;
    internal const uint SuspendedProbeVerified = 31;

    internal static int Run(string[] args)
    {
        bool prepareOnly = args is ["--cef-prepare-profile"];
        bool brokerProbe = args is ["--cef-broker-suspended-probe"];
        if ((!prepareOnly && !brokerProbe && args is not ["--cef-protected-preflight"]) || !ExternalAddress.Installed)
            return 2;
        uint expected = brokerProbe ? SuspendedProbeVerified :
            prepareOnly ? ProfilePreparedOnly : CefDisabledPreflight.VerifiedButDisabled;
        try
        {
            ProfileGate.RejectElevated();
            using var activation = new BrowserActivation();
            if (!activation.Primary) return 1;

            var config = BrokerClientConfig.Load(BrokerClientConfig.ProductPath, development: false);
            var broker = new NamedPipeVolumeBroker(config);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var pump = new CefPreflightNotificationPump(application.Dispatcher, () => application.Shutdown());
            var notifications = pump.Notifications;
            if (notifications.LockRequested) return 1;
            ProtectedProfileSession? session = null;
            char[]? password = null;
            uint result = 0;
            try
            {
                session = ProtectedProfileSession.TryOpen(broker, notifications, out password);
                ProtectedProfileSession.ClearPassword(password);
                password = null;
                if (session is null || notifications.LockRequested) return 1;
                if (brokerProbe)
                {
                    session.RunBrokerSuspendedProbe();
                    result = SuspendedProbeVerified;
                }
                else if (prepareOnly)
                {
                    // Release the desktop lock before non-forced relock.
                    // The next broker-first attempt requires a NEW unlock,
                    // not a transfer of this session or this handle.
                    using (var profile = session.AcquireCefPreparationLease())
                        profile.PrepareLayoutOnly();
                    result = ProfilePreparedOnly;
                }
                else
                {
                    result = CefDisabledLaunchCoordinator.RunPreflight(session);
                }
                if (result != expected || notifications.LockRequested) return 1;
                if (!brokerProbe) session.CompleteCefPreflightCleanup();
            }
            finally
            {
                ProtectedProfileSession.ClearPassword(password);
                session?.Dispose();
            }
            // Only explicit verified cleanup above can reach this point.
            // Best-effort Dispose alone is never sufficient for success.
            return !notifications.LockRequested && result == expected
                ? (int)expected : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception or
            System.Text.Json.JsonException or TimeoutException)
        {
            return 1;
        }
    }
}
#endif
