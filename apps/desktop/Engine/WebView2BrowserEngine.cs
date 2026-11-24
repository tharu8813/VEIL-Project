using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Veil.Desktop;

/// <summary>
/// The only enabled backend. Reuses Program.CreateEngine so profile verification, environment override
/// rejection, scratch redirection, DNS and proxy configuration cannot diverge from the existing path.
/// One initialization task per session; an unsuccessful start is not retried using another backend.
/// </summary>
internal sealed class WebView2BrowserEngine : IBrowserEngine
{
    private Task<CoreWebView2Environment>? initialization;
    private string? profileRoot;
    private NetworkSettings? appliedNetwork;
    internal CoreWebView2Environment? Environment { get; private set; }

    public string DisplayName => "Microsoft Edge WebView2";
    public string? RuntimeVersion => Environment?.BrowserVersionString;
    public bool IsReady => Environment is not null;

    public async Task InitializeAsync(string protectedProfileRoot, NetworkSettings network)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedProfileRoot));
        if (initialization is null)
        {
            profileRoot = root;
            appliedNetwork = network;
            initialization = Program.CreateEngine(Path.Combine(root, "webview2"), network: network);
        }
        else if (!string.Equals(profileRoot, root, StringComparison.OrdinalIgnoreCase) || appliedNetwork != network)
            throw new InvalidOperationException("A browser session cannot change its engine profile or network policy.");
        Environment = await initialization;
    }

    public IBrowserEngineView CreateView() =>
        new WebView2EngineView(Environment ?? throw new InvalidOperationException("The engine is not initialized."));
}

/// <summary>Transitional adapter. No navigation occurs in the constructor or controller initialization.</summary>
internal sealed class WebView2EngineView(CoreWebView2Environment environment) : IBrowserEngineView
{
    internal WebView2 NativeView { get; } = new() { Visibility = Visibility.Hidden };
    public FrameworkElement Element => NativeView;
    private bool disposed;
    private CoreWebView2 Core => !disposed && NativeView.CoreWebView2 is { } core
        ? core : throw new InvalidOperationException("The engine view is unavailable.");

    public async Task InitializeAsync()
    {
        if (disposed) throw new ObjectDisposedException(nameof(WebView2EngineView));
        await NativeView.EnsureCoreWebView2Async(environment);
    }

    public bool CanGoBack => !disposed && NativeView.CoreWebView2?.CanGoBack == true;
    public bool CanGoForward => !disposed && NativeView.CoreWebView2?.CanGoForward == true;
    public double ZoomFactor { get => NativeView.ZoomFactor; set => NativeView.ZoomFactor = value; }

    // The host wires existing NavigationStarting guards before issuing any nonblank navigation.
    public void Navigate(string address)
    {
        if (address != "about:blank" && !BrowserWindow.IsWebUrl(address))
            throw new ArgumentException("Unsupported navigation address.", nameof(address));
        Core.Navigate(address);
    }
    public void ShowInternalHtml(string trustedHostHtml) => Core.NavigateToString(trustedHostHtml);
    public void GoBack() => NativeView.GoBack();
    public void GoForward() => NativeView.GoForward();
    public void Reload() => NativeView.Reload();
    public void Stop() => Core.Stop();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        NativeView.Dispose();
    }
}
