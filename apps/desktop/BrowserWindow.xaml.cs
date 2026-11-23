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
    private sealed class BrowserTab(WebView2 view, BrowserWindow owner)
    {
        internal WebView2 View { get; } = view;
        internal BrowserWindow Owner { get; set; } = owner;
        internal TabItem Item { get; set; } = null!;
        internal string Title { get; set; } = "새 탭";
        internal string Url { get; set; } = "about:blank";
        internal TabHeader Header { get; set; } = null!;
        internal bool Loading { get; set; }
    }

    private readonly BrowserSession session;
    private readonly IReadOnlyList<string>? initialUrls;
    private readonly int initialSelected;
    private readonly TabItem? adopted;
    private bool closing;
    private bool restoring = true;
    private DispatcherTimer? downloadPopupTimer;

    private BrowserData data => session.Data;
    private Stack<string> closedTabs => session.ClosedTabs;
    private List<string> downloads => session.Downloads;
    private string statePath => session.StatePath;
    private CoreWebView2Environment? engine => session.Engine;
    private BrowserTab? Active => (Tabs.SelectedItem as TabItem)?.Tag as BrowserTab;
    internal bool IsClosing => closing;
    internal bool IsRestoring => restoring;

    internal BrowserWindow(string root) : this(new BrowserSession(root), null, 0, null) { }

    /// <summary>Additional window: restores <paramref name="urls"/>, or adopts a tab dragged out of another window.</summary>
    private BrowserWindow(BrowserSession session, IReadOnlyList<string>? urls, int selected, TabItem? adopt)
    {
        this.session = session;
        initialUrls = urls;
        initialSelected = selected;
        adopted = adopt;
        session.Windows.Add(this);
        InitializeComponent();
        InitializeMotion(playIntro: adopt is null);
        InitializeWindowFeatures();
        Loaded += Start;
        Closing += OnClosing;
        PreviewKeyDown += BrowserKeyDown;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (closing) return;
        bool last = session.Windows.Count(window => !window.closing) == 1;
        if (last && !restoring && !session.IsEnding) SaveTabs();
        closing = true;
        session.Windows.Remove(this);
        if (!last && !session.IsEnding)
        {
            // Closing one of several windows closes its tabs; they stay reopenable.
            foreach (TabItem item in Tabs.Items)
                if (IsWebUrl(((BrowserTab)item.Tag).Url)) closedTabs.Push(((BrowserTab)item.Tag).Url);
            SaveSession();
        }
        foreach (var view in Pages.Children.OfType<WebView2>())
            view.Dispose();
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
            if (session.Engine is null)
            {
                string profile = Path.GetFullPath(Path.Combine(session.Root, "webview2"));
                session.Engine = await Program.CreateEngine(profile);
            }
            if (closing) return;
            if (adopted is not null)
            {
                AttachTab(adopted, 0);
            }
            else
            {
                bool primary = initialUrls is null;
                var urls = primary ? data.Tabs : initialUrls!;
                int selected = primary ? data.Selected : initialSelected;
                string[] list = urls.Count == 0 ? ["about:blank"] : urls.ToArray();
                foreach (string url in list)
                    await OpenTab(IsWebUrl(url) || IsInternalUrl(url) ? url : "about:blank");
                if (closing) return;
                if (Tabs.Items.Count > 0)
                    Tabs.SelectedIndex = Math.Clamp(selected, 0, Tabs.Items.Count - 1);
                if (primary && data.Windows is { } others)
                    foreach (var other in others) OpenWindow(other.Tabs, other.Selected, cascade: true);
            }
            restoring = false;
            Status.Text = "준비";
            if (Active?.Url == "about:blank") FocusAddress();
        }
        catch (Exception)
        {
            if (!closing) Status.Text = "테스트 엔진을 시작하지 못했습니다. WebView2 런타임과 프로필 위치를 확인하세요.";
        }
    }

    /// <summary>Opens another window sharing this profile's engine and data.</summary>
    private BrowserWindow OpenWindow(IReadOnlyList<string> urls, int selected, bool cascade = false,
        TabItem? adopt = null, Point? at = null)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var window = new BrowserWindow(session, urls, selected, adopt)
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

    private async Task<BrowserTab?> OpenTab(string? url, bool select = true, int? index = null)
    {
        if (engine is null || closing) return null;
        var view = new WebView2 { Visibility = Visibility.Hidden };
        var tab = new BrowserTab(view, this);
        var item = new TabItem { Tag = tab };
        tab.Item = item;
        tab.Header = CreateTabHeader(item, tab);
        Pages.Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(engine);
            if (closing) return null;
            var web = view.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = true;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreBrowserAcceleratorKeysEnabled = false;
            // SEC-004/007: the engine writes download bytes (GUID.tmp) before DownloadStarting can cancel
            // them. Keep that partial data on the profile volume instead of the user's Downloads folder.
            string downloadScratch = Path.Combine(session.Root, ProfileScratch.FolderName, "downloads");
            Directory.CreateDirectory(downloadScratch);
            web.Profile.DefaultDownloadFolderPath = downloadScratch;
            web.PermissionRequested += (_, args) =>
            {
                args.SavesInProfile = false;
                args.State = CoreWebView2PermissionState.Deny;
            };
            web.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            web.NavigationStarting += (_, args) => tab.Owner.OnNavigationStarting(tab, args);
            web.SourceChanged += (_, _) => tab.Owner.OnSourceChanged(tab);
            web.HistoryChanged += (_, _) => { if (tab.Owner.Active == tab) tab.Owner.UpdateNavigationButtons(); };
            web.DocumentTitleChanged += (_, _) => tab.Owner.OnTitleChanged(tab);
            web.FaviconChanged += async (_, _) => await tab.Owner.UpdateFavicon(tab, web);
            web.NavigationCompleted += (_, args) => tab.Owner.OnNavigationCompleted(tab, args);
            web.DownloadStarting += (_, args) => tab.Owner.OnDownloadStarting(args);
            web.NewWindowRequested += (_, args) => tab.Owner.OnNewWindowRequested(tab, args);
            web.WindowCloseRequested += (_, _) => tab.Owner.Dispatcher.BeginInvoke(() => tab.Owner.CloseTab(tab.Item));
            web.IsDocumentPlayingAudioChanged += (_, _) => tab.Owner.UpdateAudio(tab);
            web.IsMutedChanged += (_, _) => tab.Owner.UpdateAudio(tab);
            web.ContainsFullScreenElementChanged += (_, _) =>
            {
                if (tab.Owner.Active == tab) tab.Owner.SetFullScreen(web.ContainsFullScreenElement, page: true);
            };
            Tabs.Items.Insert(Math.Clamp(index ?? Tabs.Items.Count, 0, Tabs.Items.Count), item);
            LayoutTabs(item);
            if (select || Tabs.SelectedItem is null) Tabs.SelectedItem = item;
            if (IsInternalUrl(url))
            {
                tab.Url = url!;
                tab.Title = InternalTitle(tab.Url);
                UpdateTabToolTip(tab);
                tab.Header.Label.Text = tab.Title;
                web.NavigateToString(BuildInternalPage(url));
            }
            else if (url is not null && url != "about:blank")
            {
                tab.Url = url;
                web.Navigate(url);
            }
            UpdateTabToolTip(tab);
            if (Active == tab) RefreshChrome();
            return tab;
        }
        catch (Exception)
        {
            view.Dispose();
            Pages.Children.Remove(view);
            if (Tabs.Items.Contains(item)) Tabs.Items.Remove(item);
            if (!closing) Status.Text = "탭을 열 수 없습니다. WebView2 런타임과 테스트 프로필을 확인하세요.";
            return null;
        }
    }

    // ── Engine events, always handled by the tab's current window ───────────

    private void OnNavigationStarting(BrowserTab tab, CoreWebView2NavigationStartingEventArgs args)
    {
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
    if (IsInternalUrl(tab.Url)) return;
        tab.Url = tab.View.CoreWebView2.Source;
        UpdateTabToolTip(tab);
        if (Active == tab) RefreshChrome();
    }

    private void OnTitleChanged(BrowserTab tab)
    {
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
        if (closing) return;
        SetTabLoading(tab, false);
        if (args.IsSuccess && IsWebUrl(tab.Url))
            data.History.Insert(0, new PageEntry(tab.Title, tab.Url, DateTimeOffset.Now));
        if (Active == tab)
        {
            if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                Status.Text = args.IsSuccess ? "준비" : $"페이지를 불러오지 못했습니다 ({args.WebErrorStatus}).";
            UpdateNavigationButtons();
        }
        if (args.IsSuccess) SaveTabs();
    }

    private void OnDownloadStarting(CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
        string fileName = Path.GetFileName(args.ResultFilePath);
        downloads.Insert(0, $"{fileName} · 취소됨");
        Status.Text = "다운로드를 취소했습니다. Ctrl+J에서 확인할 수 있습니다.";
        Dispatcher.BeginInvoke(() =>
        {
            DownloadButton.Visibility = Visibility.Visible;
            DownloadPopupText.Text = $"{fileName} 다운로드 요청이 차단되었습니다.";
            DownloadPopup.IsOpen = true;
            if (Active?.Url == "veil://downloads")
                Active.View.CoreWebView2.NavigateToString(BuildInternalPage("veil://downloads"));
            Motion.Pop(Motion.Transforms(DownloadButton).Scale, 1.35);
            downloadPopupTimer?.Stop();
            downloadPopupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            downloadPopupTimer.Tick += (_, _) =>
            {
                downloadPopupTimer.Stop();
                DownloadPopup.IsOpen = false;
            };
            downloadPopupTimer.Start();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void OnNewWindowRequested(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        args.Handled = true;
        if (args.Uri != "about:blank" && !IsWebUrl(args.Uri)) return;
        // Like other browsers, tabs opened from a page appear right after their opener.
        var popup = await OpenTab(null, args.IsUserInitiated, Tabs.Items.IndexOf(opener.Item) + 1);
        if (popup is not null && !closing)
            args.NewWindow = popup.View.CoreWebView2;
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

    private static void UpdateTabToolTip(BrowserTab tab) =>
        tab.Item.ToolTip = tab.Url == "about:blank" ? tab.Title : $"{tab.Title}\n{tab.Url}";

    // ── Tab lifetime ─────────────────────────────────────────────────────────

    private async void CloseTab(TabItem item)
    {
        if (closing || !Tabs.Items.Contains(item)) return;
        if (!await AnimateTabClose(item) || closing || !Tabs.Items.Contains(item)) return;
        var tab = (BrowserTab)item.Tag;
        if (IsWebUrl(tab.Url)) closedTabs.Push(tab.Url);
        Tabs.Items.Remove(item);
        Pages.Children.Remove(tab.View);
        tab.View.Dispose();
        LayoutTabs();
        if (Tabs.Items.Count == 0)
        {
            // The last tab of an extra window closes the window, as in other browsers.
            if (session.Windows.Count(window => !window.closing) > 1) { Close(); return; }
            await OpenTab("about:blank");
        }
        SaveTabs();
    }

    private void TabChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!IsLoaded || args.OriginalSource != Tabs || closing) return;
        foreach (TabItem item in Tabs.Items)
            ((BrowserTab)item.Tag).View.Visibility = item == Tabs.SelectedItem ? Visibility.Visible : Visibility.Hidden;
        if (fullScreen && fullScreenFromPage && Active?.View.CoreWebView2?.ContainsFullScreenElement != true) SetFullScreen(false);
        RefreshChrome();
        SyncProgress();
        SaveTabs();
    }

    private void RefreshChrome()
    {
        if (!Address.IsKeyboardFocusWithin)
            Address.Text = Active?.Url is null or "about:blank" ? "" : Active.Url;
        Home.Visibility = Active?.Url is null or "about:blank" ? Visibility.Visible : Visibility.Collapsed;
        if (Active is not null)
            Active.View.Visibility = Home.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        UpdateNavigationButtons();
        UpdateBookmarkGlyph();
    }

    private void UpdateNavigationButtons()
    {
        Back.IsEnabled = Active?.View.CoreWebView2?.CanGoBack == true;
        Forward.IsEnabled = Active?.View.CoreWebView2?.CanGoForward == true;
        Reload.IsEnabled = IsWebUrl(Active?.Url);
    }

    internal static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && IsWebUrl(url);

    private static bool IsWebUrl(Uri url) =>
        (url.Scheme is "http" or "https") && !string.IsNullOrEmpty(url.Host) && url.UserInfo.Length == 0;

    internal static bool IsInternalUrl(string? value) =>
        value is "veil://bookmarks" or "veil://history" or "veil://downloads";

    private static string InternalTitle(string? value) => value switch
    {
        "veil://bookmarks" => "북마크",
        "veil://history" => "방문 기록",
        "veil://downloads" => "다운로드",
        _ => "새 탭",
    };

    internal static Uri? ResolveAddress(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;
        if (IsInternalUrl(input)) return new Uri(input);
        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) && IsWebUrl(absolute)) return absolute;
        if (input.Contains("://") || input.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!input.Any(char.IsWhiteSpace))
        {
            string authority = input.Split('/', '?', '#')[0];
            if ((authority.Contains('.') || authority.StartsWith('[') || authority == "localhost" ||
                authority.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)) &&
                Uri.TryCreate("https://" + input, UriKind.Absolute, out var candidate) && IsWebUrl(candidate))
                return candidate.IsLoopback ? new UriBuilder(candidate) { Scheme = "http", Port = candidate.IsDefaultPort ? -1 : candidate.Port }.Uri : candidate;
            if (input.Contains(':')) return null;
        }
        return new Uri("https://duckduckgo.com/?q=" + Uri.EscapeDataString(input));
    }

    private string BuildInternalPage(string? url)
    {
        var rows = url switch
        {
            "veil://bookmarks" => data.Bookmarks.Select(entry =>
                $@"<a class=""row"" href=""{EscapeHtml(entry.Url)}""><strong>{EscapeHtml(entry.Title)}</strong><span>{EscapeHtml(entry.Url)}</span></a>"),
            "veil://history" => data.History.Select(entry =>
                $@"<a class=""row"" href=""{EscapeHtml(entry.Url)}""><strong>{EscapeHtml(entry.Title)}</strong><span>{EscapeHtml(entry.Url)}</span></a>"),
            "veil://downloads" => downloads.Select(entry =>
                $@"<div class=""row""><strong>{EscapeHtml(entry)}</strong><span>금고 검증 전까지 차단됨</span></div>"),
            _ => [],
        };
        string title = InternalTitle(url);
        string empty = url switch
        {
            "veil://bookmarks" => "저장한 북마크가 없습니다.",
            "veil://history" => "방문 기록이 없습니다.",
            "veil://downloads" => "차단한 다운로드가 없습니다.",
            _ => "",
        };
        string body = rows.Any() ? string.Concat(rows) : $"<div class=\"empty\">{EscapeHtml(empty)}</div>";
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
        (Tabs.Items.Cast<TabItem>().Select(item => ((BrowserTab)item.Tag).Url).ToList(), Tabs.SelectedIndex);

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
        try { data.Save(statePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "테스트 프로필 변경사항을 저장하지 못했습니다.";
        }
    }

    // ── Address bar ─────────────────────────────────────────────────────────

    private void FocusAddress()
    {
        Address.Text = Active?.Url is null or "about:blank" ? "" : Active.Url;
        Address.Focus();
        Address.SelectAll();
    }

    private void AddressKeyDown(object sender, KeyEventArgs args)
    {
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.Escape) { FocusAddress(); return; }
        if (key != Key.Enter) return;
        args.Handled = true;
        string text = Address.Text.Trim();
        // Ctrl+Enter completes a bare name to www.<name>.com, as in Chrome and Edge.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && text.Length > 0 && !text.Contains('.') &&
            !text.Contains(':') && !text.Contains('/') && !text.Any(char.IsWhiteSpace))
            text = $"www.{text}.com";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Alt+Enter opens the address in a new tab next to the current one.
            if (ResolveAddress(text) is { } url && Active is { } current)
                _ = OpenTab(url.AbsoluteUri, index: Tabs.Items.IndexOf(current.Item) + 1);
            return;
        }
        NavigateAddress(text);
    }

    private void NavigateAddress(string input)
    {
        var url = ResolveAddress(input);
        if (url is null)
        {
            Status.Text = "웹 주소 또는 검색어를 입력하세요.";
            return;
        }
        if (Active is not { } tab) return;
        Address.Text = url.AbsoluteUri;
        if (IsInternalUrl(url.AbsoluteUri))
        {
            tab.Url = url.AbsoluteUri;
            tab.Title = InternalTitle(tab.Url);
            tab.Header.Label.Text = tab.Title;
            tab.View.Visibility = Visibility.Visible;
            tab.View.CoreWebView2.NavigateToString(BuildInternalPage(tab.Url));
            RefreshChrome();
            UpdateTabToolTip(tab);
            return;
        }
        tab.Url = url.AbsoluteUri;
        tab.View.Visibility = Visibility.Visible;
        tab.View.Focus();
        tab.View.CoreWebView2.Navigate(url.AbsoluteUri);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private void CycleTab(int direction)
    {
        int count = Tabs.Items.Count;
        if (count > 0) Tabs.SelectedIndex = (Tabs.SelectedIndex + direction + count) % count;
    }

    private void SelectTab(int index)
    {
        if (Tabs.Items.Count > 0) Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.Items.Count - 1);
    }

    private void Zoom(double? change)
    {
        if (Active is { } tab) tab.View.ZoomFactor = change is null ? 1 : Math.Clamp(tab.View.ZoomFactor + change.Value, 0.25, 5);
    }

    private async void NewTabClick(object sender, RoutedEventArgs args)
    {
        if (await OpenTab("about:blank") is not null) FocusAddress();
    }

    private void FullScreenClick(object sender, RoutedEventArgs args) => SetFullScreen(!fullScreen);

    private void NewWindowClick(object sender, RoutedEventArgs args)
    {
        if (engine is not null) OpenWindow(["about:blank"], 0, cascade: true);
    }

    private async void RestoreClick(object sender, RoutedEventArgs args)
    {
        if (closedTabs.TryPop(out string? url) && await OpenTab(url) is null) closedTabs.Push(url);
    }

    private void BackClick(object sender, RoutedEventArgs args) { if (Back.IsEnabled) Active?.View.GoBack(); }
    private void ForwardClick(object sender, RoutedEventArgs args) { if (Forward.IsEnabled) Active?.View.GoForward(); }
    private void ReloadClick(object sender, RoutedEventArgs args)
    {
        if (!Reload.IsEnabled) return;
        Motion.Spin((System.Windows.Media.RotateTransform)ReloadGlyph.RenderTransform);
        Active?.View.Reload();
    }

    private async void HardReload()
    {
        if (Active is not { } tab || !IsWebUrl(tab.Url)) return;
        Motion.Spin((System.Windows.Media.RotateTransform)ReloadGlyph.RenderTransform);
        try { await tab.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}"); }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            tab.View.Reload();
        }
    }

    private void StopLoading()
    {
        if (Active is { Loading: true } tab)
        {
            tab.View.CoreWebView2.Stop();
            Status.Text = "페이지 로드를 중지했습니다.";
        }
    }

    private void GoHome()
    {
        if (Active is not { } tab) return;
        tab.Url = "about:blank";
        tab.View.CoreWebView2.Navigate("about:blank");
        RefreshChrome();
        FocusAddress();
    }

    private void MenuClick(object sender, RoutedEventArgs args)
    {
        var menu = MenuButton.ContextMenu;
        menu.PlacementTarget = MenuButton;
        menu.IsOpen = true;
    }

    private void BookmarkClick(object sender, RoutedEventArgs args)
    {
        if (Active is not { } tab || !IsWebUrl(tab.Url)) return;
        if (data.Bookmarks.All(entry => entry.Url != tab.Url))
        {
            data.Bookmarks.Add(new PageEntry(tab.Title, tab.Url, DateTimeOffset.Now));
            Status.Text = "북마크에 추가했습니다.";
            SaveData();
            UpdateBookmarkGlyph();
            Motion.Pop((System.Windows.Media.ScaleTransform)BookmarkGlyph.RenderTransform, 1.6);
        }
        else Status.Text = "이미 북마크에 있습니다.";
    }

    private async void BookmarksClick(object sender, RoutedEventArgs args) =>
        _ = await OpenTab("veil://bookmarks");
    private async void HistoryClick(object sender, RoutedEventArgs args) =>
        _ = await OpenTab("veil://history");
    private async void DownloadsClick(object sender, RoutedEventArgs args)
    {
        DownloadPopup.IsOpen = false;
        _ = await OpenTab("veil://downloads");
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
        catch (InvalidOperationException) { Status.Text = "페이지에서 검색할 수 없습니다."; }
    }
}
