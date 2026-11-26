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

    public IBrowserEngineView CreateView(string? privateProfileName = null) =>
        new WebView2EngineView(Environment ?? throw new InvalidOperationException("The engine is not initialized."), privateProfileName);
}

/// <summary>Transitional adapter. No navigation occurs in the constructor or controller initialization.</summary>
internal sealed class WebView2EngineView(CoreWebView2Environment environment, string? privateProfileName = null) : IBrowserEngineView
{
    internal WebView2 NativeView { get; } = new() { Visibility = Visibility.Hidden };
    public FrameworkElement Element => NativeView;
    public event EventHandler<EngineNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<EngineNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler? SourceChanged;
    public event EventHandler? TitleChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? FaviconChanged;
    public event EventHandler? AudioStateChanged;
    public event EventHandler<EngineProcessFailedEventArgs>? ProcessFailed;
    public event Func<EngineNewWindowRequest, Task>? NewWindowRequested;
    public event EventHandler? WindowCloseRequested;
    public bool? IsDocumentPlayingAudio => disposed ? null : NativeView.CoreWebView2?.IsDocumentPlayingAudio;
    public bool? IsMuted => disposed ? null : NativeView.CoreWebView2?.IsMuted;
    public void SetMuted(bool muted) => Core.IsMuted = muted;
    public async Task<Stream?> GetFaviconPngAsync()
    {
        var core = Core;
        return string.IsNullOrEmpty(core.FaviconUri) ? null :
            await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
    }
    public string Source => Core.Source;
    public string DocumentTitle => Core.DocumentTitle;
    private bool disposed;
    private CoreWebView2 Core => !disposed && NativeView.CoreWebView2 is { } core
        ? core : throw new InvalidOperationException("The engine view is unavailable.");

    public async Task InitializeAsync()
    {
        if (disposed) throw new ObjectDisposedException(nameof(WebView2EngineView));
        if (privateProfileName is null)
        {
            await NativeView.EnsureCoreWebView2Async(environment);
            if (Core.Profile.IsInPrivateModeEnabled) throw new InvalidOperationException("Unexpected private controller.");
        }
        else
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = privateProfileName;
            options.IsInPrivateModeEnabled = true;
            await NativeView.EnsureCoreWebView2Async(environment, options);
            if (!Core.Profile.IsInPrivateModeEnabled ||
                !string.Equals(Core.Profile.ProfileName, privateProfileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Private controller verification failed.");
        }
        var core = Core;
        core.NavigationStarting -= OnNavigationStarting;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged -= OnSourceChanged;
        core.SourceChanged += OnSourceChanged;
        core.DocumentTitleChanged -= OnTitleChanged;
        core.DocumentTitleChanged += OnTitleChanged;
        core.HistoryChanged -= OnHistoryChanged;
        core.HistoryChanged += OnHistoryChanged;
        core.FaviconChanged -= OnFaviconChanged;
        core.FaviconChanged += OnFaviconChanged;
        core.IsDocumentPlayingAudioChanged -= OnAudioStateChanged;
        core.IsDocumentPlayingAudioChanged += OnAudioStateChanged;
        core.IsMutedChanged -= OnAudioStateChanged;
        core.IsMutedChanged += OnAudioStateChanged;
        core.ProcessFailed -= OnProcessFailed;
        core.ProcessFailed += OnProcessFailed;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WindowCloseRequested -= OnWindowCloseRequested;
        core.WindowCloseRequested += OnWindowCloseRequested;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        var navigation = new EngineNavigationStartingEventArgs(args.Uri, args.NavigationId, args.IsRedirected)
        {
            Cancel = args.Cancel,
        };
        NavigationStarting?.Invoke(this, navigation);
        args.Cancel = navigation.Cancel;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
        NavigationCompleted?.Invoke(this, new EngineNavigationCompletedEventArgs(args.NavigationId,
            args.IsSuccess, args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled,
            (int)args.WebErrorStatus));

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args) =>
        SourceChanged?.Invoke(this, EventArgs.Empty);
    private void OnTitleChanged(object? sender, object args) =>
        TitleChanged?.Invoke(this, EventArgs.Empty);
    private void OnHistoryChanged(object? sender, object args) =>
        HistoryChanged?.Invoke(this, EventArgs.Empty);

    private void OnFaviconChanged(object? sender, object args) =>
        FaviconChanged?.Invoke(this, EventArgs.Empty);
    private void OnAudioStateChanged(object? sender, object args) =>
        AudioStateChanged?.Invoke(this, EventArgs.Empty);

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args) =>
        ProcessFailed?.Invoke(this, new EngineProcessFailedEventArgs(
            args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited
                ? EngineFailureScope.BrowserSession : EngineFailureScope.PageOrChildProcess,
            (int)args.ProcessFailedKind));

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        args.Handled = true;
        var handler = NewWindowRequested;
        if (disposed || handler is null || handler.GetInvocationList().Length != 1) return;
        var request = new EngineNewWindowRequest(args.Uri, args.IsUserInitiated, target =>
        {
            if (disposed || target is not WebView2EngineView view || ReferenceEquals(view, this) ||
                view.disposed || !ReferenceEquals(view.environmentForPopup, environment) ||
                !string.Equals(view.privateProfileForPopup, privateProfileName, StringComparison.OrdinalIgnoreCase) ||
                view.NativeView.CoreWebView2 is not { } destination) return false;
            args.NewWindow = destination;
            return true;
        });
        try { await handler(request); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Engine popup request failed: {0}", error.GetType().Name);
        }
        finally { request.Complete(); }
    }

    private readonly CoreWebView2Environment environmentForPopup = environment;
    private readonly string? privateProfileForPopup = privateProfileName;
    private void OnWindowCloseRequested(object? sender, object args) =>
        WindowCloseRequested?.Invoke(this, EventArgs.Empty);

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
        if (NativeView.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.SourceChanged -= OnSourceChanged;
            core.DocumentTitleChanged -= OnTitleChanged;
            core.HistoryChanged -= OnHistoryChanged;
            core.FaviconChanged -= OnFaviconChanged;
            core.IsDocumentPlayingAudioChanged -= OnAudioStateChanged;
            core.IsMutedChanged -= OnAudioStateChanged;
            core.ProcessFailed -= OnProcessFailed;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.WindowCloseRequested -= OnWindowCloseRequested;
        }
        NativeView.Dispose();
    }
}
