using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Veil.Desktop;

public partial class BrowserWindow : Window
{
    /// <summary>A tab and its engine view. <see cref="Owner"/> changes when the tab moves between windows,
    /// so every engine event is routed through it rather than captured at creation.</summary>
    private sealed class BrowserTab(IBrowserEngineView engineView, BrowserWindow owner)
    {
        internal IBrowserEngineView EngineView { get; } = engineView;
        // Transitional native boundary: security events, cookies, downloads and CDP remain WebView2-specific.
        internal WebView2 View => ((WebView2EngineView)EngineView).NativeView;
        internal BrowserWindow Owner { get; set; } = owner;
        internal TabItem Item { get; set; } = null!;
        internal string Title { get; set; } = "새 탭";
        internal string Url { get; set; } = "about:blank";
        internal TabHeader Header { get; set; } = null!;
        internal bool Loading { get; set; }
        internal bool Pinned { get; set; }
        internal FrameworkElement? ManagementSurface { get; set; }
        internal Action? CancelManagement { get; set; }
        internal long NavigationEpoch { get; set; }
        internal bool Closing { get; set; }
        internal (string Method, string Uri, long Epoch)? LastDownloadRequest { get; set; }
        internal bool LoginFormDetected { get; set; }
        /// <summary>HTTPS-first: the plain-HTTP address this tab is currently trying as HTTPS.</summary>
        internal Uri? UpgradedFrom { get; set; }
        /// <summary>The plain-HTTP address the HTTPS warning page offers to open.</summary>
        internal Uri? PendingHttp { get; set; }
        /// <summary>The address the VEIL threat warning page blocked (ADR-0008).</summary>
        internal Uri? PendingThreat { get; set; }
        /// <summary>Result of the last completed navigation (diagnostics and tests).</summary>
        internal CoreWebView2WebErrorStatus? LastNavigationError { get; set; }
    }

    private readonly BrowserSession session;
    private readonly IReadOnlyList<string>? initialUrls;
    private readonly int initialSelected;
    private readonly int initialPinnedCount;
    private readonly TabItem? adopted;
    private readonly BrowserWindow? adoptionSource;
    private bool closing;
    private bool restoring = true;
    private bool smokeRun;
    private bool openingDownloads;
    private string? startupAddress;
    private LibraryWindow? bookmarkLibrary;

    private BrowserData data => session.Data;
    private Stack<ClosedTab> closedTabs => session.ClosedTabs;
    private List<string> downloads => session.Downloads;
    private string statePath => session.StatePath;
    private CoreWebView2Environment? engine => session.Engine;
    private BrowserTab? Active => (Tabs.SelectedItem as TabItem)?.Tag as BrowserTab;
    internal bool IsClosing => closing;
    internal bool IsRestoring => restoring;

    internal BrowserWindow(string root, DownloadVault vault) : this(new BrowserSession(root, vault), null, 0, null) { }

    /// <summary>Additional window: restores <paramref name="urls"/>, or adopts a tab dragged out of another window.</summary>
    private BrowserWindow(BrowserSession session, IReadOnlyList<string>? urls, int selected, TabItem? adopt, int pinnedCount = 0)
    {
        this.session = session;
        initialUrls = urls;
        initialSelected = selected;
        initialPinnedCount = pinnedCount;
        adopted = adopt;
        adoptionSource = (adopt?.Tag as BrowserTab)?.Owner;
        session.Windows.Add(this);
        InitializeComponent();
        if (session.IsPrivate)
        {
            Title = "VEIL · 일회용 탐색";
            PrivateIndicator.Visibility = Visibility.Visible;
            BookmarkButton.Visibility = Visibility.Collapsed;
        }
        ActiveDownloadsList.ItemsSource = session.ActiveDownloads;
        PopupActiveDownloads.ItemsSource = session.ActiveDownloads;
        InitializeDownloadToolbar();
        RefreshBookmarkBar();
        RefreshDownloadProgress();
        InitializeMotion(playIntro: adopt is null);
        InitializeWindowFeatures();
        InitializeAddressSuggestions();
        Loaded += Start;
        Closing += OnClosing;
        PreviewKeyDown += BrowserKeyDown;
        Deactivated += (_, _) => managementFocusEpoch++;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (closing) return;
        bool last = session.Windows.Count(window => !window.closing) == 1;
        if (last && !restoring && !session.IsEnding) SaveTabs();
        if (last || session.IsEnding)
            foreach (var download in session.ActiveDownloads.ToArray()) download.Cancel();
        closing = true;
        foreach (var item in Tabs.Items.OfType<TabItem>()) ((BrowserTab)item.Tag).Closing = true;
        DownloadPopup.IsOpen = false;
        downloadRingTimer.Stop();
        retryDownload = null;
        RecentDownloadsList.ItemsSource = null;
        ActiveDownloadsList.ItemsSource = null;
        PopupActiveDownloads.ItemsSource = null;
        CloseAddressSuggestions();
        session.Windows.Remove(this);
        if (!last && !session.IsEnding)
        {
            // Closing one of several windows closes its tabs; they stay reopenable.
            foreach (TabItem item in Tabs.Items)
                if (item.Tag is BrowserTab tab) session.RememberClosedTab(new ClosedTab(tab.Url, tab.Pinned));
            SaveSession();
        }
        foreach (var tab in Tabs.Items.OfType<TabItem>().Select(item => (BrowserTab)item.Tag).ToArray())
            ClearManagement(tab);
        if (session.IsPrivate) DeletePrivateProfile();
        foreach (var view in Pages.Children.OfType<WebView2>())
            view.Dispose();
        session.ReleasePrivate();
        session.StopProxyMonitorIfIdle();
    }

    internal void PrepareForProtectedShutdown()
    {
        try { session.Save(); }
        finally { session.IsEnding = true; }
    }

    private async void Start(object sender, RoutedEventArgs args)
    {
        Loaded -= Start;
        try
        {
            await session.EngineHost.InitializeAsync(session.Root, session.Network);
            if (closing || session.IsEnding || session.EngineLost) return;
            await session.StartProxyMonitorAsync();
            UpdateNetworkIndicator();
            if (closing || session.IsEnding || session.EngineLost) return;
            if (adopted is not null)
            {
                if (adoptionSource is null || !adoptionSource.TransferTabTo(adopted, this, 0, initializingTarget: true))
                {
                    Close();
                    return;
                }
            }
            else
            {
                bool primary = initialUrls is null;
                var urls = primary ? data.Tabs : initialUrls!;
                int selected = primary ? data.Selected : initialSelected;
                string[] list = urls.Count == 0 ? ["about:blank"] : urls.ToArray();
                int savedPins = primary ? data.PinnedCount : initialPinnedCount;
                int pins = savedPins >= 0 && savedPins <= urls.Count ? savedPins : 0;
                for (int i = 0; i < list.Length; i++)
                {
                    string url = list[i];
                    await OpenTab(IsWebUrl(url) || IsInternalUrl(url) ? url : "about:blank", pinned: i < pins);
                }
                if (closing) return;
                if (Tabs.Items.Count > 0)
                    Tabs.SelectedIndex = Math.Clamp(selected, 0, Tabs.Items.Count - 1);
                if (primary && data.Windows is { } others)
                    foreach (var other in others) OpenWindow(other.Tabs, other.Selected, cascade: true, pinnedCount: other.PinnedCount);
            }
            restoring = false;
            if (adopted is not null) SaveSession();
            Status.Text = "준비";
            if (startupAddress is { } external)
            {
                startupAddress = null;
                if (await OpenTab(external) is null && !closing) Status.Text = "외부 링크를 열지 못했습니다.";
            }
            if (Active?.Url == "about:blank") FocusAddress();
        }
        catch (Exception)
        {
            if (!closing)
            {
                if (adopted is not null && !Tabs.Items.Contains(adopted)) { Close(); return; }
                Status.Text = "테스트 엔진을 시작하지 못했습니다. WebView2 런타임과 프로필 위치를 확인하세요.";
            }
        }
    }

    internal void SetStartupAddress(string? address) => startupAddress = ExternalAddress.Parse(address);

    internal async Task<bool> OpenExternalAddressAsync(string? address)
    {
        var main = session.MainSession;
        if (main.IsEnding || main.EngineLost || !main.EngineHost.IsReady ||
            main.PasswordUiOpen || main.PermissionUiOpen || main.SettingsUiOpen || main.VaultUiOpen) return false;
        string? url = ExternalAddress.Parse(address);
        if (address is not null && url is null) return false;
        var target = main.Windows.FirstOrDefault(w => !w.closing && !w.restoring && w.IsActive) ??
            main.Windows.FirstOrDefault(w => !w.closing && !w.restoring);
        if (target is null)
        {
            if (main.Windows.Any(w => !w.closing)) return false;
            // Only private windows remain; do not route the external address into them.
            target = new BrowserWindow(main, ["about:blank"], 0, null);
            target.SetStartupAddress(url);
            target.Show();
            return true; // accepted for startup, not a claim of successful navigation
        }
        if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
        target.Activate();
        return url is null || await target.OpenTab(url) is not null;
    }

    /// <summary>Opens another window sharing this profile's engine and data.</summary>
    private BrowserWindow OpenWindow(IReadOnlyList<string> urls, int selected, bool cascade = false,
        TabItem? adopt = null, Point? at = null, int pinnedCount = 0)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var window = new BrowserWindow(session, urls, selected, adopt, pinnedCount)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = bounds.Width,
            Height = bounds.Height,
            Left = at?.X ?? bounds.Left + (cascade ? 32 : 0),
            Top = at?.Y ?? bounds.Top + (cascade ? 32 : 0),
        };
        if (ShowActivated == false) { window.ShowActivated = false; window.ShowInTaskbar = false; }
        window.Show();
        return window;
    }

    private void PrivateWindowClick(object sender, RoutedEventArgs args)
    {
        if (closing || restoring || session.IsEnding || session.EngineLost || !session.EngineHost.IsReady) return;
        OpenPrivateWindow();
    }

    private BrowserWindow OpenPrivateWindow()
    {
        var window = new BrowserWindow(session.CreatePrivate(), ["about:blank"], 0, null)
        {
            Width = Width, Height = Height, ShowActivated = ShowActivated, ShowInTaskbar = ShowInTaskbar,
        };
        window.Show();
        return window;
    }

    private void DeletePrivateProfile()
    {
        // Only a verified, randomly named private profile is ever eligible for deletion.
        foreach (var view in Pages.Children.OfType<WebView2>())
        {
            try
            {
                var profile = view.CoreWebView2?.Profile;
                if (profile is null || !profile.IsInPrivateModeEnabled ||
                    !string.Equals(profile.ProfileName, session.PrivateProfileName, StringComparison.OrdinalIgnoreCase)) continue;
                profile.Delete(); // engine schedules deletion; physical erasure is not guaranteed
                break;
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException) { }
        }
    }

    /// <summary>Initialized engine views of this window's tabs (Security Manager).</summary>
    internal IEnumerable<CoreWebView2> CoreViews() => Tabs.Items.OfType<TabItem>()
        .Select(item => (item.Tag as BrowserTab)?.View.CoreWebView2).OfType<CoreWebView2>();

    private async Task<BrowserTab?> OpenTab(string? url, bool select = true, int? index = null, bool pinned = false)
    {
        if (!session.EngineHost.IsReady || closing || session.IsEnding || session.EngineLost ||
            (session.IsPrivate && IsInternalUrl(url))) return null;
        // Reject an unsupported backend before creating or attaching any content.
        if (session.EngineHost is not WebView2BrowserEngine)
            throw new InvalidOperationException("The selected engine has no approved security integration.");
        var engineView = session.EngineHost.CreateView(session.PrivateProfileName);
        var tab = new BrowserTab(engineView, this) { Pinned = pinned };
        var view = tab.View;
        var item = new TabItem { Tag = tab };
        tab.Item = item;
        tab.Header = CreateTabHeader(item, tab);
        UpdatePinnedHeader(tab);
        Pages.Children.Add(view);
        try
        {
            await engineView.InitializeAsync();
            if (closing || session.IsEnding || session.EngineLost)
            {
                engineView.Dispose();
                Pages.Children.Remove(view);
                return null;
            }
            var web = view.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = true;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreBrowserAcceleratorKeysEnabled = false;
            // ADR-0007: SmartScreen policy before the first navigation (an unset view re-enables it for all).
            if (!session.Security.Apply(web))
            {
                Status.Text = session.Security.Describe();
                UpdateSecurityIndicators();
            }
            // SEC-004/007: the engine writes download bytes (GUID.tmp) before DownloadStarting can cancel
            // them. Keep that partial data on the profile volume instead of the user's Downloads folder.
            string downloadScratch = Path.Combine(session.Root, ProfileScratch.FolderName,
                session.IsPrivate ? session.PrivateProfileName! : "downloads");
            Directory.CreateDirectory(downloadScratch);
            web.Profile.DefaultDownloadFolderPath = downloadScratch;
            // ADR-0006 standard protection: strict tracking prevention for every profile.
            web.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
            web.PermissionRequested += (_, args) => tab.Owner.OnSitePermissionRequested(tab, args);
            web.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            web.NavigationStarting += (_, args) => tab.Owner.OnNavigationStarting(tab, args);
            web.SourceChanged += (_, _) => tab.Owner.OnSourceChanged(tab);
            web.HistoryChanged += (_, _) => { if (tab.Owner.Active == tab) tab.Owner.UpdateNavigationButtons(); };
            web.DocumentTitleChanged += (_, _) => tab.Owner.OnTitleChanged(tab);
            web.FaviconChanged += async (_, _) => await tab.Owner.UpdateFavicon(tab, web);
            web.NavigationCompleted += (_, args) => tab.Owner.OnNavigationCompleted(tab, args);
            web.DownloadStarting += (_, args) => tab.Owner.OnDownloadStarting(tab, args);
            // Record navigation methods so the vault never replays a POST download as GET.
            if (!session.IsPrivate)
            {
                web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
                web.WebResourceRequested += (_, args) =>
                {
                    if (!IsWebUrl(args.Request.Uri)) return;
                    session.Vault.NoteRequest(args.Request.Method, args.Request.Uri);
                    tab.LastDownloadRequest = (args.Request.Method, args.Request.Uri, tab.NavigationEpoch);
                };
            }
            web.NewWindowRequested += (_, args) => tab.Owner.OnNewWindowRequested(tab, args);
            web.WindowCloseRequested += (_, _) => tab.Owner.Dispatcher.BeginInvoke(() => tab.Owner.CloseTab(tab.Item));
            web.IsDocumentPlayingAudioChanged += (_, _) => tab.Owner.UpdateAudio(tab);
            web.IsMutedChanged += (_, _) => tab.Owner.UpdateAudio(tab);
            web.ProcessFailed += (_, args) => tab.Owner.OnProcessFailed(tab, args);
            web.ContainsFullScreenElementChanged += (_, _) =>
            {
                if (tab.Owner.Active == tab) tab.Owner.SetFullScreen(web.ContainsFullScreenElement, page: true);
            };
            // SelectionChanged can synchronously save the session. Publish an internal route
            // before inserting the item, since native pages may never navigate the WebView.
            if (IsInternalUrl(url))
            {
                tab.Url = url!;
                tab.Title = InternalTitle(tab.Url);
                tab.Header.Label.Text = tab.Title;
            }
            Tabs.Items.Insert(TabInsertionIndex(tab.Pinned, index ?? Tabs.Items.Count), item);
            LayoutTabs(item);
            if (select || Tabs.SelectedItem is null) Tabs.SelectedItem = item;
            if (IsInternalUrl(url))
            {
                if (!IsManagementUrl(url)) engineView.ShowInternalHtml(BuildInternalPage(url));
            }
            else if (url is not null && url != "about:blank")
            {
                tab.Url = url;
                engineView.Navigate(url);
            }
            UpdateTabToolTip(tab);
            if (Active == tab) RefreshChrome();
            else if (IsInternalUrl(url))
            {
                // Background internal tabs have no selection event to persist them.
                UpdateManagementVisibility();
                SaveTabs();
            }
            return tab;
        }
        catch (Exception)
        {
            engineView.Dispose();
            Pages.Children.Remove(view);
            if (Tabs.Items.Contains(item)) Tabs.Items.Remove(item);
            if (!closing) Status.Text = "탭을 열 수 없습니다. WebView2 런타임과 테스트 프로필을 확인하세요.";
            return null;
        }
    }

    // ── Engine events, always handled by the tab's current window ───────────

    private bool AcceptTabEvents(BrowserTab tab) => !closing && !tab.Closing && !session.IsEnding && !session.EngineLost &&
        ReferenceEquals(tab.Owner, this);

    private void OnNavigationStarting(BrowserTab tab, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!AcceptTabEvents(tab)) { args.Cancel = true; return; }
        tab.NavigationEpoch++;
        tab.LastDownloadRequest = null;
        if (retryDownload?.Tab == tab) retryDownload = null;
        tab.LoginFormDetected = false;
        if (Active == tab) UpdateLoginAction();
        // Internal management routes are browser-chrome commands, never web navigation.
        if (args.Uri.StartsWith("veil:", StringComparison.OrdinalIgnoreCase) || IsManagementUrl(tab.Url))
        {
            args.Cancel = true;
            return;
        }
        if (HandleHttpsFirst(tab, args)) return;
        if (HandleThreat(tab, args)) return;
        args.Cancel = args.Uri != "about:blank" &&
            !args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            !IsWebUrl(args.Uri) && !IsInternalUrl(args.Uri);
        if (!args.Cancel && IsWebUrl(args.Uri) && IsInternalUrl(tab.Url))
        {
            tab.Url = args.Uri;
            UpdateTabToolTip(tab);
        }
        if (args.Cancel && Active == tab) Status.Text = "지원하지 않는 탐색 주소입니다.";
        if (!args.Cancel && args.Uri != "about:blank") SetTabLoading(tab, true);
    }

    private void OnSourceChanged(BrowserTab tab)
    {
        if (!AcceptTabEvents(tab) || IsInternalUrl(tab.Url)) return;
        tab.Url = tab.View.CoreWebView2.Source;
        UpdateTabToolTip(tab);
        if (Active == tab) RefreshChrome();
    }

    private void OnTitleChanged(BrowserTab tab)
    {
        if (!AcceptTabEvents(tab)) return;
        var web = tab.View.CoreWebView2;
        if (IsInternalUrl(tab.Url))
        {
            tab.Title = InternalTitle(tab.Url);
            UpdateTabToolTip(tab);
            tab.Header.Label.Text = tab.Title;
            return;
        }
        tab.Title = tab.Url == "about:blank" ? "새 탭" : string.IsNullOrWhiteSpace(web.DocumentTitle) ? tab.Url : web.DocumentTitle;
        UpdateTabToolTip(tab);
        var label = tab.Header.Label;
        if (label.Text == tab.Title) return;
        label.Text = tab.Title;
        Motion.Enter(label, 0, dx: 8, ms: 450);
    }

    private void OnNavigationCompleted(BrowserTab tab, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!AcceptTabEvents(tab)) return;
        SetTabLoading(tab, false);
        tab.LastNavigationError = args.IsSuccess ? null : args.WebErrorStatus;
        if (tab.UpgradedFrom is { } plain && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            tab.UpgradedFrom = null;
            if (!args.IsSuccess)
            {
                ShowHttpsWarning(tab, plain, $"HTTPS 연결에 실패했습니다 ({args.WebErrorStatus}).");
                return;
            }
        }
        if (!session.IsPrivate && args.IsSuccess && IsWebUrl(tab.Url))
            data.History.Insert(0, new PageEntry(tab.Title, tab.Url, DateTimeOffset.Now));
        if (Active == tab)
        {
            if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                Status.Text = args.IsSuccess ? "준비" : $"페이지를 불러오지 못했습니다 ({args.WebErrorStatus}).";
            UpdateNavigationButtons();
        }
        if (args.IsSuccess)
        {
            SaveTabs();
            if (Active == tab) _ = ProbeLoginFormAsync(tab);
        }
    }

    /// <summary>Engine browser exit ends the session cleanly (app state saved once, every window closed so the
    /// profile lock and protected-volume relock run); a renderer failure only affects that page.</summary>
    private void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs args)
    {
        if (args.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            if (Active == tab) Status.Text = "페이지 프로세스가 중단되었습니다. 새로고침(F5)하세요.";
            return;
        }
        if (closing || session.EngineLost) return;
        session.EngineLost = true;
        try { session.MainSession.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        session.IsEnding = true;
        foreach (var window in session.AllWindows.ToList())
            window.Status.Text = "브라우저 엔진이 종료되었습니다. VEIL을 다시 시작하세요.";
        if (smokeRun) return; // the smoke harness observes and closes on its own
        MessageBox.Show(this, "브라우저 엔진이 예기치 않게 종료되었습니다. 열린 탭은 저장되었습니다. VEIL을 다시 시작하세요.",
            "VEIL", MessageBoxButton.OK, MessageBoxImage.Warning);
        foreach (var window in session.AllWindows.ToList()) window.Close();
    }

    /// <summary>Phase 4-0: the engine's download is always cancelled. Supported requests are fetched again
    /// by VEIL straight into the encrypted vault; everything else is blocked (no plaintext fallback).</summary>
    private void OnDownloadStarting(BrowserTab tab, CoreWebView2DownloadStartingEventArgs args)
    {
        if (!AcceptTabEvents(tab)) { args.Cancel = true; args.Handled = true; return; }
        if (session.IsPrivate)
        {
            args.Cancel = true;
            args.Handled = true;
            Status.Text = "일회용 탐색에서는 다운로드를 지원하지 않습니다.";
            return;
        }
        var operation = args.DownloadOperation;
        long engineReceived = operation.BytesReceived;
        long engineOnDisk = ScratchBytes();
        args.Cancel = true;
        args.Handled = true;
        string fileName = Path.GetFileName(args.ResultFilePath);
        // The engine has already received (and HTTP-cached) the start of the body before this event.
        _ = PurgeEngineCacheAsync(tab.View.CoreWebView2);
        var vault = session.Vault;
        string raw = operation.Uri;
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            string webSource = tab.View.CoreWebView2.Source;
            long epoch = tab.NavigationEpoch;
            bool sourceReady = Uri.TryCreate(webSource, UriKind.Absolute, out var origin) && InlineDownloadPolicy.SourceAllowed(origin);
            bool allowed = InlineDownloadPolicy.Enabled && vault.Available && !session.InlineCapturePending &&
                sourceReady && raw.Length <= InlineDownloadPolicy.MaxUriChars && CanUseTab(tab.Item) && Active == tab &&
                !IsInternalUrl(tab.Url);
            if (!allowed)
            {
                const string detail = "Data 다운로드 미지원 또는 제한 초과";
                vault.Results.Add(new VaultResult(fileName, DownloadVault.Blocked, 0, detail, null, engineReceived, engineOnDisk));
                ShowDownloadOutcome(fileName, DownloadVault.Blocked, detail, "Data 다운로드를 안전하게 처리할 수 없어 차단했습니다.");
                return;
            }
            session.InlineCapturePending = true;
            bool ContextReady() => CanUseTab(tab.Item) && tab.NavigationEpoch == epoch &&
                tab.View.CoreWebView2?.Source == webSource && !IsInternalUrl(tab.Url);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (ContextReady())
                        await CaptureAsync(tab.View.CoreWebView2, origin!, fileName, engineReceived, engineOnDisk,
                            inlinePayload: raw, inlineReady: ContextReady);
                    else
                    {
                        const string detail = "원래 다운로드 페이지 변경";
                        vault.Results.Add(new VaultResult(fileName, DownloadVault.Blocked, 0, detail, null, engineReceived, engineOnDisk));
                        if (!closing) ShowDownloadOutcome(fileName, DownloadVault.Blocked, detail, "원래 페이지에서 다시 다운로드하세요.");
                    }
                }
                catch (Exception error) when (RecoverableFailure.Is(error))
                {
                    // An exception escaping this async dispatcher callback would terminate the browser.
                    if (!closing) ShowDownloadOutcome(fileName, DownloadVault.Failed, error.GetType().Name, "다운로드를 저장하지 못했습니다.");
                }
                finally { session.InlineCapturePending = false; }
            }), DispatcherPriority.Normal);
            return;
        }
        string? unsupported = Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? vault.Unsupported(uri) : "미지원 방식";
        if (unsupported is not null)
        {
            vault.Results.Add(new VaultResult(fileName, DownloadVault.Blocked, 0, unsupported, null, engineReceived, engineOnDisk));
            ShowDownloadOutcome(fileName, DownloadVault.Blocked, unsupported, $"{fileName}: 보호 저장을 지원하지 않는 다운로드라 차단했습니다.");
            return;
        }
        // WebView2 callbacks cannot safely run a nested modal message loop. Schedule capture
        // after DownloadStarting returns, keeping the original request cancelled and protected.
        var web = tab.View.CoreWebView2;
        RetryDownload? retry = null;
        if (uri is not null && tab.LastDownloadRequest is { } request && request.Epoch == tab.NavigationEpoch &&
            string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Uri, uri.AbsoluteUri, StringComparison.Ordinal) &&
            DownloadFailurePolicy.SafeRetryAddress(uri) && Uri.TryCreate(web.Source, UriKind.Absolute, out var source) &&
            source.Scheme == "https" && source.GetLeftPart(UriPartial.Authority) == uri.GetLeftPart(UriPartial.Authority))
            retry = new RetryDownload(tab, uri, fileName, tab.NavigationEpoch, web.Source, DateTimeOffset.UtcNow.AddMinutes(5));
        tab.LastDownloadRequest = null;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!closing && !session.IsEnding && !session.EngineLost)
                _ = CaptureAsync(web, uri!, fileName, engineReceived, engineOnDisk, retry);
        }), DispatcherPriority.Normal);
    }

    private async Task CaptureAsync(CoreWebView2 web, Uri uri, string fileName, long engineReceived, long engineOnDisk,
        RetryDownload? candidate = null, bool retry = false, string? inlinePayload = null, Func<bool>? inlineReady = null)
    {
        var vault = session.Vault;
        using var download = new BrowserDownload(fileName);
        session.ActiveDownloads.Add(download);
        foreach (var window in session.Windows) window.RefreshDownloadProgress();
        Status.Text = $"{fileName} · Ctrl+J에서 진행 상황을 확인할 수 있습니다.";

        void Report(string outcome, string? detail, string message, VaultEntry? entry = null)
        {
            vault.Results.Add(new VaultResult(fileName, outcome, entry?.Bytes ?? download.Bytes, detail,
                entry is null ? null : vault.ObjectPath(entry), engineReceived, engineOnDisk));
            var window = closing ? session.Windows.FirstOrDefault() : this;
            window?.ShowDownloadOutcome(fileName, outcome, detail, message);
        }

        bool open = false;
        try
        {
            download.WaitForVault();
            open = await EnsureVaultOpenAsync(download.Token);
            download.Token.ThrowIfCancellationRequested();
            if (!open)
            {
                Report(DownloadVault.Blocked, "금고 잠김", $"{fileName}: 금고를 열지 않아 저장하지 않았습니다.");
                return;
            }
            download.WaitForTransfer();
            if (retry && (candidate is null || !CanRetry(candidate)))
            {
                Report(DownloadVault.Blocked, "원래 다운로드 페이지 변경", "원래 페이지에서 다시 다운로드하세요.");
                return;
            }
            if (inlinePayload is not null && inlineReady?.Invoke() != true)
            {
                Report(DownloadVault.Blocked, "원래 다운로드 페이지 변경", "원래 페이지에서 다시 다운로드하세요.");
                return;
            }
            var entry = inlinePayload is null ? await vault.CaptureAsync(uri, download, web, retry,
                retryReady: retry ? () => candidate is not null && CanRetry(candidate) : null) :
                await vault.CaptureInlineAsync(inlinePayload, uri, download);
            Report(DownloadVault.Stored, null, $"{fileName}을(를) 암호화 금고에 보관했습니다.", entry);
        }
        catch (OperationCanceledException) when (download.Token.IsCancellationRequested)
        {
            Report(DownloadVault.Cancelled, null, $"{fileName} 다운로드를 취소했습니다.");
        }
        catch (ThreatBlockedException threat)
        {
            Report(DownloadVault.Blocked, "VEIL 위협 목록", $"{fileName}: {threat.Message}. 저장하지 않았습니다.");
        }
        catch (InvalidDataException) when (inlinePayload is not null)
        {
            Report(DownloadVault.Blocked, "Data 형식 또는 크기 제한", "지원하지 않는 Data 다운로드를 차단했습니다.");
        }
        catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or InvalidOperationException
            or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.COMException)
        {
            string reason = DownloadFailurePolicy.Describe(error, download.Phase, open);
            if (!retry && open && download.Phase == DownloadPhase.Request &&
                error is DownloadRequestFailure { Retryable: true } && candidate is not null && CanRetry(candidate))
                retryDownload = candidate;
            Report(DownloadVault.Failed, reason, $"{fileName}을(를) 보관하지 못했습니다. {reason}");
        }
        finally
        {
            session.ActiveDownloads.Remove(download);
            foreach (var window in session.Windows) window.RefreshDownloadProgress();
        }
    }

    /// <summary>Opens the vault: Debug test variables, else the setup wizard (first use) or the unlock dialog.
    /// Smoke runs never show dialogs. A recovery-code unlock requires a new passphrase and code at once.</summary>
    private async Task<bool> EnsureVaultOpenAsync(CancellationToken cancellation = default)
    {
        await session.VaultOpenGate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (closing || session.IsEnding || session.EngineLost) return false;
            return await OpenVaultAsync(cancellation);
        }
        finally { session.VaultOpenGate.Release(); }
    }

    private async Task<bool> OpenVaultAsync(CancellationToken cancellation)
    {
        bool CanContinue() => !closing && !session.IsEnding && !session.EngineLost && !cancellation.IsCancellationRequested;
        if (!CanContinue()) return false;
        var vault = session.Vault;
        if (vault.Unlocked) return true;
        if (vault.UsesProfilePassword) return false;
        if (!vault.Available) return false;
        if (await vault.TryOpenFromDevelopmentEnvironmentAsync()) return CanContinue();
        if (smokeRun || !CanContinue()) return false;
        if (!vault.Exists)
        {
            var setup = new VaultSetupDialog(this, "다운로드 금고 만들기", askPassphrase: true, issueCode: true, allowRestore: true);
            if (setup.ShowDialog() != true || !CanContinue()) return false;
            bool restoringBackup = setup.Backup is not null;
            Status.Text = restoringBackup ? "암호화 금고를 복원하는 중입니다." : "암호화 금고를 만드는 중입니다.";
            string? failure = setup.Backup is { } backup
                ? await vault.RestoreAsync(backup)
                : await vault.CreateAsync(setup.Passphrase!, setup.RecoveryCode!);
            if (failure is not null && CanContinue())
                MessageBox.Show(this, failure, restoringBackup ? "금고 백업 복원" : "다운로드 금고 만들기",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return failure is null && CanContinue();
        }
        string? error = null;
        for (int attempt = 0; attempt < 5 && CanContinue(); attempt++)
        {
            var unlock = new VaultUnlockDialog(this, error);
            if (unlock.ShowDialog() != true || !CanContinue()) return false;
            if (unlock.RecoveryCode is { } code)
            {
                error = await vault.RecoverAsync(code);
                if (error is not null) continue;
                bool resetSaved = false;
                try
                {
                    // A recovered vault must be relocked if shutdown/cancellation wins before credential reset.
                    if (!CanContinue()) return false;
                    var reset = new VaultSetupDialog(this, "복구 후 새 암호와 복구 코드", askPassphrase: true, issueCode: true);
                    if (reset.ShowDialog() != true || !CanContinue()) return false;
                    resetSaved = await vault.CompleteRecoveryAsync(reset.Passphrase!, reset.RecoveryCode!);
                    if (resetSaved) return CanContinue();
                    error = "새 인증 정보를 모두 저장하지 못했습니다. 다시 잠금을 해제하세요.";
                }
                finally
                {
                    if (!resetSaved) vault.Lock();
                }
                continue;
            }
            error = await vault.UnlockAsync(unlock.Passphrase!);
            if (error is null) return CanContinue();
            if (!CanContinue()) return false;
            await Task.Delay(500 * (attempt + 1), cancellation); // slow down repeated guesses
        }
        return false;
    }

    private async void VaultClick(object sender, RoutedEventArgs args)
    {
        if (FocusManagementPage("veil://vault")) return;
        if (!SettingsReady() || session.VaultUiOpen || session.SettingsUiOpen ||
            session.PermissionUiOpen || session.PasswordUiOpen) return;
        session.VaultUiOpen = true;
        session.PermissionUiOpen = true;
        try
        {
            if (await EnsureVaultOpenAsync())
            {
                if (SettingsReady())
                    await ShowManagementPageAsync("veil://vault", new VaultWindow(this, session.Vault,
                        () => SettingsReady() && Active?.Url == "veil://vault"));
            }
            else if (SettingsReady() && session.Vault.UsesProfilePassword)
                MessageBox.Show(this, "다운로드 금고가 잠겨 있습니다. VEIL을 다시 시작하고 보호 프로필 잠금 해제 단계에서 금고를 여세요.",
                    "다운로드 금고", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            if (SettingsReady()) Status.Text = "다운로드 금고를 열지 못했습니다.";
        }
        finally
        {
            session.VaultUiOpen = false;
            session.PermissionUiOpen = false;
            RefreshSharedDownloadPages();
        }
    }

    /// <summary>Drops the engine's HTTP disk cache after a cancelled download (twice: the engine can still
    /// finish writing the cancelled entry shortly after the event).</summary>
    private static async Task PurgeEngineCacheAsync(CoreWebView2 web)
    {
        try
        {
            await web.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            await Task.Delay(1500);
            await web.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Bytes the engine already put in its (profile-volume) download scratch before cancellation.</summary>
    private long ScratchBytes()
    {
        try
        {
            var scratch = new DirectoryInfo(Path.Combine(session.Root, ProfileScratch.FolderName, "downloads"));
            return scratch.Exists ? scratch.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length) : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return -1; }
    }

    private void ShowDownloadOutcome(string fileName, string outcome, string? detail, string message)
    {
        string label = detail is null ? outcome : $"{outcome}: {detail}";
        downloads.Insert(0, $"{fileName} · {label}");
        if (downloads.Count > 200) downloads.RemoveAt(200);
        if (outcome != DownloadVault.Stored)
        {
            session.RecentDownloadIssues.Insert(0, new DownloadIssue(Guid.NewGuid(), fileName, outcome, detail, DateTimeOffset.Now));
            if (session.RecentDownloadIssues.Count > 100) session.RecentDownloadIssues.RemoveAt(100);
        }
        Status.Text = message + " Ctrl+J에서 확인할 수 있습니다.";
        Dispatcher.BeginInvoke(() =>
        {
            RefreshSharedDownloadPages();
            if (!closing && !session.IsEnding && !session.EngineLost)
                Motion.Pop(Motion.Transforms(DownloadButton).Scale, 1.35);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // Results and vault visibility belong to the profile, not the originating window.
    private void RefreshSharedDownloadPages()
    {
        if (session.IsEnding || session.EngineLost) return;
        foreach (var window in session.Windows.ToArray())
        {
            if (window.closing) continue;
            window.RefreshDownloadProgress();
            foreach (var tab in window.Tabs.Items.OfType<TabItem>().Select(item => item.Tag).OfType<BrowserTab>().ToArray())
            {
                if (tab.Url == "veil://downloads" && !window.closingTabs.Contains(tab.Item) &&
                    tab.ManagementSurface is DownloadOverview overview) overview.Refresh();
            }
        }
    }

    private async void OnNewWindowRequested(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        args.Handled = true;
        if (!CanUseTab(opener.Item) || (args.Uri != "about:blank" && !IsWebUrl(args.Uri))) return;
        long epoch = opener.NavigationEpoch;
        try
        {
            // Like other browsers, tabs opened from a page appear right after their opener.
            var popup = await OpenTab(null, args.IsUserInitiated, Tabs.Items.IndexOf(opener.Item) + 1);
            if (popup is null) return;
            if (CanUseTab(opener.Item) && opener.NavigationEpoch == epoch && CanUseTab(popup.Item))
                args.NewWindow = popup.View.CoreWebView2;
            else if (!closing && CanUseTab(popup.Item))
                await CloseTabAsync(popup.Item);
        }
        catch (Exception error) when (RecoverableFailure.Is(error)) { if (!closing) Status.Text = "새 창 요청을 열지 못했습니다."; }
    }

    private void UpdateAudio(BrowserTab tab)
    {
        var web = tab.View.CoreWebView2;
        bool show = web.IsDocumentPlayingAudio || web.IsMuted;
        var button = tab.Header.Audio;
        if (show && button.Visibility != Visibility.Visible)
            Motion.Enter(button, 0, scale: 0.3, ms: 500, ease: Motion.Spring);
        button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        button.Content = web.IsMuted ? "" : "";
        button.ToolTip = web.IsMuted ? "탭 음소거 해제" : "탭 음소거";
    }

    private void ToggleMute(BrowserTab tab)
    {
        if (tab.View.CoreWebView2 is { } web) web.IsMuted = !web.IsMuted;
    }

    private static void UpdateTabToolTip(BrowserTab tab)
    {
        string title = (tab.Pinned ? "[고정] " : "") + tab.Title;
        tab.Item.ToolTip = tab.Url == "about:blank" ? title : $"{title}\n{tab.Url}";
        System.Windows.Automation.AutomationProperties.SetName(tab.Item, title);
    }

    // ── Tab lifetime ─────────────────────────────────────────────────────────

    private async void CloseTab(TabItem item)
    {
        try { await CloseTabAsync(item); }
        catch (Exception error) when (RecoverableFailure.Is(error)) { if (!closing) Status.Text = "탭을 닫지 못했습니다."; }
    }

    private async Task CloseTabAsync(TabItem item, bool protectPinned = false)
    {
        if (closing || session.IsEnding || closingTabs.Contains(item) || !Tabs.Items.Contains(item)) return;
        var tab = (BrowserTab)item.Tag;
        if (tab.Closing || protectPinned && tab.Pinned) return;
        if (session.IsPrivate && Tabs.Items.Count == 1) { Close(); return; }
        // Invalidate page events and pending native actions before yielding to the close animation.
        tab.Closing = true;
        tab.NavigationEpoch++;
        if (retryDownload?.Tab == tab) retryDownload = null;
        try
        {
            ClearManagement(tab);
            if (!await AnimateTabClose(item) || closing || session.IsEnding || !Tabs.Items.Contains(item)) return;
            session.RememberClosedTab(new ClosedTab(tab.Url, tab.Pinned));
            ClearManagement(tab);
            Tabs.Items.Remove(item);
            Pages.Children.Remove(tab.View);
            tab.EngineView.Dispose();
            LayoutTabs();
            if (Tabs.Items.Count == 0)
            {
                // The last tab of an extra window closes the window, as in other browsers.
                if (session.Windows.Count(window => !window.closing) > 1) { Close(); return; }
                await OpenTab("about:blank");
            }
            SaveTabs();
        }
        finally
        {
            // A failed close must not leave an attached tab permanently unusable.
            if (!closing && !session.IsEnding && Tabs.Items.Contains(item))
                tab.Closing = false;
        }
    }

    private void TabChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!IsLoaded || args.OriginalSource != Tabs || closing) return;
        managementFocusEpoch++;
        CloseAddressSuggestions();
        foreach (var tab in Tabs.Items.OfType<TabItem>().Select(item => (BrowserTab)item.Tag).ToArray())
            if (tab != Active && tab.Url == "veil://passwords" && tab.CancelManagement is not null) ClearManagement(tab);
        SetAddressText(Active?.Url is null or "about:blank" ? "" : Active.Url);
        UpdateManagementVisibility();
        if (fullScreen && fullScreenFromPage && Active?.View.CoreWebView2?.ContainsFullScreenElement != true) SetFullScreen(false);
        RefreshChrome();
        if (Active is { } selected) _ = ProbeLoginFormAsync(selected);
        SyncProgress();
        SaveTabs();
    }

    private void RefreshChrome()
    {
        UpdateHttpsBar();
        UpdateThreatBar();
        if (!Address.IsKeyboardFocusWithin)
            SetAddressText(Active?.Url is null or "about:blank" ? "" : Active.Url);
        Home.Visibility = Active?.Url is null or "about:blank" ? Visibility.Visible : Visibility.Collapsed;
        UpdateManagementVisibility();
        UpdateNavigationButtons();
        UpdateBookmarkGlyph();
        UpdateLoginAction();
        RefreshDownloadProgress();
    }

    private void RefreshDownloadProgress()
    {
        bool hasDownloads = session.ActiveDownloads.Count > 0;
        DownloadButton.Visibility = session.IsPrivate ? Visibility.Collapsed : Visibility.Visible;
        UpdateDownloadToolbar();
        DownloadProgressPanel.Visibility = Active?.Url == "veil://downloads"
            ? Visibility.Visible : Visibility.Collapsed;
        ActiveDownloadsList.Visibility = hasDownloads ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelDownloadClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: BrowserDownload download }) download.Cancel();
    }

    private void UpdateNavigationButtons()
    {
        Back.IsEnabled = !IsManagementUrl(Active?.Url) && Active?.EngineView.CanGoBack == true;
        Forward.IsEnabled = !IsManagementUrl(Active?.Url) && Active?.EngineView.CanGoForward == true;
        Reload.IsEnabled = IsWebUrl(Active?.Url);
    }

    internal static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && IsWebUrl(url);

    private static bool IsWebUrl(Uri url) =>
        (url.Scheme is "http" or "https") && !string.IsNullOrEmpty(url.Host) && url.UserInfo.Length == 0;

    internal static bool IsInternalUrl(string? value) =>
        IsManagementUrl(value) || value is HttpsWarningUrl or ThreatWarningUrl;

    private static string InternalTitle(string? value) => value switch
    {
        "veil://bookmarks" => "북마크",
        "veil://history" => "방문 기록",
        "veil://downloads" => "다운로드",
        "veil://settings" => "설정",
        "veil://passwords" => "비밀번호 관리자",
        "veil://vault" => "다운로드 금고",
        "veil://settings/network" => "네트워크",
        "veil://settings/security" => "보안",
        "veil://settings/search" => "검색 및 주소창",
        "veil://settings/site-data" => "쿠키 및 사이트 데이터",
        "veil://settings/permissions" => "사이트 권한",
        "veil://tabs" => "탭 검색",
        HttpsWarningUrl => "안전하지 않은 연결",
        ThreatWarningUrl => "위험한 사이트",
        _ => "새 탭",
    };

    internal static Uri? ResolveAddress(string input, string? searchEngine = null)
    {
        input = input.Trim();
        if (input.Length == 0 || AddressInputPolicy.HasUnsafeCharacters(input)) return null;
        if (SearchPolicy.TryExplicitSearch(input, searchEngine, out var search)) return search;
        if (IsInternalUrl(input)) return new Uri(input);
        if (AddressInputPolicy.TryResolve(input, out var address)) return address;
        return SearchPolicy.Search(searchEngine, input);
    }

    private string BuildInternalPage(string? url)
    {
        // Sensitive download metadata is now rendered only by native chrome.
        string title = InternalTitle(url);
        const string body = "";
        return $$"""
<!doctype html>
<html><head><meta charset="utf-8"><style>
*{box-sizing:border-box}body{margin:0;background:#0A0C13;color:#E9ECF5;font:13px Segoe UI,Malgun Gothic,sans-serif}
main{max-width:920px;margin:0 auto;padding:28px 32px}h1{margin:0 0 18px;font-size:24px}
.row{display:block;text-decoration:none;color:inherit;border:1px solid #262A3B;border-radius:10px;background:#171A26;padding:12px 14px;margin:8px 0}
.row:hover{border-color:#8B7CFF;background:#1E2231}strong,.row span{display:block}.row span{color:#8B91A7;margin-top:4px}
.empty{border:1px solid #262A3B;border-radius:12px;padding:48px;text-align:center;color:#8B91A7}
</style></head><body><main><h1>{{EscapeHtml(title)}}</h1>{{body}}</main></body></html>
""";
    }

    private static string EscapeHtml(string? value) =>
        (value ?? "").Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);

    // ── Persistence ──────────────────────────────────────────────────────────

    internal (List<string> Tabs, int Selected) Snapshot() =>
        // The HTTPS warning page is not restored as such: its HTTP address is upgraded again next time.
        (Tabs.Items.Cast<TabItem>().Select(item => (BrowserTab)item.Tag)
            .Select(tab => tab.Url == HttpsWarningUrl && tab.PendingHttp is { } plain ? plain.AbsoluteUri
                : tab.Url == ThreatWarningUrl && tab.PendingThreat is { } blocked ? blocked.AbsoluteUri : tab.Url).ToList(), Tabs.SelectedIndex);

    private void SaveTabs()
    {
        if (restoring || closing) return;
        SaveSession();
    }

    private void SaveSession()
    {
        try { session.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "테스트 프로필 변경사항을 저장하지 못했습니다.";
        }
    }

    private void SaveData()
    {
        if (session.IsPrivate) return;
        try { data.Save(statePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "테스트 프로필 변경사항을 저장하지 못했습니다.";
        }
    }

    // ── Address bar ─────────────────────────────────────────────────────────

    private void FocusAddress()
    {
        CloseAddressSuggestions();
        SetAddressText(Active?.Url is null or "about:blank" ? "" : Active.Url);
        Address.Focus();
        Address.SelectAll();
    }

    private void AddressKeyDown(object sender, KeyEventArgs args)
    {
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.Escape)
        {
            args.Handled = true;
            if (AddressSuggestionsPopup.IsOpen) CloseAddressSuggestions();
            else FocusAddress();
            return;
        }
        if (key is Key.Down or Key.Up && AddressSuggestionsPopup.IsOpen && Keyboard.Modifiers == ModifierKeys.None)
        {
            args.Handled = true;
            MoveAddressSuggestion(key == Key.Down ? 1 : -1);
            return;
        }
        if (key != Key.Enter) return;
        args.Handled = true;
        SubmitAddress(Keyboard.Modifiers);
    }

    private void SubmitAddress(ModifierKeys modifiers)
    {
        if (AddressSuggestionsPopup.IsOpen && AddressSuggestionsList.SelectedItem is AddressSuggestion selected &&
            !modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenAddressSuggestion(selected, modifiers.HasFlag(ModifierKeys.Alt));
            return;
        }
        CloseAddressSuggestions();
        string text = Address.Text.Trim();
        // Ctrl+Enter completes a bare name to www.<name>.com, as in Chrome and Edge.
        if (modifiers.HasFlag(ModifierKeys.Control) && text.Length > 0 && !text.Contains('.') &&
            !text.StartsWith('?') && !text.StartsWith('@') &&
            !text.Contains(':') && !text.Contains('/') && !text.Any(char.IsWhiteSpace))
            text = $"www.{text}.com";
        if (IsManagementUrl(text)) { OpenManagementRoute(text); return; }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Alt+Enter opens the address in a new tab next to the current one.
            if (ResolveAddress(text, data.SearchEngine) is { } url && Active is { } current)
                _ = OpenTab(IsInternalUrl(url.OriginalString) ? url.OriginalString : url.AbsoluteUri, index: Tabs.Items.IndexOf(current.Item) + 1);
            return;
        }
        NavigateAddress(text);
    }

    private void NavigateAddress(string input)
    {
        CloseAddressSuggestions();
        if (session.IsPrivate && IsInternalUrl(input.Trim()))
        {
            Status.Text = "일회용 탐색에서는 일반 프로필 관리 페이지를 열 수 없습니다.";
            return;
        }
        if (IsManagementUrl(input.Trim())) { OpenManagementRoute(input.Trim()); return; }
        var url = ResolveAddress(input, data.SearchEngine);
        if (url is null)
        {
            Status.Text = "웹 주소 또는 검색어를 입력하세요.";
            return;
        }
        if (Active is not { } tab || closingTabs.Contains(tab.Item)) return;
        ClearManagement(tab);
        tab.PendingHttp = null;
        tab.PendingThreat = null;
        tab.UpgradedFrom = null;
        SetAddressText(url.AbsoluteUri);
        if (IsInternalUrl(url.OriginalString))
        {
            tab.Url = url.OriginalString;
            tab.Title = InternalTitle(tab.Url);
            tab.Header.Label.Text = tab.Title;
            tab.View.Visibility = Visibility.Visible;
            tab.EngineView.ShowInternalHtml(BuildInternalPage(tab.Url));
            RefreshChrome();
            UpdateTabToolTip(tab);
            return;
        }
        tab.Url = url.AbsoluteUri;
        tab.View.Visibility = Visibility.Visible;
        tab.View.Focus();
        tab.EngineView.Navigate(url.AbsoluteUri);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private void CycleTab(int direction)
    {
        var live = Tabs.Items.OfType<TabItem>().Where(CanUseTab).ToArray();
        if (live.Length == 0) return;
        int current = Array.IndexOf(live, Tabs.SelectedItem as TabItem);
        int next = current < 0 ? (direction > 0 ? 0 : live.Length - 1) :
            (current + direction + live.Length) % live.Length;
        Tabs.SelectedItem = live[next];
    }

    private void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count && Tabs.Items[index] is TabItem item && CanUseTab(item))
            Tabs.SelectedItem = item;
    }

    private void Zoom(double? change)
    {
        if (Active is { } tab) tab.EngineView.ZoomFactor = change is null ? 1 : Math.Clamp(tab.EngineView.ZoomFactor + change.Value, 0.25, 5);
    }

    private async void NewTabClick(object sender, RoutedEventArgs args)
    {
        try { if (await OpenTab("about:blank") is not null) FocusAddress(); }
        catch (Exception error) when (RecoverableFailure.Is(error)) { if (!closing) Status.Text = "탭을 열 수 없습니다."; }
    }

    private void FullScreenClick(object sender, RoutedEventArgs args) => SetFullScreen(!fullScreen);

    private void NewWindowClick(object sender, RoutedEventArgs args)
    {
        if (session.IsPrivate) { PrivateWindowClick(sender, args); return; }
        if (session.EngineHost.IsReady && !session.IsEnding && !session.EngineLost) OpenWindow(["about:blank"], 0, cascade: true);
    }

    private async void RestoreClick(object sender, RoutedEventArgs args)
    {
        if (session.IsPrivate || closing || restoring || session.IsEnding || session.EngineLost ||
            session.RestoringClosedTab || !closedTabs.TryPop(out var closed)) return;
        session.RestoringClosedTab = true;
        try
        {
            if (await OpenTab(closed.Url, pinned: closed.Pinned) is null)
                session.RememberClosedTab(closed);
        }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            session.RememberClosedTab(closed);
            if (!closing) Status.Text = "닫은 탭을 다시 열지 못했습니다.";
        }
        finally { session.RestoringClosedTab = false; }
    }

    private void BackClick(object sender, RoutedEventArgs args) { if (Back.IsEnabled) Active?.EngineView.GoBack(); }
    private void ForwardClick(object sender, RoutedEventArgs args) { if (Forward.IsEnabled) Active?.EngineView.GoForward(); }
    private void ReloadClick(object sender, RoutedEventArgs args)
    {
        if (!Reload.IsEnabled) return;
        Motion.Spin((System.Windows.Media.RotateTransform)ReloadGlyph.RenderTransform);
        Active?.EngineView.Reload();
    }

    private async void HardReload()
    {
        if (Active is not { } tab || !IsWebUrl(tab.Url)) return;
        Motion.Spin((System.Windows.Media.RotateTransform)ReloadGlyph.RenderTransform);
        try { await tab.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}"); }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            try { tab.EngineView.Reload(); }
            catch (Exception again) when (RecoverableFailure.Is(again)) { if (!closing) Status.Text = "페이지를 새로 고치지 못했습니다."; }
        }
    }

    private void StopLoading()
    {
        if (Active is { Loading: true } tab)
        {
            tab.EngineView.Stop();
            Status.Text = "페이지 로드를 중지했습니다.";
        }
    }

    private void GoHome()
    {
        if (Active is not { } tab || closingTabs.Contains(tab.Item)) return;
        ClearManagement(tab);
        tab.PendingHttp = null;
        tab.PendingThreat = null;
        tab.UpgradedFrom = null;
        tab.Url = "about:blank";
        tab.EngineView.Navigate("about:blank");
        RefreshChrome();
        FocusAddress();
    }

    private void MenuClick(object sender, RoutedEventArgs args)
    {
        var menu = MenuButton.ContextMenu;
        if (session.IsPrivate)
            foreach (var item in menu.Items.OfType<MenuItem>())
                item.Visibility = Equals(item.Tag, "private-ok") ? Visibility.Visible : Visibility.Collapsed;
        menu.PlacementTarget = MenuButton;
        menu.IsOpen = true;
    }

    private void BookmarkClick(object sender, RoutedEventArgs args)
    {
        if (!SettingsReady() || Active is not { } tab || closingTabs.Contains(tab.Item) || !IsWebUrl(tab.Url)) return;
        if (data.Bookmarks.All(entry => entry.Url != tab.Url))
        {
            data.Bookmarks.Add(new PageEntry(tab.Title, tab.Url, DateTimeOffset.Now));
            Status.Text = "북마크에 추가했습니다.";
            try { data.Save(statePath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                data.Bookmarks.RemoveAt(data.Bookmarks.Count - 1);
                Status.Text = "북마크를 저장하지 못했습니다.";
                return;
            }
            RefreshBookmarkBars();
            Motion.Pop((System.Windows.Media.ScaleTransform)BookmarkGlyph.RenderTransform, 1.6);
        }
        else Status.Text = "이미 북마크에 있습니다.";
    }

    private async void BookmarksClick(object sender, RoutedEventArgs args) => await GuardLibraryAsync(editable: true);
    private async void HistoryClick(object sender, RoutedEventArgs args) => await GuardLibraryAsync(editable: false);

    private async Task GuardLibraryAsync(bool editable)
    {
        try { await ShowLibraryAsync(editable); }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            if (!closing) Status.Text = editable ? "북마크 관리를 완료하지 못했습니다." : "방문 기록 관리를 완료하지 못했습니다.";
        }
    }

    private async Task ShowLibraryAsync(bool editable)
    {
        if (!SettingsReady()) return;
        string internalUrl = editable ? "veil://bookmarks" : "veil://history";
        if (FocusManagementPage(internalUrl)) return;
        var entries = editable ? data.Bookmarks : data.History;
        LibraryWindow? dialog = null;
        bool SaveLibrary()
        {
            try
            {
                data.Save(statePath);
                if (editable)
                    RefreshBookmarkBars(dialog);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        dialog = new LibraryWindow(this, editable ? "북마크 관리" : "방문 기록", entries, editable, SaveLibrary,
            editable ? data.BookmarkFolders : null, editable ? () =>
                SettingsReady() && Active is { Url: "veil://bookmarks", CancelManagement: not null } active &&
                !closingTabs.Contains(active.Item) && ReferenceEquals(bookmarkLibrary, dialog) : null);
        if (editable) bookmarkLibrary = dialog;
        try
        {
            bool open = await ShowManagementPageAsync(internalUrl, dialog);
            if (!SettingsReady()) return;
            if (open && dialog.SelectedUrl is { } url && IsWebUrl(url))
                _ = await OpenTab(url);
        }
        finally
        {
            if (ReferenceEquals(bookmarkLibrary, dialog)) bookmarkLibrary = null;
        }
    }
    private void ClearDownloadIssues()
    {
        if (!SettingsReady() || Active?.Url != "veil://downloads") return;
        session.RecentDownloadIssues.Clear();
        foreach (var window in session.Windows.ToArray())
            window.retryDownload = null;
        RefreshSharedDownloadPages();
    }

    private async void DownloadsClick(object sender, RoutedEventArgs args)
    {
        DownloadPopup.IsOpen = false;
        if (!SettingsReady() || openingDownloads) return;
        var existing = Tabs.Items.OfType<TabItem>().FirstOrDefault(item => item.Tag is BrowserTab { Url: "veil://downloads" } && !closingTabs.Contains(item));
        if (existing is not null) { Tabs.SelectedItem = existing; RefreshChrome(); return; }
        openingDownloads = true;
        try { _ = await OpenTab("veil://downloads"); }
        catch (Exception error) when (RecoverableFailure.Is(error)) { if (!closing) Status.Text = "다운로드 목록을 열지 못했습니다."; }
        finally { openingDownloads = false; }
    }

    // ── Find in page ─────────────────────────────────────────────────────────

    private void FindClick(object sender, RoutedEventArgs args)
    {
        ShowFindBar(true);
        FindText.Focus();
        FindText.SelectAll();
    }

    private async void FindKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter) await Find(Keyboard.Modifiers == ModifierKeys.Shift);
        if (args.Key == Key.Escape) FindCloseClick(sender, args);
    }

    private async void FindPreviousClick(object sender, RoutedEventArgs args) => await Find(true);
    private async void FindNextClick(object sender, RoutedEventArgs args) => await Find(false);
    private void FindCloseClick(object sender, RoutedEventArgs args) => ShowFindBar(false);

    private async Task Find(bool backwards)
    {
        if (FindText.Text.Length == 0) { FindClick(this, new RoutedEventArgs()); return; }
        if (Active is not { } tab) return;
        string text = System.Text.Json.JsonSerializer.Serialize(FindText.Text);
        string reverse = backwards ? "true" : "false";
        try
        {
            string result = await tab.View.ExecuteScriptAsync($"window.find({text}, false, {reverse}, true)");
            Status.Text = result == "true" ? "일치하는 텍스트를 찾았습니다." : "일치하는 텍스트가 없습니다.";
        }
        catch (Exception error) when (RecoverableFailure.Is(error)) { if (!closing) Status.Text = "페이지에서 검색할 수 없습니다."; }
    }
}
