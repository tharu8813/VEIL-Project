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
    }

    private readonly Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    private readonly Window window = new() { Width = 1000, Height = 700, Title = "VEIL CEF development tabs" };
    private readonly DockPanel layout = new();
    private readonly StackPanel strip = new() { Orientation = Orientation.Horizontal };
    private readonly Grid pages = new();
    private readonly TextBox address = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Margin = new Thickness(6) };
    private readonly Button add = new() { Content = "+" };
    private readonly Button close = new() { Content = "Close tab" };
    private readonly Button back = new() { Content = "Back" };
    private readonly Button forward = new() { Content = "Forward" };
    private readonly Button reload = new() { Content = "Reload" };
    private readonly Button go = new() { Content = "Go" };
    private readonly List<Tab> tabs = new();
    private readonly List<ulong> retired = new();
    private readonly CefDevelopmentEngine engine = new();
    private readonly DispatcherTimer pump = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
    private readonly string? fixture;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    private Tab? active;
    private Tab? first, second;
    private ulong firstId, secondId;
    private Task<IReadOnlyList<string>>? emptyDomains, firstDomains, secondDomains;
    private Task<int?>? cookieDeletion;
    private Task<bool>? profileCookieDeletion;
    private Task<double?>? firstUsage, secondUsage, rejectedUsage;
    private Task<bool>? storageClear;
    private int deletionAuthorizationChecks;
    private int phase, result;
    private bool shuttingDown, windowClosed, allowWindowClose;

    private TabbedDevelopmentHost(string? fixture)
    {
        this.fixture = fixture;
        result = fixture is null ? 0 : 40;
        engine.InitializeAsync(CefDevelopmentEngine.FixtureProfile, NetworkSettings.Standard).GetAwaiter().GetResult();

        DockPanel.SetDock(strip, Dock.Top);
        layout.Children.Add(strip);
        strip.Children.Add(add);
        strip.Children.Add(close);
        var toolbar = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        foreach (var button in new[] { back, forward, reload })
        {
            DockPanel.SetDock(button, Dock.Left);
            toolbar.Children.Add(button);
        }
        DockPanel.SetDock(go, Dock.Right);
        toolbar.Children.Add(go);
        toolbar.Children.Add(address);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(status);
        layout.Children.Add(pages);
        window.Content = layout;

        add.Click += (_, _) => AddTab();
        close.Click += (_, _) => { if (active is { } tab) RequestClose(tab); };
        back.Click += (_, _) => WithActive(view => view.GoBack());
        forward.Click += (_, _) => WithActive(view => view.GoForward());
        reload.Click += (_, _) => WithActive(view => view.Reload());
        go.Click += (_, _) => NavigateAddress();
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
            request.Cancel = tab.Closing || shuttingDown;
        };
        view.ProcessFailed += (_, _) =>
        {
            status.Text = "CEF child process failed.";
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

    private void Select(Tab tab)
    {
        if (shuttingDown || tab.Closing || !tabs.Contains(tab)) return;
        active = tab;
        foreach (var item in tabs)
        {
            item.View.Element.Visibility = ReferenceEquals(item, active) ? Visibility.Visible : Visibility.Hidden;
            item.Header.FontWeight = ReferenceEquals(item, active) ? FontWeights.Bold : FontWeights.Normal;
        }
        address.Text = tab.View.Source;
        tab.LastSource = tab.View.Source;
        RefreshToolbar();
    }

    private void WithActive(Action<CefDevelopmentView> operation)
    {
        if (active is not { Closing: false } tab || tab.View.NativeHandle == 0 ||
            CefNative.VeilCefClosed(tab.View.NativeHandle) != 0) return;
        try { operation(tab.View); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { status.Text = error.Message; }
    }

    private void NavigateAddress() => WithActive(view => view.Navigate(address.Text));

    private void RequestClose(Tab tab)
    {
        if (tab.Closing || !tabs.Contains(tab)) return;
        tab.Closing = true;
        tab.ClosingId = tab.View.NativeHandle;
        tab.Header.IsEnabled = false;
        CefNative.VeilCefCancelOwnedAttachment(tab.ClosingId);
        CefNative.VeilCefTryClose(tab.ClosingId);
        if (ReferenceEquals(active, tab))
        {
            active = null;
            var next = tabs.FirstOrDefault(candidate => !candidate.Closing);
            if (next is not null && !shuttingDown) Select(next);
            else { tab.View.Element.Visibility = Visibility.Hidden; address.Clear(); }
        }
    }

    private void BeginShutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        foreach (var tab in tabs.ToArray()) RequestClose(tab);
    }

    private void RefreshToolbar()
    {
        var live = active is { Closing: false } tab && tab.View.NativeHandle != 0 &&
                   CefNative.VeilCefClosed(tab.View.NativeHandle) == 0;
        go.IsEnabled = reload.IsEnabled = address.IsEnabled = live && !shuttingDown;
        back.IsEnabled = live && !shuttingDown && active!.View.CanGoBack;
        forward.IsEnabled = live && !shuttingDown && active!.View.CanGoForward;
        close.IsEnabled = active is { Closing: false } && !shuttingDown;
        add.IsEnabled = !shuttingDown && tabs.Count + retired.Count < CefNative.VeilCefViewLimit();
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
        if (elapsed.Elapsed > TimeSpan.FromSeconds(45))
        {
            Log($"Tabbed fixture timeout at phase {phase}");
            BeginShutdown();
            return;
        }
        string Url(string path)
        {
            var url = new Uri(new Uri(fixture!), path);
            return path is "/tabs-second" or "/tabs-preserved" or "/tabs-cleared-second" or "/tabs-storage-kept"
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
        else if (phase == 2 && first?.View.DocumentTitle == "TAB_FIRST" && second?.View.DocumentTitle == "TAB_SECOND")
        {
            Select(first);
            if (address.Text != Url("/tabs-first") || first.View.NativeHandle != firstId ||
                second.View.NativeHandle != secondId || first.View.Element.Visibility != Visibility.Visible ||
                second.View.Element.Visibility != Visibility.Hidden) { Fail(); return; }
            Select(second);
            if (address.Text != Url("/tabs-second") || first.View.NativeHandle != firstId ||
                second.View.NativeHandle != secondId) { Fail(); return; }
            Log("Tab selection preserved both native identities and active address");
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
            result = 0;
            BeginShutdown();
        }
    }
}
