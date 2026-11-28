// SPDX-License-Identifier: MPL-2.0
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Veil.Desktop;

namespace Veil.Cef;

// Explicit opt-in development shell. The product BrowserWindow is unchanged.
internal sealed class TabbedDevelopmentHost
{
    private sealed class Tab(CefDevelopmentView view, Button header)
    {
        internal readonly CefDevelopmentView View = view;
        internal readonly Button Header = header;
        internal ulong ClosingId;
        internal bool Closing;
        internal string LastSource = "";
        internal string Status = "";
        internal ulong PendingNavigationId;
        internal ulong LastNavigationId;
    }

    private sealed record FindRequest(Tab Tab, ulong ViewId, ulong NavigationId, string Source, string Query);
    private FindRequest? currentFind;
    private bool findInFlight;
    private Task? fixtureFind;
    private readonly DockPanel findBar = new() { Margin = new Thickness(6), Visibility = Visibility.Collapsed };
    private readonly TextBox findQuery = new() { MaxLength = 256, MinWidth = 180 };
    private readonly Button findOpen = new() { Content = "Find" };
    private readonly Button findSearch = new() { Content = "Search" };
    private readonly Button findClose = new() { Content = "Close find" };
    private readonly TextBlock findResult = new() { Margin = new Thickness(8, 0, 8, 0) };

    private readonly Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    private readonly Window window = new() { Width = 1000, Height = 700, Title = "VEIL CEF development tabs" };
    private readonly DockPanel layout = new();
    private readonly StackPanel strip = new() { Orientation = Orientation.Horizontal };
    private readonly Grid pages = new();
    private readonly DockPanel toolbar = new() { Margin = new Thickness(6) };
    private readonly Button fullscreenToggle = new() { Content = "Full screen (F11)" };
    private readonly Button pageFullscreenExit = new() { Content = "Exit page full screen", Visibility = Visibility.Collapsed };
    private readonly TextBlock fullscreenNotice = new() { Margin = new Thickness(6) };
    private bool browserFullscreen, pageExitPending;
    private WindowStyle savedWindowStyle;
    private WindowState savedWindowState;
    private ResizeMode savedResizeMode;
    private readonly TextBox address = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Margin = new Thickness(6) };
    private readonly TextBlock popupNotice = new() { Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
    private readonly Button add = new() { Content = "+" };
    private readonly Button close = new() { Content = "Close tab" };
    private readonly Button back = new() { Content = "Back" };
    private readonly Button forward = new() { Content = "Forward" };
    private readonly Button reload = new() { Content = "Reload" };
    private readonly Button go = new() { Content = "Go" };
    private readonly Button zoomOut = new() { Content = "-" };
    private readonly Button zoomIn = new() { Content = "+" };
    private readonly Button zoomReset = new() { Content = "100%" };
    private readonly TextBlock zoomLabel = new() { MinWidth = 48, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Tab> tabs = new();
    private readonly List<ulong> retired = new();
    private readonly CefDevelopmentEngine engine = new();
    private readonly DispatcherTimer pump = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
    private readonly string? fixture;
    private readonly bool freshProfileProbe;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    private Tab? active;
    private Tab? first, second;
    private ulong firstId, secondId;
    private Task<IReadOnlyList<string>>? emptyDomains, firstDomains, secondDomains;
    private Task<int?>? cookieDeletion;
    private Task<bool>? profileCookieDeletion;
    private Task<double?>? firstUsage, secondUsage, rejectedUsage;
    private Task<bool>? storageClear, httpCacheClear;
    private Task<int>? knownOriginClear;
    private Task<bool?>? loginProbe, duplicateLoginProbe;
    private Tab? loginSpare;
    private int lifetimeAuthorizationChecks;
    private ulong closingLoginId;
    private bool expectLoginRendererFailure;
    private int loginRendererFailures;
    private int loginProbeIndex, loginValuesIndex, loginFillIndex;
    private static readonly string[] LoginFillPaths = ["/tabs-login-fill-empty", "/tabs-login-fill-matching",
        "/tabs-login-fill-existing", "/tabs-login-fill-other", "/tabs-login-fill-denied"];
    private static readonly string[] LoginValuesPaths = ["/tabs-login-read", "/tabs-login-read-empty", "/tabs-login-read-mismatch"];
    private static readonly string[] LoginProbePaths = ["/tabs-login-valid", "/tabs-login-hidden", "/tabs-login-ambiguous", "/tabs-login-cross-origin", "/tabs-login-iframe", "/tabs-login-new"];
    private int deletionAuthorizationChecks;
    private int phase, result;
    private bool shuttingDown, windowClosed, allowWindowClose;

    private TabbedDevelopmentHost(string? fixture)
    {
        this.fixture = fixture;
        freshProfileProbe = fixture is not null && Environment.GetEnvironmentVariable("VEIL_CEF_WPF_TABS_FRESH_PROBE") == "1";
        result = fixture is null ? 0 : 40;
        engine.InitializeAsync(CefDevelopmentEngine.FixtureProfile, NetworkSettings.Standard).GetAwaiter().GetResult();

        DockPanel.SetDock(strip, Dock.Top);
        layout.Children.Add(strip);
        strip.Children.Add(add);
        strip.Children.Add(close);
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        foreach (var button in new[] { back, forward, reload })
        {
            DockPanel.SetDock(button, Dock.Left);
            toolbar.Children.Add(button);
        }
        DockPanel.SetDock(go, Dock.Right);
        toolbar.Children.Add(go);
        foreach (var element in new FrameworkElement[] { zoomReset, zoomIn, zoomLabel, zoomOut })
        {
            DockPanel.SetDock(element, Dock.Right);
            toolbar.Children.Add(element);
        }
        DockPanel.SetDock(findOpen, Dock.Right);
        toolbar.Children.Add(findOpen);
        findOpen.Visibility = fixture is null ? Visibility.Collapsed : Visibility.Visible;
        toolbar.Children.Add(address);
        DockPanel.SetDock(findBar, Dock.Top);
        layout.Children.Add(findBar);
        foreach (var element in new FrameworkElement[] { findClose, findSearch, findResult })
        {
            DockPanel.SetDock(element, Dock.Right);
            findBar.Children.Add(element);
        }
        findBar.Children.Add(findQuery);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(status);
        DockPanel.SetDock(popupNotice, Dock.Bottom);
        layout.Children.Add(popupNotice);
        // Always-visible host controls: a native page must never hide the
        // only escape route, even when WPF keyboard focus is unavailable.
        var fullscreenControls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        DockPanel.SetDock(fullscreenControls, Dock.Bottom);
        fullscreenControls.Children.Add(fullscreenToggle);
        fullscreenControls.Children.Add(pageFullscreenExit);
        fullscreenControls.Children.Add(fullscreenNotice);
        layout.Children.Add(fullscreenControls);
        layout.Children.Add(pages);
        window.Content = layout;

        add.Click += (_, _) => AddTab();
        close.Click += (_, _) => { if (active is { } tab) RequestClose(tab); };
        back.Click += (_, _) => WithActive(view => view.GoBack());
        forward.Click += (_, _) => WithActive(view => view.GoForward());
        reload.Click += (_, _) => WithActive(view => view.Reload());
        go.Click += (_, _) => NavigateAddress();
        zoomOut.Click += (_, _) => ChangeZoom(-0.25);
        zoomIn.Click += (_, _) => ChangeZoom(0.25);
        zoomReset.Click += (_, _) => ChangeZoom(0, reset: true);
        fullscreenToggle.Click += (_, _) => SetBrowserFullscreen(!browserFullscreen);
        pageFullscreenExit.Click += async (_, _) => await ExitActivePageFullscreenAsync();
        findOpen.Click += (_, _) => ShowFindBar(true);
        findClose.Click += (_, _) => ShowFindBar(false);
        findSearch.Click += async (_, _) => await SearchPageAsync(false);
        findQuery.TextChanged += (_, _) => InvalidateFindUi();
        findQuery.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SearchPageAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        };
        // WPF-routed keys only; keyboard focus within native CEF HWND still
        // requires separate integration and runtime verification.
        window.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
            {
                SetBrowserFullscreen(!browserFullscreen);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && active?.View.IsPageFullScreen == true)
            {
                e.Handled = true;
                await ExitActivePageFullscreenAsync();
            }
            else if (e.Key == Key.Escape && browserFullscreen)
            {
                SetBrowserFullscreen(false);
                e.Handled = true;
            }
            else if (fixture is not null && e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowFindBar(true);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control &&
                e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0)
            {
                ChangeZoom(e.Key is Key.OemMinus or Key.Subtract ? -0.25 : 0.25,
                    reset: e.Key is Key.D0 or Key.NumPad0);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && findBar.Visibility == Visibility.Visible)
            {
                ShowFindBar(false);
                e.Handled = true;
            }
        };
        address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { NavigateAddress(); e.Handled = true; }
        };
        window.Loaded += (_, _) => first = AddTab();
        window.Closing += (_, e) =>
        {
            if (allowWindowClose) return;
            e.Cancel = true;
            BeginShutdown();
        };
        window.Closed += (_, _) => windowClosed = true;
        if (fixture is not null)
        {
            window.Left = window.Top = -32000;
            window.ShowActivated = window.ShowInTaskbar = false;
        }
        pump.Tick += (_, _) => Tick();
    }

    internal static int Run(string? fixture)
    {
        var host = new TabbedDevelopmentHost(fixture);
        host.pump.Start();
        host.app.Run(host.window);
        return host.result;
    }

    private void Log(string message)
    {
        if (fixture is not null)
            System.IO.File.AppendAllText(System.IO.Path.Combine(CefDevelopmentEngine.FixtureProfile, "tabs.log"), message + Environment.NewLine);
    }

    private Tab? AddTab()
    {
        if (shuttingDown) return null;
        CefDevelopmentView view;
        try { view = (CefDevelopmentView)engine.CreateView(); }
        catch (InvalidOperationException) { status.Text = "Development view limit reached; wait for closing views."; return null; }
        var header = new Button { Content = "New tab", MinWidth = 95, MaxWidth = 240 };
        var tab = new Tab(view, header);
        header.Click += (_, _) => Select(tab);
        view.NavigationStarting += (_, request) =>
        {
            // Native and managed loopback restrictions remain in force.
            request.Cancel = !AcceptTabEvent(tab);
            if (request.Cancel) return;
            // Redirects retain the current ID; stale callbacks cannot publish
            // a new loading state for a completed or newer request.
            if (request.NavigationId == 0 ||
                (request.IsRedirected && tab.PendingNavigationId != request.NavigationId) ||
                (!request.IsRedirected && request.NavigationId <= tab.LastNavigationId))
            {
                request.Cancel = true;
                return;
            }
            tab.PendingNavigationId = request.NavigationId;
            tab.LastNavigationId = request.NavigationId;
            if (ReferenceEquals(active, tab)) InvalidateFindUi();
            SetTabStatus(tab, "Loading...");
        };
        view.NavigationCompleted += (_, completed) =>
        {
            if (!AcceptTabEvent(tab) || completed.NavigationId == 0 ||
                tab.PendingNavigationId != completed.NavigationId) return;
            tab.PendingNavigationId = 0;
            SetTabStatus(tab, completed.IsCanceled ? "Navigation canceled." :
                completed.IsSuccess ? "Page loaded." : "Page navigation failed.");
        };
        view.SourceChanged += (_, _) =>
        {
            if (AcceptTabEvent(tab) && ReferenceEquals(active, tab)) InvalidateFindUi();
        };
        view.ProcessFailed += (_, failure) =>
        {
            if (!AcceptTabEvent(tab)) return;
            tab.PendingNavigationId = 0;
            if (ReferenceEquals(active, tab)) InvalidateFindUi();
            SetTabStatus(tab, "CEF child process failed.");
            if (freshProfileProbe && expectLoginRendererFailure && phase == 81 && tab == first &&
                view.NativeHandle == firstId && failure.Scope == EngineFailureScope.PageOrChildProcess &&
                CefNative.VeilCefLoginProbeStatus(firstId) == -1 && loginRendererFailures == 0)
            {
                loginRendererFailures++;
                expectLoginRendererFailure = false;
                Log("Expected renderer failure invalidated the pending login observation");
                return;
            }
            if (fixture is not null) { result = 41; window.Dispatcher.BeginInvoke(new Action(BeginShutdown)); }
        };
        tabs.Add(tab);
        strip.Children.Insert(strip.Children.Count - 2, header);
        // Keep every HWND host parented to this Grid. A TabControl that swaps
        // content could destroy the old HwndHost and its native browser.
        pages.Children.Add(view.Element);
        Select(tab);
        return tab;
    }

    private bool AcceptTabEvent(Tab tab) =>
        !shuttingDown && !tab.Closing && tabs.Contains(tab);

    private void SetTabStatus(Tab tab, string message)
    {
        if (!AcceptTabEvent(tab)) return;
        tab.Status = message;
        if (ReferenceEquals(active, tab)) status.Text = message;
    }

    private void Select(Tab tab)
    {
        if (shuttingDown || tab.Closing || !tabs.Contains(tab)) return;
        if (!ReferenceEquals(active, tab))
        {
            active?.View.InvalidateFixtureLoginContext();
            tab.View.InvalidateFixtureLoginContext();
            InvalidateFindUi();
        }
        active = tab;
        foreach (var item in tabs)
        {
            item.View.Element.Visibility = ReferenceEquals(item, active) ? Visibility.Visible : Visibility.Hidden;
            item.Header.FontWeight = ReferenceEquals(item, active) ? FontWeights.Bold : FontWeights.Normal;
        }
        address.Text = tab.View.Source;
        tab.LastSource = tab.View.Source;
        status.Text = tab.Status;
        RefreshToolbar();
    }

    private void InvalidateFindUi()
    {
        currentFind = null;
        findResult.Text = "";
    }

    private void ShowFindBar(bool visible, bool focus = true)
    {
        if (visible && (fixture is null || shuttingDown || browserFullscreen || active is not { Closing: false })) return;
        InvalidateFindUi();
        findBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible && focus)
        {
            findQuery.Focus();
            findQuery.SelectAll();
        }
    }

    private bool CanPublishFind(FindRequest request) =>
        ReferenceEquals(currentFind, request) && findBar.Visibility == Visibility.Visible &&
        AcceptTabEvent(request.Tab) && ReferenceEquals(active, request.Tab) &&
        request.Tab.View.NativeHandle == request.ViewId && request.ViewId != 0 &&
        CefNative.VeilCefClosed(request.ViewId) == 0 && request.Tab.PendingNavigationId == 0 &&
        request.Tab.LastNavigationId == request.NavigationId &&
        request.Tab.View.Source == request.Source && findQuery.Text == request.Query;

    private void PublishFindResult(FindRequest request, bool? found)
    {
        if (!CanPublishFind(request)) return;
        findResult.Text = found switch { true => "Match found.", false => "No match.", _ => "Search unavailable." };
    }

    private async Task SearchPageAsync(bool backwards)
    {
        if (fixture is null || findInFlight || findBar.Visibility != Visibility.Visible ||
            active is not { } tab || !AcceptTabEvent(tab) || tab.View.NativeHandle == 0 ||
            tab.PendingNavigationId != 0 || string.IsNullOrWhiteSpace(findQuery.Text)) return;
        var request = new FindRequest(tab, tab.View.NativeHandle, tab.LastNavigationId,
            tab.View.Source, findQuery.Text);
        currentFind = request;
        if (!CanPublishFind(request)) return;
        findInFlight = true;
        findResult.Text = "Searching...";
        RefreshToolbar();
        try
        {
            bool? found = await tab.View.FindTextAsync(request.Query, backwards);
            PublishFindResult(request, found);
        }
        catch (InvalidOperationException)
        {
            PublishFindResult(request, null);
        }
        finally
        {
            findInFlight = false;
            RefreshToolbar();
        }
    }

    private void WithActive(Action<CefDevelopmentView> operation)
    {
        if (active is not { Closing: false } tab || tab.View.NativeHandle == 0 ||
            CefNative.VeilCefClosed(tab.View.NativeHandle) != 0) return;
        InvalidateFindUi();
        try { operation(tab.View); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { SetTabStatus(tab, error.Message); }
    }

    private void SetBrowserFullscreen(bool enabled)
    {
        if (enabled == browserFullscreen || (enabled && shuttingDown)) return;
        if (enabled)
        {
            savedWindowStyle = window.WindowStyle;
            savedWindowState = window.WindowState;
            savedResizeMode = window.ResizeMode;
            ShowFindBar(false);
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
        }
        else
        {
            window.WindowState = WindowState.Normal;
            window.WindowStyle = savedWindowStyle;
            window.ResizeMode = savedResizeMode;
            window.WindowState = savedWindowState;
        }
        browserFullscreen = enabled;
        strip.Visibility = toolbar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        fullscreenToggle.Content = enabled ? "Exit full screen (F11)" : "Full screen (F11)";
    }

    private async Task ExitActivePageFullscreenAsync()
    {
        if (pageExitPending || active is not { } tab || !AcceptTabEvent(tab) ||
            tab.View.IsPageFullScreen != true) return;
        ulong id = tab.View.NativeHandle, navigation = tab.LastNavigationId;
        pageExitPending = true;
        RefreshToolbar();
        try
        {
            bool exited = await tab.View.TryExitPageFullScreenAsync();
            if (!exited && AcceptTabEvent(tab) && ReferenceEquals(active, tab) &&
                tab.View.NativeHandle == id && tab.LastNavigationId == navigation &&
                tab.View.IsPageFullScreen == true)
                SetTabStatus(tab, "Page full screen exit not confirmed.");
        }
        finally
        {
            pageExitPending = false;
            RefreshToolbar();
        }
    }

    private void ChangeZoom(double delta, bool reset = false)
    {
        if (shuttingDown || active is not { } tab || !AcceptTabEvent(tab) ||
            tab.View.NativeHandle == 0 || CefNative.VeilCefClosed(tab.View.NativeHandle) != 0) return;
        try
        {
            double factor = tab.View.ZoomFactor;
            if (!double.IsFinite(factor) || factor <= 0) return;
            tab.View.ZoomFactor = reset ? 1.0 : Math.Clamp(factor + delta, 0.5, 3.0);
        }
        catch (InvalidOperationException)
        {
            SetTabStatus(tab, "Zoom unavailable.");
        }
        // Display actual native readback, not the requested value. Chromium
        // may share zoom within an origin; never pretend it is tab-private.
        RefreshToolbar();
    }

    private void NavigateAddress() => WithActive(view => view.Navigate(address.Text));

    private void RequestClose(Tab tab)
    {
        if (tab.Closing || !tabs.Contains(tab)) return;
        tab.Closing = true;
        tab.View.InvalidateFixtureLoginContext();
        tab.PendingNavigationId = 0;
        if (ReferenceEquals(active, tab)) ShowFindBar(false);
        tab.ClosingId = tab.View.NativeHandle;
        tab.Header.IsEnabled = false;
        CefNative.VeilCefCancelOwnedAttachment(tab.ClosingId);
        CefNative.VeilCefTryClose(tab.ClosingId);
        RefreshToolbar();
    }

    private void BeginShutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        SetBrowserFullscreen(false);
        ShowFindBar(false);
        foreach (var tab in tabs.ToArray()) RequestClose(tab);
    }

    private void RefreshToolbar()
    {
        var live = active is { Closing: false } tab && tab.View.NativeHandle != 0 &&
                   CefNative.VeilCefClosed(tab.View.NativeHandle) == 0;
        go.IsEnabled = reload.IsEnabled = address.IsEnabled = live && !shuttingDown;
        findOpen.IsEnabled = fixture is not null && live && !shuttingDown;
        findSearch.IsEnabled = findOpen.IsEnabled && !findInFlight && active!.PendingNavigationId == 0;
        back.IsEnabled = live && !shuttingDown && active!.View.CanGoBack;
        forward.IsEnabled = live && !shuttingDown && active!.View.CanGoForward;
        close.IsEnabled = active is { Closing: false } && !shuttingDown;
        double zoom = live && !shuttingDown ? active!.View.ZoomFactor : double.NaN;
        bool zoomKnown = double.IsFinite(zoom) && zoom > 0;
        zoomLabel.Text = zoomKnown ? $"{Math.Round(zoom * 100)}%" : "";
        zoomOut.IsEnabled = zoomKnown && zoom > 0.5 + 1e-6;
        zoomIn.IsEnabled = zoomKnown && zoom < 3.0 - 1e-6;
        zoomReset.IsEnabled = zoomKnown;
        bool pageFullscreen = live && !shuttingDown && active!.View.IsPageFullScreen == true;
        pageFullscreenExit.Visibility = pageFullscreen ? Visibility.Visible : Visibility.Collapsed;
        pageFullscreenExit.IsEnabled = pageFullscreen && !pageExitPending;
        fullscreenNotice.Text = pageFullscreen ? "Page requested full screen; host controls remain available." : "";
        fullscreenToggle.IsEnabled = !shuttingDown;
        add.IsEnabled = !shuttingDown && tabs.Count + retired.Count < CefNative.VeilCefViewLimit();
        // Read the selected live view only. A denial is informational, not
        // a deferred popup request or a permission to replay the target URL.
        uint blocked = live && !shuttingDown ? CefNative.VeilCefPopupsBlocked(active!.View.NativeHandle) : 0;
        popupNotice.Text = blocked == 0 ? "" : $"Popups blocked in this tab: {blocked}";
        popupNotice.Visibility = blocked == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Tick()
    {
        CefNative.VeilCefPump();
        foreach (var tab in tabs.ToArray())
        {
            if (tab.Closing && CefNative.VeilCefReadyToClose(tab.ClosingId) != 0)
            {
                tab.View.Dispose();
                pages.Children.Remove(tab.View.Element);
                strip.Children.Remove(tab.Header);
                tabs.Remove(tab);
                if (tab.ClosingId != 0) retired.Add(tab.ClosingId);
                if (ReferenceEquals(active, tab))
                {
                    active = null;
                    var next = tabs.FirstOrDefault(candidate => !candidate.Closing);
                    if (next is not null && !shuttingDown) Select(next);
                    else
                    {
                        address.Clear();
                        status.Text = "";
                    }
                }
                continue;
            }
            if (!tab.Closing && tab.View.DocumentTitle is { Length: > 0 } title)
                tab.Header.Content = title;
        }
        retired.RemoveAll(id => CefNative.VeilCefClosed(id) != 0);
        if (shuttingDown && tabs.Count == 0 && retired.Count == 0 && CefNative.VeilCefLiveViewCount() == 0)
        {
            allowWindowClose = true;
            if (!windowClosed) window.Close();
            Log("Tabbed host registry drained");
            pump.Stop();
            app.Shutdown();
            return;
        }
        if (active is { Closing: false } current)
        {
            var source = current.View.Source;
            if (source != current.LastSource && !address.IsKeyboardFocusWithin)
            {
                address.Text = source;
                current.LastSource = source;
            }
        }
        RefreshToolbar();
        if (fixture is not null && !shuttingDown) RunFixture();
    }

    private void RunFixture()
    {
        if (elapsed.Elapsed > TimeSpan.FromSeconds(freshProfileProbe ? 105 : 75))
        {
            Log($"Tabbed fixture timeout at phase {phase}");
            BeginShutdown();
            return;
        }
        string Url(string path)
        {
            var url = new Uri(new Uri(fixture!), path);
            return path is "/tabs-second" or "/tabs-preserved" or "/tabs-cleared-second" or "/tabs-storage-kept" or "/tabs-http-cache-first" or "/tabs-http-cache-reused" or "/tabs-http-cache-cleared" or "/tabs-batch-kept" or "/tabs-batch-second-deleted"
                ? new UriBuilder(url) { Host = "localhost" }.Uri.AbsoluteUri : url.AbsoluteUri;
        }
        void Fail() { result = 42; Log($"Tabbed fixture failed at phase {phase}"); BeginShutdown(); }

        if (phase == 0 && first is { } a && a.View.NativeHandle != 0)
        {
            firstId = a.View.NativeHandle;
            emptyDomains = ((IEngineSiteDataReader)a.View).GetCookieDomainsAsync();
            phase = 10;
        }
        else if (phase == 10 && emptyDomains is { IsCompleted: true })
        {
            if (!emptyDomains.IsCompletedSuccessfully || emptyDomains.Result.Count != 0) { Fail(); return; }
            Log("Fresh fixture cookie scan returned an empty list");
            if (freshProfileProbe)
            {
                address.Text = Url("/tabs-fresh-probe");
                NavigateAddress();
                phase = 50;
                return;
            }
            address.Text = Url("/tabs-first");
            NavigateAddress();
            second = AddTab();
            if (second is null) { Fail(); return; }
            phase = 1;
        }
        else if (phase == 1 && second is { } b && b.View.NativeHandle != 0)
        {
            secondId = b.View.NativeHandle;
            if (secondId == firstId || CefNative.VeilCefLiveViewCount() != 2) { Fail(); return; }
            address.Text = Url("/tabs-second");
            NavigateAddress();
            phase = 2;
        }
        else if (phase == 2 && first?.View.DocumentTitle == "TAB_FIRST" && second?.View.DocumentTitle == "TAB_SECOND" &&
                 first.PendingNavigationId == 0 && second.PendingNavigationId == 0 &&
                 first.Status == "Page loaded." && second.Status == "Page loaded." &&
                 CefNative.VeilCefPopupsBlocked(firstId) > 0)
        {
            Select(first);
            if (CefNative.VeilCefPopupsBlocked(firstId) != 1 ||
                CefNative.VeilCefPopupsBlocked(secondId) != 0 ||
                popupNotice.Text != "Popups blocked in this tab: 1" ||
                popupNotice.Visibility != Visibility.Visible ||
                address.Text != Url("/tabs-first") || first.View.NativeHandle != firstId ||
                second.View.NativeHandle != secondId || first.View.Element.Visibility != Visibility.Visible ||
                second.View.Element.Visibility != Visibility.Hidden) { Fail(); return; }
            Select(second);
            if (address.Text != Url("/tabs-second") || first.View.NativeHandle != firstId ||
                second.View.NativeHandle != secondId || popupNotice.Text != "" ||
                popupNotice.Visibility != Visibility.Collapsed ||
                CefNative.VeilCefLiveViewCount() != 2) { Fail(); return; }
            Log("Tab selection preserved both native identities and active address");
            Log("Blocked popup count visible only for the owning tab; no extra native view");
            // Exercise the same status publication path used by real callbacks.
            // This is a synthetic background status update, not a renderer crash.
            SetTabStatus(first, "Synthetic background status.");
            if (status.Text != "Page loaded." || active != second) { Fail(); return; }
            Select(first);
            if (status.Text != "Synthetic background status.") { Fail(); return; }
            Select(second);
            if (status.Text != "Page loaded.") { Fail(); return; }
            SetTabStatus(first, "Page loaded.");
            Log("Per-tab completion state and synthetic background status remained isolated");
            Select(first);
            ShowFindBar(true, focus: false);
            findQuery.Text = "VEIL-TAB-FIND-ONLY-FIRST";
            fixtureFind = SearchPageAsync(false);
            phase = 90;
        }
        else if (phase == 90 && fixtureFind is { IsCompleted: true })
        {
            if (!fixtureFind.IsCompletedSuccessfully || findResult.Text != "Match found." ||
                currentFind is not { } oldRequest || first is null || second is null) { Fail(); return; }
            Select(second);
            // Synthetic stale UI publication using the real previous request.
            PublishFindResult(oldRequest, true);
            if (findResult.Text != "") { Fail(); return; }
            fixtureFind = SearchPageAsync(false);
            phase = 91;
        }
        else if (phase == 91 && fixtureFind is { IsCompleted: true })
        {
            if (!fixtureFind.IsCompletedSuccessfully || findResult.Text != "No match." ||
                currentFind is not { } oldRequest || first is null || second is null) { Fail(); return; }
            findQuery.Text = "different";
            PublishFindResult(oldRequest, true);
            if (findResult.Text != "") { Fail(); return; }
            ShowFindBar(false);
            PublishFindResult(oldRequest, true);
            if (findResult.Text != "" || findBar.Visibility != Visibility.Collapsed) { Fail(); return; }
            Log("Find UI showed per-tab match results and discarded stale publication on switch, edit and close");
            Select(first);
            if (zoomLabel.Text != "100%" || Math.Abs(first.View.ZoomFactor - 1) > 1e-6 ||
                Math.Abs(second.View.ZoomFactor - 1) > 1e-6) { Fail(); return; }
            ChangeZoom(0.25);
            phase = 92;
        }
        else if (phase == 92 && first is not null && second is not null &&
                 Math.Abs(first.View.ZoomFactor - 1.25) < 1e-6)
        {
            if (zoomLabel.Text != "125%") { Fail(); return; }
            Select(second);
            if (zoomLabel.Text != "100%" || Math.Abs(second.View.ZoomFactor - 1) > 1e-6)
            { Fail(); return; }
            Select(first);
            if (zoomLabel.Text != "125%") { Fail(); return; }
            ChangeZoom(0, reset: true);
            phase = 93;
        }
        else if (phase == 93 && first is not null && second is not null &&
                 Math.Abs(first.View.ZoomFactor - 1) < 1e-6)
        {
            if (zoomLabel.Text != "100%") { Fail(); return; }
            Select(second);
            Log("Zoom readback followed selected distinct-origin view and reset to 100 percent");
            var style = window.WindowStyle;
            var state = window.WindowState;
            var resize = window.ResizeMode;
            SetBrowserFullscreen(true);
            if (!browserFullscreen || window.WindowStyle != WindowStyle.None ||
                window.WindowState != WindowState.Maximized || strip.Visibility != Visibility.Collapsed ||
                toolbar.Visibility != Visibility.Collapsed || fullscreenToggle.Visibility != Visibility.Visible ||
                second.View.IsPageFullScreen != false) { Fail(); return; }
            SetBrowserFullscreen(false);
            if (browserFullscreen || window.WindowStyle != style || window.WindowState != state ||
                window.ResizeMode != resize || toolbar.Visibility != Visibility.Visible ||
                strip.Visibility != Visibility.Visible || active != second) { Fail(); return; }
            Log("Browser full screen restored host state without changing page full screen");
            firstDomains = ((IEngineSiteDataReader)first.View).GetCookieDomainsAsync();
            secondDomains = ((IEngineSiteDataReader)second.View).GetCookieDomainsAsync();
            phase = 11;
        }
        else if (phase == 11 && firstDomains is { IsCompleted: true } && secondDomains is { IsCompleted: true })
        {
            if (!firstDomains.IsCompletedSuccessfully || !secondDomains.IsCompletedSuccessfully ||
                firstDomains.Result.Count != 2 || secondDomains.Result.Count != 2 ||
                firstDomains.Result.Count(domain => domain == "127.0.0.1") != 1 ||
                firstDomains.Result.Count(domain => domain == "localhost") != 1 ||
                secondDomains.Result.Count(domain => domain == "127.0.0.1") != 1 ||
                secondDomains.Result.Count(domain => domain == "localhost") != 1)
            { Fail(); return; }
            Log("Both views returned only the two synthetic cookie domains");
            // Initial authorization succeeds; the visitor must check it again.
            deletionAuthorizationChecks = 0;
            cookieDeletion = ((IEngineSiteDataWriter)second!.View).DeleteCookieDomainAsync("127.0.0.1",
                () => ++deletionAuthorizationChecks == 1);
            phase = 12;
        }
        else if (phase == 12 && cookieDeletion is { IsCompleted: true })
        {
            if (!cookieDeletion.IsCompletedSuccessfully || cookieDeletion.Result is not null ||
                deletionAuthorizationChecks < 2) { Fail(); return; }
            firstDomains = ((IEngineSiteDataReader)first!.View).GetCookieDomainsAsync();
            phase = 13;
        }
        else if (phase == 13 && firstDomains is { IsCompleted: true })
        {
            if (!firstDomains.IsCompletedSuccessfully || firstDomains.Result.Count != 2 ||
                firstDomains.Result.Count(domain => domain == "127.0.0.1") != 1 ||
                firstDomains.Result.Count(domain => domain == "localhost") != 1) { Fail(); return; }
            Log("Revoked cookie deletion left both domains untouched");
            cookieDeletion = ((IEngineSiteDataWriter)second!.View).DeleteCookieDomainAsync("127.0.0.1",
                () => active == second && !second.Closing && !shuttingDown);
            phase = 14;
        }
        else if (phase == 14 && cookieDeletion is { IsCompleted: true })
        {
            if (!cookieDeletion.IsCompletedSuccessfully || cookieDeletion.Result != 1) { Fail(); return; }
            firstDomains = ((IEngineSiteDataReader)first!.View).GetCookieDomainsAsync();
            phase = 15;
        }
        else if (phase == 15 && firstDomains is { IsCompleted: true })
        {
            if (!firstDomains.IsCompletedSuccessfully) { Fail(); return; }
            if (firstDomains.Result.Count == 2 && firstDomains.Result.Contains("127.0.0.1") &&
                firstDomains.Result.Contains("localhost"))
            {
                // CEF issues deletion asynchronously; verify the observed state.
                firstDomains = ((IEngineSiteDataReader)first!.View).GetCookieDomainsAsync();
                return;
            }
            if (firstDomains.Result.Count != 1 || firstDomains.Result[0] != "localhost") { Fail(); return; }
            Log("Exact domain deletion preserved the other domain");
            if (CefNative.VeilCefStartCookieDomainScan(secondId) == 0) { Fail(); return; }
            RequestClose(second!);
            phase = 3;
        }
        else if (phase == 3 && CefNative.VeilCefClosed(secondId) != 0 && tabs.Count == 1)
        {
            if (active != first || first!.View.NativeHandle != firstId || address.Text != Url("/tabs-first") ||
                CefNative.VeilCefLiveViewCount() != 1) { Fail(); return; }
            if (CefNative.VeilCefCookieDomainScanStatus(secondId) != -1 ||
                CefNative.VeilCefStartCookieDomainScan(secondId) != 0 ||
                CefNative.VeilCefCookieDomainAt(secondId, 0) != 0) { Fail(); return; }
            Log("Closed view cookie scan identity rejected");
            CefNative.VeilCefNavigate(secondId, Url("/tabs-stale"));
            address.Text = Url("/tabs-after-close");
            NavigateAddress();
            phase = 4;
        }
        else if (phase == 4 && first?.View.DocumentTitle == "TAB_AFTER_CLOSE")
        {
            if (first.View.NativeHandle != firstId || first.View.Source != Url("/tabs-after-close") ||
                !first.View.CanGoBack) { Fail(); return; }
            Log("Closing active tab preserved surviving view and navigation");
            address.Text = Url("/tabs-preserved");
            NavigateAddress();
            phase = 5;
        }
        else if (phase == 5 && first?.View.DocumentTitle == "TAB_PRESERVED")
        {
            if (first.View.NativeHandle != firstId || first.View.Source != Url("/tabs-preserved")) { Fail(); return; }
            Log("Preserved domain HTTP check completed");
            address.Text = Url("/tabs-reseed");
            NavigateAddress();
            phase = 6;
        }
        else if (phase == 6 && first?.View.DocumentTitle == "TAB_RESEEDED")
        {
            firstDomains = ((IEngineSiteDataReader)first.View).GetCookieDomainsAsync();
            phase = 16;
        }
        else if (phase == 16 && firstDomains is { IsCompleted: true })
        {
            if (!firstDomains.IsCompletedSuccessfully || firstDomains.Result.Count != 2 ||
                firstDomains.Result.Count(domain => domain == "127.0.0.1") != 1 ||
                firstDomains.Result.Count(domain => domain == "localhost") != 1) { Fail(); return; }
            profileCookieDeletion = ((IEngineSiteDataWriter)first!.View).ClearProfileDataAsync(
                EngineProfileDataKinds.Cookies | EngineProfileDataKinds.DomStorage, () => true);
            phase = 17;
        }
        else if (phase == 17 && profileCookieDeletion is { IsCompleted: true })
        {
            if (profileCookieDeletion.Exception?.GetBaseException() is not NotSupportedException) { Fail(); return; }
            profileCookieDeletion = ((IEngineSiteDataWriter)first!.View).ClearProfileDataAsync(
                EngineProfileDataKinds.Cookies, () => false);
            phase = 18;
        }
        else if (phase == 18 && profileCookieDeletion is { IsCompleted: true })
        {
            if (!profileCookieDeletion.IsCompletedSuccessfully || profileCookieDeletion.Result) { Fail(); return; }
            firstDomains = ((IEngineSiteDataReader)first!.View).GetCookieDomainsAsync();
            phase = 19;
        }
        else if (phase == 19 && firstDomains is { IsCompleted: true })
        {
            if (!firstDomains.IsCompletedSuccessfully || firstDomains.Result.Count != 2 ||
                firstDomains.Result.Count(domain => domain == "127.0.0.1") != 1 ||
                firstDomains.Result.Count(domain => domain == "localhost") != 1) { Fail(); return; }
            Log("Unsupported mixed scope and denied profile clear preserved both cookies");
            profileCookieDeletion = ((IEngineSiteDataWriter)first!.View).ClearProfileDataAsync(
                EngineProfileDataKinds.Cookies, () => active == first && !first.Closing && !shuttingDown);
            phase = 20;
        }
        else if (phase == 20 && profileCookieDeletion is { IsCompleted: true })
        {
            if (!profileCookieDeletion.IsCompletedSuccessfully || !profileCookieDeletion.Result) { Fail(); return; }
            firstDomains = ((IEngineSiteDataReader)first!.View).GetCookieDomainsAsync();
            phase = 21;
        }
        else if (phase == 21 && firstDomains is { IsCompleted: true })
        {
            if (!firstDomains.IsCompletedSuccessfully) { Fail(); return; }
            if (firstDomains.Result.Count != 0)
            {
                firstDomains = ((IEngineSiteDataReader)first!.View).GetCookieDomainsAsync();
                return;
            }
            Log("Explicit profile cookie clear returned an empty cookie scan");
            address.Text = Url("/tabs-cleared-first");
            NavigateAddress();
            phase = 7;
        }
        else if (phase == 7 && first?.View.DocumentTitle == "TAB_CLEARED_FIRST")
        {
            if (first.View.Source != Url("/tabs-cleared-first")) { Fail(); return; }
            address.Text = Url("/tabs-cleared-second");
            NavigateAddress();
            phase = 8;
        }
        else if (phase == 8 && first?.View.DocumentTitle == "TAB_CLEARED_SECOND")
        {
            if (first.View.Source != Url("/tabs-cleared-second") || first.View.NativeHandle != firstId) { Fail(); return; }
            Log("Profile cookie clear preserved both origin localStorage markers");
            var reader = (IEngineSiteDataReader)first.View;
            rejectedUsage = reader.GetOriginStorageUsageAsync("http://example.invalid");
            firstUsage = reader.GetOriginStorageUsageAsync(new Uri(Url("/tabs-first")).GetLeftPart(UriPartial.Authority));
            phase = 22;
        }
        else if (phase == 22 && firstUsage is { IsCompleted: true } && rejectedUsage is { IsCompleted: true })
        {
            if (rejectedUsage.Exception?.GetBaseException() is not InvalidOperationException ||
                !firstUsage.IsCompletedSuccessfully || firstUsage.Result is not double bytes ||
                !double.IsFinite(bytes) || bytes < 65536) { Fail(); return; }
            Log("Storage usage rejected an unapproved origin and observed first fixture cache");
            secondUsage = ((IEngineSiteDataReader)first!.View).GetOriginStorageUsageAsync(
                new Uri(Url("/tabs-second")).GetLeftPart(UriPartial.Authority));
            phase = 23;
        }
        else if (phase == 23 && secondUsage is { IsCompleted: true })
        {
            if (!secondUsage.IsCompletedSuccessfully || secondUsage.Result is not double bytes ||
                !double.IsFinite(bytes) || bytes < 524288 || bytes <= firstUsage!.Result!.Value) { Fail(); return; }
            Log("Distinct fixture origins returned distinct finite storage usage");
            address.Text = Url("/tabs-storage-reseed");
            NavigateAddress();
            phase = 24;
        }
        else if (phase == 24 && first?.View.DocumentTitle == "TAB_STORAGE_RESEEDED")
        {
            storageClear = ((IEngineSiteDataWriter)first.View).ClearOriginStorageAsync(
                "http://example.invalid", () => true);
            phase = 25;
        }
        else if (phase == 25 && storageClear is { IsCompleted: true })
        {
            if (storageClear.Exception?.GetBaseException() is not InvalidOperationException) { Fail(); return; }
            deletionAuthorizationChecks = 0;
            storageClear = ((IEngineSiteDataWriter)first!.View).ClearOriginStorageAsync(
                new Uri(Url("/tabs-first")).GetLeftPart(UriPartial.Authority),
                () => ++deletionAuthorizationChecks == 1);
            phase = 26;
        }
        else if (phase == 26 && storageClear is { IsCompleted: true })
        {
            if (!storageClear.IsCompletedSuccessfully || storageClear.Result ||
                deletionAuthorizationChecks < 2) { Fail(); return; }
            address.Text = Url("/tabs-storage-denied");
            NavigateAddress();
            phase = 27;
        }
        else if (phase == 27 && first?.View.DocumentTitle == "TAB_STORAGE_DENIED")
        {
            Log("Unapproved origin and revoked storage clear left fixture storage untouched");
            storageClear = ((IEngineSiteDataWriter)first.View).ClearOriginStorageAsync(
                new Uri(Url("/tabs-first")).GetLeftPart(UriPartial.Authority),
                () => active == first && !first.Closing && !shuttingDown);
            phase = 28;
        }
        else if (phase == 28 && storageClear is { IsCompleted: true })
        {
            if (!storageClear.IsCompletedSuccessfully || !storageClear.Result) { Fail(); return; }
            address.Text = Url("/tabs-storage-deleted");
            NavigateAddress();
            phase = 29;
        }
        else if (phase == 29 && first?.View.DocumentTitle == "TAB_STORAGE_DELETED")
        {
            address.Text = Url("/tabs-storage-kept");
            NavigateAddress();
            phase = 30;
        }
        else if (phase == 30 && first?.View.DocumentTitle == "TAB_STORAGE_KEPT")
        {
            if (first.View.NativeHandle != firstId || first.View.Source != Url("/tabs-storage-kept")) { Fail(); return; }
            Log("Exact origin storage clear preserved the other origin and cookie scope");
            address.Text = Url("/tabs-http-cache-first");
            NavigateAddress();
            phase = 31;
        }
        else if (phase == 31 && first?.View.DocumentTitle == "TAB_HTTP_CACHE_FIRST")
        {
            httpCacheClear = ((IEngineSiteDataWriter)first.View).ClearProfileDataAsync(
                EngineProfileDataKinds.Cookies | EngineProfileDataKinds.DiskCache, () => true);
            phase = 32;
        }
        else if (phase == 32 && httpCacheClear is { IsCompleted: true })
        {
            if (httpCacheClear.Exception?.GetBaseException() is not NotSupportedException) { Fail(); return; }
            deletionAuthorizationChecks = 0;
            httpCacheClear = ((IEngineSiteDataWriter)first!.View).ClearProfileDataAsync(
                EngineProfileDataKinds.DiskCache, () => ++deletionAuthorizationChecks == 1);
            phase = 33;
        }
        else if (phase == 33 && httpCacheClear is { IsCompleted: true })
        {
            if (!httpCacheClear.IsCompletedSuccessfully || httpCacheClear.Result || deletionAuthorizationChecks < 2)
            {
                Log($"HTTP cache refusal probe failed after {deletionAuthorizationChecks} authorization checks: " +
                    (httpCacheClear.Exception?.GetBaseException().Message ?? httpCacheClear.Status.ToString()));
                Fail();
                return;
            }
            address.Text = Url("/tabs-http-cache-reused");
            NavigateAddress();
            phase = 34;
        }
        else if (phase == 34 && first?.View.DocumentTitle == "TAB_HTTP_CACHE_REUSED")
        {
            Log("Denied and mixed cache scopes preserved cached HTTP response");
            httpCacheClear = ((IEngineSiteDataWriter)first.View).ClearProfileDataAsync(
                EngineProfileDataKinds.DiskCache, () => active == first && !first.Closing && !shuttingDown);
            phase = 35;
        }
        else if (phase == 35 && httpCacheClear is { IsCompleted: true })
        {
            if (!httpCacheClear.IsCompletedSuccessfully || !httpCacheClear.Result) { Fail(); return; }
            address.Text = Url("/tabs-http-cache-cleared");
            NavigateAddress();
            phase = 36;
        }
        else if (phase == 36 && first?.View.DocumentTitle == "TAB_HTTP_CACHE_CLEARED")
        {
            if (first.View.NativeHandle != firstId || first.View.Source != Url("/tabs-http-cache-cleared")) { Fail(); return; }
            Log("HTTP cache clear refetched resource and preserved origin storage");
            address.Text = Url("/tabs-http-cache-cookie");
            NavigateAddress();
            phase = 37;
        }
        else if (phase == 37 && first?.View.DocumentTitle == "TAB_HTTP_CACHE_COOKIE")
        {
            Log("HTTP cache clear cookie preservation probe completed");
            address.Text = Url("/tabs-batch-seed-first");
            NavigateAddress();
            phase = 38;
        }
        else if (phase == 38 && first?.View.DocumentTitle == "TAB_BATCH_SEED_FIRST")
        {
            storageClear = ((IEngineSiteDataWriter)first.View).ClearProfileDataAsync(
                EngineProfileDataKinds.DomStorage, () => true);
            phase = 39;
        }
        else if (phase == 39 && storageClear is { IsCompleted: true })
        {
            if (storageClear.Exception?.GetBaseException() is not NotSupportedException) { Fail(); return; }
            knownOriginClear = first!.View.ClearKnownFixtureOriginsAsync(() => false);
            phase = 40;
        }
        else if (phase == 40 && knownOriginClear is { IsCompleted: true })
        {
            if (!knownOriginClear.IsCompletedSuccessfully || knownOriginClear.Result != 0) { Fail(); return; }
            address.Text = Url("/tabs-batch-denied");
            NavigateAddress();
            phase = 41;
        }
        else if (phase == 41 && first?.View.DocumentTitle == "TAB_BATCH_DENIED")
        {
            deletionAuthorizationChecks = 0;
            knownOriginClear = first.View.ClearKnownFixtureOriginsAsync(() => ++deletionAuthorizationChecks <= 2);
            phase = 42;
        }
        else if (phase == 42 && knownOriginClear is { IsCompleted: true })
        {
            if (!knownOriginClear.IsCompletedSuccessfully || knownOriginClear.Result != 1 ||
                deletionAuthorizationChecks < 3) { Fail(); return; }
            address.Text = Url("/tabs-batch-first-deleted");
            NavigateAddress();
            phase = 43;
        }
        else if (phase == 43 && first?.View.DocumentTitle == "TAB_BATCH_FIRST_DELETED")
        {
            address.Text = Url("/tabs-batch-kept");
            NavigateAddress();
            phase = 44;
        }
        else if (phase == 44 && first?.View.DocumentTitle == "TAB_BATCH_KEPT")
        {
            Log("Known-origin batch revocation acknowledged one clear and preserved second origin");
            address.Text = Url("/tabs-batch-reseed-first");
            NavigateAddress();
            phase = 45;
        }
        else if (phase == 45 && first?.View.DocumentTitle == "TAB_BATCH_RESEED_FIRST")
        {
            knownOriginClear = first.View.ClearKnownFixtureOriginsAsync(
                () => active == first && !first.Closing && !shuttingDown);
            phase = 46;
        }
        else if (phase == 46 && knownOriginClear is { IsCompleted: true })
        {
            if (!knownOriginClear.IsCompletedSuccessfully || knownOriginClear.Result != 2) { Fail(); return; }
            address.Text = Url("/tabs-batch-final-first");
            NavigateAddress();
            phase = 47;
        }
        else if (phase == 47 && first?.View.DocumentTitle == "TAB_BATCH_FINAL_FIRST")
        {
            address.Text = Url("/tabs-batch-second-deleted");
            NavigateAddress();
            phase = 48;
        }
        else if (phase == 48 && first?.View.DocumentTitle == "TAB_BATCH_SECOND_DELETED")
        {
            if (first.View.NativeHandle != firstId || first.View.Source != Url("/tabs-batch-second-deleted")) { Fail(); return; }
            Log("Known-origin batch cleared both fixture origins without claiming full profile clear");
            address.Text = Url("/tabs-isolation-seed");
            NavigateAddress();
            phase = 49;
        }
        else if (phase == 49 && first?.View.DocumentTitle == "TAB_ISOLATION_SEEDED")
        {
            Log("Seeded persistent synthetic storage for next-process isolation probe");
            firstDomains = ((IEngineSiteDataReader)first.View).GetCookieDomainsAsync();
            phase = 51;
        }
        else if (phase == 51 && firstDomains is { IsCompleted: true })
        {
            if (!firstDomains.IsCompletedSuccessfully || firstDomains.Result.Count != 2 ||
                firstDomains.Result.Any(domain => domain != "127.0.0.1")) { Fail(); return; }
            Log("Persistent synthetic cookie present before shutdown");
            result = 0;
            BeginShutdown();
        }
        else if (phase == 50 && first?.View.DocumentTitle == "TAB_FRESH_PROFILE")
        {
            if (!freshProfileProbe || first.View.NativeHandle != firstId ||
                first.View.Source != Url("/tabs-fresh-probe")) { Fail(); return; }
            Log("Fresh process saw no earlier LocalStorage or CacheStorage");
            if ((IBrowserEngineView)first.View is IEngineLoginForms) { Fail(); return; }
            address.Text = Url(LoginProbePaths[0]);
            NavigateAddress();
            phase = 52;
        }
        else if (phase == 52 && first?.View.DocumentTitle == "TAB_LOGIN_" + loginProbeIndex &&
                 !first.View.IsFixtureLoading)
        {
            var page = Url(LoginProbePaths[loginProbeIndex]);
            if (first.View.Source != page) { Fail(); return; }
            var origin = new Uri(page).GetLeftPart(UriPartial.Authority);
            loginProbe = first.View.ProbeFixtureLoginFormAsync(origin, page,
                () => active == first && !first.Closing && !shuttingDown);
            phase = 53;
        }
        else if (phase == 53 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is null ||
                loginProbe.Result.Value != (loginProbeIndex == 0)) { Fail(); return; }
            if (loginProbeIndex == 0)
            {
                var page = Url(LoginProbePaths[0]);
                var origin = new Uri(page).GetLeftPart(UriPartial.Authority);
                deletionAuthorizationChecks = 0;
                loginProbe = first!.View.ProbeFixtureLoginFormAsync(origin, page, () => ++deletionAuthorizationChecks == 1);
                phase = 54;
                return;
            }
            if (++loginProbeIndex == LoginProbePaths.Length)
            {
                Log("Boolean-only login probe accepted one form and rejected hidden, ambiguous, cross-origin, iframe and new-password forms");
                // Existing detection pages are outside the separate values allowlist.
                var page = Url(LoginProbePaths[^1]);
                loginProbe = first!.View.CheckFixtureLoginValuesAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page, () => true);
                phase = 55;
            }
            else
            {
                address.Text = Url(LoginProbePaths[loginProbeIndex]);
                NavigateAddress();
                phase = 52;
            }
        }
        else if (phase == 54 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is not null || deletionAuthorizationChecks < 2) { Fail(); return; }
            Log("Login probe discarded result after authorization expired");
            loginProbeIndex = 1;
            address.Text = Url(LoginProbePaths[loginProbeIndex]);
            NavigateAddress();
            phase = 52;
        }
        else if (phase == 55 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is not null) { Fail(); return; }
            address.Text = Url(LoginValuesPaths[0]);
            NavigateAddress();
            phase = 56;
        }
        else if (phase == 56 && first?.View.DocumentTitle == "TAB_LOGIN_VALUES_" + loginValuesIndex &&
                 !first.View.IsFixtureLoading)
        {
            var page = Url(LoginValuesPaths[loginValuesIndex]);
            if (first.View.Source != page) { Fail(); return; }
            var origin = new Uri(page).GetLeftPart(UriPartial.Authority);
            if (loginValuesIndex == 0)
            {
                deletionAuthorizationChecks = 0;
                loginProbe = first.View.CheckFixtureLoginValuesAsync(origin, page, () => ++deletionAuthorizationChecks == 1);
                phase = 57;
            }
            else
            {
                loginProbe = first.View.CheckFixtureLoginValuesAsync(origin, page,
                    () => active == first && !first.Closing && !shuttingDown);
                phase = 58;
            }
        }
        else if (phase == 57 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is not null ||
                deletionAuthorizationChecks != 2) { Fail(); return; }
            Log("Synthetic values check rejected authorization at native dispatch");
            var page = Url(LoginValuesPaths[0]);
            loginProbe = first!.View.CheckFixtureLoginValuesAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page,
                () => active == first && !first.Closing && !shuttingDown);
            phase = 58;
        }
        else if (phase == 58 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is null ||
                loginProbe.Result.Value != (loginValuesIndex == 0)) { Fail(); return; }
            if (++loginValuesIndex == LoginValuesPaths.Length)
            {
                Log("Synthetic login values equality accepted matching markers and rejected empty and mismatched markers");
                var page = Url(LoginValuesPaths[^1]);
                loginProbe = first!.View.FillFixtureLoginAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page, () => true);
                phase = 59;
            }
            else
            {
                address.Text = Url(LoginValuesPaths[loginValuesIndex]);
                NavigateAddress();
                phase = 56;
            }
        }
        else if (phase == 59 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is not null) { Fail(); return; }
            address.Text = Url(LoginFillPaths[0]);
            NavigateAddress();
            phase = 60;
        }
        else if (phase == 60 && first?.View.DocumentTitle == "TAB_LOGIN_FILL_" + loginFillIndex &&
                 !first.View.IsFixtureLoading)
        {
            var page = Url(LoginFillPaths[loginFillIndex]);
            if (first.View.Source != page || first.View.NativeHandle != firstId) { Fail(); return; }
            var origin = new Uri(page).GetLeftPart(UriPartial.Authority);
            deletionAuthorizationChecks = 0;
            loginProbe = first.View.FillFixtureLoginAsync(origin, page,
                () => active == first && !first.Closing && !shuttingDown &&
                    (loginFillIndex != 4 || ++deletionAuthorizationChecks == 1));
            phase = 61;
        }
        else if (phase == 61 && loginProbe is { IsCompleted: true })
        {
            bool? expected = loginFillIndex == 4 ? null : loginFillIndex < 2;
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result != expected ||
                (loginFillIndex == 4 && deletionAuthorizationChecks != 2)) { Fail(); return; }
            var page = Url(LoginFillPaths[loginFillIndex]);
            loginProbe = first!.View.CheckFixtureLoginFillStateAsync(new Uri(page).GetLeftPart(UriPartial.Authority),
                page, () => active == first && !first.Closing && !shuttingDown);
            phase = 62;
        }
        else if (phase == 62 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result != true) { Fail(); return; }
            if (++loginFillIndex == LoginFillPaths.Length)
            {
                Log("Synthetic login fill verified exact markers, input/change events, existing values and revoked dispatch without submission");
                address.Text = Url("/tabs-login-fill-duplicate");
                NavigateAddress();
                phase = 63;
            }
            else
            {
                address.Text = Url(LoginFillPaths[loginFillIndex]);
                NavigateAddress();
                phase = 60;
            }
        }
        else if (phase == 63 && first?.View.DocumentTitle == "TAB_LOGIN_LIFETIME_DUPLICATE" && !first.View.IsFixtureLoading)
        {
            var page = Url("/tabs-login-fill-duplicate");
            var origin = new Uri(page).GetLeftPart(UriPartial.Authority);
            if (first.View.Source != page) { Fail(); return; }
            lifetimeAuthorizationChecks = 0;
            loginProbe = first.View.FillFixtureLoginAsync(origin, page, () =>
            {
                // Reenter during native dispatch authorization, while the
                // original managed/native operation owns its slot.
                if (++lifetimeAuthorizationChecks == 2)
                    duplicateLoginProbe = first.View.FillFixtureLoginAsync(origin, page, () => true);
                return active == first && !first.Closing && !shuttingDown;
            });
            phase = 64;
        }
        else if (phase == 64 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result != true ||
                duplicateLoginProbe is not { IsCompletedSuccessfully: true } || duplicateLoginProbe.Result is not null)
            { Fail(); return; }
            var page = Url("/tabs-login-fill-duplicate");
            loginProbe = first!.View.CheckFixtureLoginFillStateAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page, () => true);
            phase = 65;
        }
        else if (phase == 65 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result != true) { Fail(); return; }
            Log("Duplicate login operation rejected without releasing original slot");
            loginSpare = AddTab();
            if (loginSpare is null) { Fail(); return; }
            Select(first!);
            address.Text = Url("/tabs-login-fill-switch");
            NavigateAddress();
            phase = 66;
        }
        else if (phase == 66 && first?.View.DocumentTitle == "TAB_LOGIN_LIFETIME_SWITCH" &&
                 loginSpare?.View.NativeHandle > 0 && !first.View.IsFixtureLoading)
        {
            var page = Url("/tabs-login-fill-switch");
            if (first.View.Source != page) { Fail(); return; }
            lifetimeAuthorizationChecks = 0;
            loginProbe = first.View.FillFixtureLoginAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page, () =>
            {
                if (++lifetimeAuthorizationChecks == 2)
                {
                    Select(loginSpare!);
                    Select(first);
                }
                return active == first && !first.Closing && !shuttingDown;
            });
            phase = 67;
        }
        else if (phase == 67 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is not null || lifetimeAuthorizationChecks != 2)
            { Fail(); return; }
            var page = Url("/tabs-login-fill-switch");
            loginProbe = first!.View.CheckFixtureLoginFillStateAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page, () => true);
            phase = 68;
        }
        else if (phase == 68 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result != true) { Fail(); return; }
            Log("A-B-A tab switch invalidated login authorization and preserved empty form");
            RequestClose(loginSpare!);
            address.Text = Url("/tabs-login-fill-navigation");
            NavigateAddress();
            phase = 69;
        }
        else if (phase == 69 && first?.View.DocumentTitle == "TAB_LOGIN_LIFETIME_NAVIGATION" && !first.View.IsFixtureLoading)
        {
            var page = Url("/tabs-login-fill-navigation");
            if (first.View.Source != page) { Fail(); return; }
            lifetimeAuthorizationChecks = 0;
            loginProbe = first.View.FillFixtureLoginAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page, () =>
            {
                if (++lifetimeAuthorizationChecks == 2)
                    first.View.Navigate(Url("/tabs-login-fill-destination"));
                return active == first && !first.Closing && !shuttingDown;
            });
            phase = 70;
        }
        else if (phase == 70 && loginProbe is { IsCompleted: true } &&
                 first?.View.DocumentTitle == "TAB_LOGIN_LIFETIME_DESTINATION" && !first.View.IsFixtureLoading)
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is not null || lifetimeAuthorizationChecks != 2)
            { Fail(); return; }
            var page = Url("/tabs-login-fill-destination");
            if (first.View.Source != page || first.View.NativeHandle != firstId) { Fail(); return; }
            loginProbe = first.View.CheckFixtureLoginFillStateAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page, () => true);
            phase = 71;
        }
        else if (phase == 71 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result != true) { Fail(); return; }
            Log("Navigation during native authorization rejected fill and preserved destination form");
            phase = 72;
        }
        else if (phase == 72 && CefNative.VeilCefLiveViewCount() == 1 && retired.Count == 0)
        {
            loginSpare = AddTab();
            if (loginSpare is null) { Fail(); return; }
            phase = 73;
        }
        else if (phase == 73 && loginSpare is { } closingTab && closingTab.View.NativeHandle != 0)
        {
            closingLoginId = closingTab.View.NativeHandle;
            if (closingLoginId == firstId) { Fail(); return; }
            address.Text = Url("/tabs-login-fill-closing");
            NavigateAddress();
            phase = 74;
        }
        else if (phase == 74 && loginSpare is { } closingTarget &&
                 closingTarget.View.DocumentTitle == "TAB_LOGIN_LIFETIME_CLOSING" && !closingTarget.View.IsFixtureLoading)
        {
            var page = Url("/tabs-login-fill-closing");
            if (closingTarget.View.Source != page || closingTarget.View.NativeHandle != closingLoginId) { Fail(); return; }
            lifetimeAuthorizationChecks = 0;
            loginProbe = closingTarget.View.FillFixtureLoginAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page, () =>
            {
                if (++lifetimeAuthorizationChecks == 2) RequestClose(closingTarget);
                // Intentionally return true: closing must invalidate the
                // captured generation even if the caller fails to revoke.
                return true;
            });
            phase = 75;
        }
        else if (phase == 75 && loginProbe is { IsCompleted: true } &&
                 CefNative.VeilCefClosed(closingLoginId) != 0 && retired.Count == 0)
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is not null || lifetimeAuthorizationChecks != 2 ||
                active != first || first!.View.NativeHandle != firstId || CefNative.VeilCefLiveViewCount() != 1)
            { Fail(); return; }
            var page = Url("/tabs-login-fill-closing");
            CefNative.CookieDeleteAuthorization allow = _ => 1;
            if (CefNative.VeilCefStartLoginFill(closingLoginId, new Uri(page).GetLeftPart(UriPartial.Authority), page, allow) != 0 ||
                CefNative.VeilCefLoginProbeStatus(closingLoginId) != -1) { Fail(); return; }
            Log("Closing login target invalidated authorization and retired native identity");
            loginSpare = AddTab();
            if (loginSpare is null) { Fail(); return; }
            phase = 76;
        }
        else if (phase == 76 && loginSpare is { } reopenedTab && reopenedTab.View.NativeHandle != 0)
        {
            if (reopenedTab.View.NativeHandle <= closingLoginId) { Fail(); return; }
            address.Text = Url("/tabs-login-fill-reopened");
            NavigateAddress();
            phase = 77;
        }
        else if (phase == 77 && loginSpare is { } newTarget &&
                 newTarget.View.DocumentTitle == "TAB_LOGIN_LIFETIME_REOPENED" && !newTarget.View.IsFixtureLoading)
        {
            var page = Url("/tabs-login-fill-reopened");
            if (newTarget.View.Source != page) { Fail(); return; }
            loginProbe = newTarget.View.CheckFixtureLoginFillStateAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page,
                () => active == newTarget && !newTarget.Closing && !shuttingDown);
            phase = 78;
        }
        else if (phase == 78 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result != true) { Fail(); return; }
            Log("Reopened login view retained new identity and empty form");
            RequestClose(loginSpare!);
            phase = 79;
        }
        else if (phase == 79 && CefNative.VeilCefLiveViewCount() == 1 && retired.Count == 0 && active == first)
        {
            address.Text = Url("/tabs-login-renderer-failure");
            NavigateAddress();
            phase = 80;
        }
        else if (phase == 80 && first?.View.DocumentTitle == "TAB_LOGIN_RENDERER_FAILURE" && !first.View.IsFixtureLoading)
        {
            var page = Url("/tabs-login-renderer-failure");
            if (first.View.Source != page || first.View.NativeHandle != firstId) { Fail(); return; }
            expectLoginRendererFailure = true;
            phase = 81;
            loginProbe = first.View.AwaitFixtureLoginRendererFailureAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page,
                () => active == first && !first.Closing && !shuttingDown);
        }
        else if (phase == 81 && loginRendererFailures == 1 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result is not null ||
                first!.View.NativeHandle != firstId || CefNative.VeilCefLiveViewCount() != 1)
            { Fail(); return; }
            var page = Url("/tabs-login-renderer-failure");
            var origin = new Uri(page).GetLeftPart(UriPartial.Authority);
            CefNative.CookieDeleteAuthorization allow = _ => 1;
            if (CefNative.VeilCefStartLoginRendererWait(firstId, origin, page, allow) != 0) { Fail(); return; }
            Log("Failed renderer refused another login observation before recovery");
            address.Text = Url("/tabs-login-fill-renderer-recovered");
            NavigateAddress();
            phase = 82;
        }
        else if (phase == 82 && first?.View.DocumentTitle == "TAB_LOGIN_RENDERER_RECOVERED" && !first.View.IsFixtureLoading)
        {
            var page = Url("/tabs-login-fill-renderer-recovered");
            if (first.View.Source != page || first.View.NativeHandle != firstId) { Fail(); return; }
            loginProbe = first.View.CheckFixtureLoginFillStateAsync(new Uri(page).GetLeftPart(UriPartial.Authority), page,
                () => active == first && !first.Closing && !shuttingDown);
            phase = 83;
        }
        else if (phase == 83 && loginProbe is { IsCompleted: true })
        {
            if (!loginProbe.IsCompletedSuccessfully || loginProbe.Result != true || loginRendererFailures != 1)
            { Fail(); return; }
            Log("Explicit recovery navigation restored login observation on the same view with empty form");
            result = 0;
            BeginShutdown();
        }
    }
}
