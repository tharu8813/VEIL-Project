using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Veil.Desktop;

public partial class BrowserWindow : Window
{
    private sealed class BrowserTab(WebView2 view)
    {
        internal WebView2 View { get; } = view;
        internal string Title { get; set; } = "새 탭";
        internal string Url { get; set; } = "about:blank";
    }

    private readonly string root;
    private readonly string statePath;
    private readonly BrowserData data;
    private readonly Stack<string> closedTabs = new();
    private readonly List<string> downloads = [];
    private CoreWebView2Environment? engine;
    private bool closing;
    private bool restoring = true;
    private BrowserTab? Active => (Tabs.SelectedItem as TabItem)?.Tag as BrowserTab;

    internal BrowserWindow(string root)
    {
        this.root = root;
        statePath = Path.Combine(root, "browser-state.json");
        data = BrowserData.Load(statePath);
        InitializeComponent();
        Loaded += Start;
        Closing += (_, _) =>
        {
            if (!restoring) SaveTabs();
            closing = true;
            foreach (var view in Pages.Children.OfType<WebView2>())
                view.Dispose();
        };
        PreviewKeyDown += BrowserKeyDown;
    }

    private async void Start(object sender, RoutedEventArgs args)
    {
        Loaded -= Start;
        try
        {
            string profile = Path.GetFullPath(Path.Combine(root, "webview2"));
            engine = await Program.CreateEngine(profile);
            if (closing) return;
            string[] urls = data.Tabs.Count == 0 ? ["about:blank"] : data.Tabs.ToArray();
            foreach (string url in urls)
                await OpenTab(IsWebUrl(url) ? url : "about:blank");
            if (closing) return;
            if (Tabs.Items.Count > 0)
                Tabs.SelectedIndex = Math.Clamp(data.Selected, 0, Tabs.Items.Count - 1);
            restoring = false;
            Status.Text = "준비";
            if (Active?.Url == "about:blank") FocusAddress();
        }
        catch (Exception)
        {
            if (!closing) Status.Text = "테스트 엔진을 시작하지 못했습니다. WebView2 런타임과 프로필 위치를 확인하세요.";
        }
    }

    private async Task<BrowserTab?> OpenTab(string? url, bool select = true)
    {
        if (engine is null || closing) return null;
        var view = new WebView2 { Visibility = Visibility.Hidden };
        var tab = new BrowserTab(view);
        var item = new TabItem { Tag = tab };
        var close = new Button { Content = "×", Padding = new Thickness(5, 0, 5, 0), MinHeight = 20 };
        System.Windows.Automation.AutomationProperties.SetName(close, "탭 닫기");
        close.Click += (_, _) => CloseTab(item);
        var label = new TextBlock { Text = tab.Title, Width = 115, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(label);
        header.Children.Add(close);
        item.Header = header;
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
            web.PermissionRequested += (_, args) =>
            {
                args.SavesInProfile = false;
                args.State = CoreWebView2PermissionState.Deny;
            };
            web.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            web.NavigationStarting += (_, args) =>
            {
                args.Cancel = args.Uri != "about:blank" && !IsWebUrl(args.Uri);
                if (args.Cancel && Active == tab) Status.Text = "지원하지 않는 탐색 주소입니다.";
            };
            web.SourceChanged += (_, _) =>
            {
                tab.Url = web.Source;
                if (Active == tab) RefreshChrome();
            };
            web.HistoryChanged += (_, _) => { if (Active == tab) UpdateNavigationButtons(); };
            web.DocumentTitleChanged += (_, _) =>
            {
                tab.Title = tab.Url == "about:blank" ? "새 탭" : string.IsNullOrWhiteSpace(web.DocumentTitle) ? tab.Url : web.DocumentTitle;
                label.Text = tab.Title;
            };
            web.NavigationCompleted += (_, args) =>
            {
                if (closing) return;
                if (args.IsSuccess && IsWebUrl(tab.Url))
                {
                    data.History.Insert(0, new PageEntry(tab.Title, tab.Url, DateTimeOffset.Now));
                }
                if (Active == tab)
                {
                    if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                        Status.Text = args.IsSuccess ? "준비" : $"페이지를 불러오지 못했습니다 ({args.WebErrorStatus}).";
                    UpdateNavigationButtons();
                }
                if (args.IsSuccess) SaveTabs();
            };
            web.DownloadStarting += (_, args) =>
            {
                args.Cancel = true;
                args.Handled = true;
                downloads.Insert(0, $"{Path.GetFileName(args.ResultFilePath)} · 취소됨");
                Status.Text = "다운로드를 취소했습니다. Ctrl+J에서 확인할 수 있습니다.";
            };
            web.NewWindowRequested += async (_, args) =>
            {
                using var deferral = args.GetDeferral();
                args.Handled = true;
                if (args.Uri != "about:blank" && !IsWebUrl(args.Uri)) return;
                var popup = await OpenTab(null, args.IsUserInitiated);
                if (popup is not null && !closing)
                    args.NewWindow = popup.View.CoreWebView2;
            };
            web.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() => CloseTab(item));
            Tabs.Items.Add(item);
            if (select || Tabs.SelectedItem is null) Tabs.SelectedItem = item;
            if (url is not null && url != "about:blank")
            {
                tab.Url = url;
                web.Navigate(url);
            }
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

    private async void CloseTab(TabItem item)
    {
        if (closing || !Tabs.Items.Contains(item)) return;
        var tab = (BrowserTab)item.Tag;
        if (IsWebUrl(tab.Url)) closedTabs.Push(tab.Url);
        Tabs.Items.Remove(item);
        Pages.Children.Remove(tab.View);
        tab.View.Dispose();
        if (Tabs.Items.Count == 0) await OpenTab("about:blank");
        SaveTabs();
    }

    private void TabChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!IsLoaded || args.OriginalSource != Tabs || closing) return;
        foreach (TabItem item in Tabs.Items)
            ((BrowserTab)item.Tag).View.Visibility = item == Tabs.SelectedItem ? Visibility.Visible : Visibility.Hidden;
        RefreshChrome();
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

    internal static Uri? ResolveAddress(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;
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

    private void SaveTabs()
    {
        if (restoring || closing) return;
        data.Tabs = Tabs.Items.Cast<TabItem>().Select(item => ((BrowserTab)item.Tag).Url).ToList();
        data.Selected = Tabs.SelectedIndex;
        SaveData();
    }

    private void SaveData()
    {
        try { data.Save(statePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "테스트 프로필 변경사항을 저장하지 못했습니다.";
        }
    }

    private void FocusAddress()
    {
        Address.Text = Active?.Url is null or "about:blank" ? "" : Active.Url;
        Address.Focus();
        Address.SelectAll();
    }

    private void AddressKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { FocusAddress(); return; }
        if (args.Key != Key.Enter) return;
        args.Handled = true;
        NavigateAddress(Address.Text);
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
        tab.Url = url.AbsoluteUri;
        tab.View.Visibility = Visibility.Visible;
        tab.View.Focus();
        tab.View.CoreWebView2.Navigate(url.AbsoluteUri);
    }

    private void BrowserKeyDown(object sender, KeyEventArgs args)
    {
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        var command = (Keyboard.Modifiers, key) switch
        {
            (ModifierKeys.Control, Key.T) => (Action)(() => NewTabClick(sender, args)),
            (ModifierKeys.Control, Key.W) => () => { if (Tabs.SelectedItem is TabItem item) CloseTab(item); },
            (ModifierKeys.Control, Key.L) => FocusAddress,
            (ModifierKeys.Control, Key.D) => () => BookmarkClick(sender, args),
            (ModifierKeys.Control, Key.H) => () => HistoryClick(sender, args),
            (ModifierKeys.Control, Key.J) => () => DownloadsClick(sender, args),
            (ModifierKeys.Control, Key.B) => () => BookmarksClick(sender, args),
            (ModifierKeys.Control, Key.F) => () => FindClick(sender, args),
            (ModifierKeys.Control, Key.R) or (ModifierKeys.None, Key.F5) => () => ReloadClick(sender, args),
            (ModifierKeys.Control, Key.Tab) => () => CycleTab(1),
            (ModifierKeys.Control, Key.Add or Key.OemPlus) => () => Zoom(0.1),
            (ModifierKeys.Control, Key.Subtract or Key.OemMinus) => () => Zoom(-0.1),
            (ModifierKeys.Control, Key.D0 or Key.NumPad0) => () => Zoom(null),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.T) => () => RestoreClick(sender, args),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.Tab) => () => CycleTab(-1),
            (ModifierKeys.Alt, Key.Left) => () => BackClick(sender, args),
            (ModifierKeys.Alt, Key.Right) => () => ForwardClick(sender, args),
            _ => null
        };
        if (command is null) return;
        args.Handled = true;
        // The WebView2 accelerator callback can block the browser until input routing returns.
        Dispatcher.BeginInvoke(command);
    }

    private void CycleTab(int direction)
    {
        int count = Tabs.Items.Count;
        if (count > 0) Tabs.SelectedIndex = (Tabs.SelectedIndex + direction + count) % count;
    }

    private void Zoom(double? change)
    {
        if (Active is { } tab) tab.View.ZoomFactor = change is null ? 1 : Math.Clamp(tab.View.ZoomFactor + change.Value, 0.5, 3);
    }

    private async void NewTabClick(object sender, RoutedEventArgs args)
    {
        if (await OpenTab("about:blank") is not null) FocusAddress();
    }

    private async void RestoreClick(object sender, RoutedEventArgs args)
    {
        if (closedTabs.TryPop(out string? url) && await OpenTab(url) is null) closedTabs.Push(url);
    }

    private void BackClick(object sender, RoutedEventArgs args) { if (Back.IsEnabled) Active?.View.GoBack(); }
    private void ForwardClick(object sender, RoutedEventArgs args) { if (Forward.IsEnabled) Active?.View.GoForward(); }
    private void ReloadClick(object sender, RoutedEventArgs args) { if (Reload.IsEnabled) Active?.View.Reload(); }

    private void MenuClick(object sender, RoutedEventArgs args)
    {
        var menu = ((Button)sender).ContextMenu;
        menu.PlacementTarget = (Button)sender;
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
        }
        else Status.Text = "이미 북마크에 있습니다.";
    }

    private async void BookmarksClick(object sender, RoutedEventArgs args) => await ShowLibrary("북마크", data.Bookmarks, true);
    private async void HistoryClick(object sender, RoutedEventArgs args) => await ShowLibrary("방문 기록", data.History, false);

    private async Task ShowLibrary(string title, List<PageEntry> entries, bool edit)
    {
        var dialog = new LibraryWindow(this, title, entries, edit, SaveData);
        if (dialog.ShowDialog() == true && IsWebUrl(dialog.SelectedUrl))
            await OpenTab(dialog.SelectedUrl);
    }

    private void DownloadsClick(object sender, RoutedEventArgs args)
    {
        var dialog = new Window { Owner = this, Title = "다운로드", Width = 500, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var text = new TextBlock { Text = "금고 검증 전까지 파일 다운로드를 취소합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(text, Dock.Top);
        panel.Children.Add(text);
        panel.Children.Add(new ListBox { ItemsSource = downloads.ToArray() });
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void FindClick(object sender, RoutedEventArgs args)
    {
        FindBar.Visibility = Visibility.Visible;
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
    private void FindCloseClick(object sender, RoutedEventArgs args) => FindBar.Visibility = Visibility.Collapsed;

    private async Task Find(bool backwards)
    {
        if (Active is not { } tab || FindText.Text.Length == 0) return;
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
