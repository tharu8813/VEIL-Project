// SPDX-License-Identifier: MPL-2.0
#if VEIL_CEF_PREFLIGHT_ONLY
using System.IO;
using System.Windows;

namespace Veil.Desktop;

// Only compiled in the separate CefPreflight configuration. No product
// browsing, external activation forwarding, development broker or fallback.
internal static class CefProtectedPreflightEntry
{
    internal static int Run(string[] args)
    {
        if (args is not ["--cef-protected-preflight"] || !ExternalAddress.Installed)
            return 2;
        try
        {
            ProfileGate.RejectElevated();
            using var activation = new BrowserActivation();
            if (!activation.Primary) return 1;

            var config = BrokerClientConfig.Load(BrokerClientConfig.ProductPath, development: false);
            var broker = new NamedPipeVolumeBroker(config);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var notifications = new ProtectedSessionNotifications(() => application.Shutdown());
            ProtectedProfileSession? session = null;
            char[]? password = null;
            uint result = 0;
            try
            {
                session = ProtectedProfileSession.TryOpen(broker, notifications, out password);
                ProtectedProfileSession.ClearPassword(password);
                password = null;
                if (session is null || notifications.LockRequested) return 1;
                result = CefDisabledLaunchCoordinator.RunPreflight(session);
            }
            finally
            {
                ProtectedProfileSession.ClearPassword(password);
                session?.Dispose();
            }
            // Dispose may report a lock failure without throwing. Do not
            // publish verification success until registered status is closed.
            var final = broker.QueryRegisteredVolume();
            return final is not { Unlocked: true } &&
                !notifications.LockRequested && result == CefDisabledPreflight.VerifiedButDisabled
                ? (int)CefDisabledPreflight.VerifiedButDisabled : 1;
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
