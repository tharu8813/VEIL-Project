using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

internal static class Program
{
    // ADR-0003: which volume authority the startup gate consults. Smoke and probe runs stay on the
    // unmanaged development volume; a product install with %ProgramData%\VEIL\client.json uses the broker.
    private static IVolumeBroker volume = UnmanagedDevelopmentVolume.Instance;
    private static bool requireProtected;

    [STAThread]
    private static int Main(string[] args)
    {
#if !DEBUG
        // Release builds must never fall back to the unencrypted developer profile or test modes.
        if (args.Length != 0) return 2;
#endif
        if (args is ["--broker-status", var clientConfig, var gateProfile, var gateReport])
            return BrokerStatus(clientConfig, gateProfile, gateReport);
        var application = new Application();
        if (args is ["--probe", var profile, var origin, var action, var report])
            return application.Run(new EngineProbe(profile, origin, action, report));
        if (args is ["--probe-proxy", var proxyProfile, var proxyOrigin, var proxyPort, var proxyReport] &&
            ushort.TryParse(proxyPort, out var port) && port > 0)
            return application.Run(new EngineProbe(proxyProfile, proxyOrigin, "proxy", proxyReport, port));

        bool smoke = args is ["--smoke", _, _, _, _];
        if (!smoke && args.Length != 0) return 2;
        ProtectedProfileSession? protectedSession = null;
        ProtectedSessionNotifications? notifications = null;
        BrowserWindow? window = null;
        string? scratch = null;
        char[]? profilePassword = null;
        try
        {
            ProfileGate.RejectElevated();
#if !DEBUG
            if (!File.Exists(BrokerClientConfig.ProductPath))
                throw new ProfileLocationException("Protected profile registration is missing.");
#endif
            string? productProfile = null;
            if (!smoke && File.Exists(BrokerClientConfig.ProductPath))
            {
                var client = BrokerClientConfig.Load(BrokerClientConfig.ProductPath, development: false);
                productProfile = client.ProfilePath ?? throw new InvalidDataException("No registered profile path.");
                var broker = new NamedPipeVolumeBroker(client);
                volume = broker;
                requireProtected = true;
                // The password dialog runs before the browser window. Prevent early WPF shutdown.
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                notifications = new ProtectedSessionNotifications(() =>
                {
                    try { window?.PrepareForProtectedShutdown(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        // Preserve the previous snapshot if the final write fails, then close all views.
                    }
                    finally { application.Shutdown(); }
                });
                protectedSession = ProtectedProfileSession.TryOpen(broker, notifications, out profilePassword);
                if (protectedSession is null || notifications.LockRequested) return 0;
            }
            string root = Path.GetFullPath(smoke ? args[1] : productProfile ?? Path.Combine(".veil-dev", "desktop"));
            ProfileGate.Prepare(root, volume, requireProtected);
            using var profileLock = new FileStream(Path.Combine(root, "profile.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            using var vault = new DownloadVault(root, usesProfilePassword: requireProtected);
            if (profilePassword is not null)
            {
                bool vaultFailed = false;
                try
                {
                    string vaultRoot = vault.Directory;
                    foreach (string directory in new[] { vaultRoot, Path.Combine(vaultRoot, "objects"),
                        Path.Combine(vaultRoot, "index"), Path.Combine(vaultRoot, "meta") })
                    {
                        if (Directory.Exists(directory)) ProfileGate.Verify(directory, volume, requireProtected: true);
                    }
                    ProtectedVaultStartup.TryOpen(vault, profilePassword, () => notifications!.LockRequested);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
                {
                    vaultFailed = true;
                    vault.Lock();
                }
                finally
                {
                    ProtectedProfileSession.ClearPassword(profilePassword);
                    profilePassword = null;
                }
                if (notifications!.LockRequested) return 0;
                if (vaultFailed) MessageBox.Show("다운로드 금고를 열지 못했습니다. 탐색은 계속할 수 있으며 다운로드는 차단됩니다.",
                    "다운로드 금고", MessageBoxButton.OK, MessageBoxImage.Warning);
                if (notifications.LockRequested) return 0;
            }
            window = new BrowserWindow(root, vault);
            // SEC-004: temp files of the app and engine stay on the profile volume (after state loads,
            // so a refused profile is left exactly as found).
            scratch = ProfileScratch.Redirect(root);
            if (smoke) window.ConfigureSmoke(args[2], args[3], args[4]);
            application.MainWindow = window;
            application.ShutdownMode = ShutdownMode.OnLastWindowClose;
            return application.Run(window);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        {
            ProtectedProfileSession.ClearPassword(profilePassword);
            if (smoke) File.WriteAllText(args[4], System.Text.Json.JsonSerializer.Serialize(new { error = error.GetType().Name }));
            else if (notifications?.LockRequested != true) MessageBox.Show("프로필을 안전하게 열 수 없습니다. 보호 저장소와 브로커 상태를 확인하세요. 비보호 프로필로 전환하지 않습니다.",
                "VEIL 보호 프로필", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        finally
        {
            ProtectedProfileSession.ClearPassword(profilePassword);
            if (scratch is not null)
            {
                try { ProfileScratch.Clear(scratch); }
                catch (ProfileLocationException) { }
            }
            try { protectedSession?.Dispose(); }
            finally { notifications?.Dispose(); }
        }
    }

    /// <summary>Development check of the gate against a running broker; never starts the engine.</summary>
    private static int BrokerStatus(string clientConfig, string profile, string report)
    {
        object result;
        try
        {
            ProfileGate.RejectElevated();
            var broker = new NamedPipeVolumeBroker(BrokerClientConfig.Load(clientConfig, development: true));
            var location = ProfileGate.Verify(profile, broker, requireProtected: true);
            result = new { error = (string?)null, location.Protected, location.VolumeGuid };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidDataException)
        {
            result = new { error = $"{error.GetType().Name}: {error.Message}" };
        }
        File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(result));
        return result.GetType().GetProperty("error")!.GetValue(result) is null ? 0 : 1;
    }

    internal static async Task<CoreWebView2Environment> CreateEngine(
        string profile, ushort? loopbackProxyPort = null, bool observeDownloadContent = false, NetworkSettings? network = null)
    {
        // WebView2 environment overrides can replace the profile path or inject browser flags.
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
        {
            if (key is string name && name.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WebView2 환경 재정의 설정을 제거한 뒤 다시 실행하세요.");
        }

        ProfileGate.RejectElevated();
        string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        ProfileGate.Prepare(expected, volume, requireProtected);
        // Reject this collision before the runtime can display a native error dialog.
        if (File.Exists(Path.Combine(expected, "EBWebView")))
            throw new IOException("The WebView2 profile directory is obstructed by a file.");
        var arguments = new List<string>();
        if (loopbackProxyPort is ushort port)
            arguments.Add($"--proxy-server=http://127.0.0.1:{port} --proxy-bypass-list=<-loopback>");
        if (observeDownloadContent)
            arguments.Add("--enable-features=msWebView2EnableDownloadContentInWebResourceResponseReceived");
        // ADR-0006: proxy mode pins the engine to the proxy (no DIRECT fallback), limits WebRTC, disables QUIC.
        arguments.AddRange(NetworkPolicy.BrowserArguments(network ?? NetworkSettings.Standard));
        var options = new CoreWebView2EnvironmentOptions
        {
            ExclusiveUserDataFolderAccess = true,
            AdditionalBrowserArguments = arguments.Count == 0 ? null : string.Join(' ', arguments),
        };
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: expected, options: options);
        string reported = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder));
        if (!string.Equals(reported, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected WebView2 profile directory.");
        return environment;
    }
}
