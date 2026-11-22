using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application();
        if (args is ["--probe", var profile, var origin, var action, var report])
            return application.Run(new EngineProbe(profile, origin, action, report));
        if (args is ["--probe-proxy", var proxyProfile, var proxyOrigin, var proxyPort, var proxyReport] &&
            ushort.TryParse(proxyPort, out var port) && port > 0)
            return application.Run(new EngineProbe(proxyProfile, proxyOrigin, "proxy", proxyReport, port));

        bool smoke = args is ["--smoke", _, _, _, _];
        if (!smoke && args.Length != 0) return 2;
        var root = Path.GetFullPath(smoke ? args[1] : Path.Combine(".veil-dev", "desktop"));
        try
        {
            Directory.CreateDirectory(root);
            using var profileLock = new FileStream(Path.Combine(root, "profile.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var window = new BrowserWindow(root);
            if (smoke) window.ConfigureSmoke(args[2], args[3], args[4]);
            return application.Run(window);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (smoke) File.WriteAllText(args[4], System.Text.Json.JsonSerializer.Serialize(new { error = error.GetType().Name }));
            else MessageBox.Show("테스트 프로필을 열 수 없습니다. 실행 중인 VEIL 창과 저장 위치를 확인하세요.", "VEIL 개발자용");
            return 1;
        }
    }

    internal static async Task<CoreWebView2Environment> CreateEngine(
        string profile, ushort? loopbackProxyPort = null, bool observeDownloadContent = false)
    {
        // WebView2 environment overrides can replace the profile path or inject browser flags.
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
        {
            if (key is string name && name.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WebView2 환경 재정의 설정을 제거한 뒤 다시 실행하세요.");
        }

        string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        Directory.CreateDirectory(expected);
        var options = new CoreWebView2EnvironmentOptions { ExclusiveUserDataFolderAccess = true };
        if (loopbackProxyPort is ushort port)
            options.AdditionalBrowserArguments = $"--proxy-server=http://127.0.0.1:{port} --proxy-bypass-list=<-loopback>";
        if (observeDownloadContent)
            options.AdditionalBrowserArguments = "--enable-features=msWebView2EnableDownloadContentInWebResourceResponseReceived";
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: expected, options: options);
        string reported = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder));
        if (!string.Equals(reported, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected WebView2 profile directory.");
        return environment;
    }
}
