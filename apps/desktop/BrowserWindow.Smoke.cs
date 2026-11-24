using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    internal void ConfigureSmoke(string origin, string stage, string report)
    {
        ShowActivated = false;
        ShowInTaskbar = false;
        smokeRun = true;
        Loaded += async (_, _) =>
        {
            var checks = new List<string>();
            string? error = null;
            try
            {
                var uri = new Uri(origin);
                Check(uri.Scheme == "http" && uri.Host == "127.0.0.1", "loopback fixture");
                Check(string.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                    Path.Combine(session.Root, ProfileScratch.FolderName), StringComparison.OrdinalIgnoreCase), "temp path inside profile");
                await WaitFor(() => !restoring, "desktop initialization");
                Check(ExternalAddress.Parse("https://example.test/a?b=1") == "https://example.test/a?b=1" &&
                    ExternalAddress.Parse("veil://passwords") is null &&
                    ExternalAddress.Parse("file:///C:/secret") is null &&
                    ExternalAddress.Parse("https://user@example.test") is null &&
                    ExternalAddress.Parse("https://example.test/\" --smoke") is null &&
                    ExternalAddress.Parse("example.test") is null, "external activation accepts only well-formed web URLs");
                Check(ResolveAddress("example.test")?.AbsoluteUri == "https://example.test/", "HTTPS address default");
                Check(ResolveAddress("한글 브라우저")?.Host == "duckduckgo.com", "search encoding");
                Check(ResolveAddress("한글 & 검색", "google")?.Host == "www.google.com" &&
                    ResolveAddress("한글 & 검색", "google")?.Query.Contains("%26") == true &&
                    ResolveAddress("한글 검색", "bing")?.Host == "www.bing.com" &&
                    ResolveAddress("한글 검색", "naver")?.Host == "search.naver.com" &&
                    ResolveAddress("한글 검색", "invalid")?.Host == "duckduckgo.com",
                    "fixed HTTPS search providers and fallback");
                var localData = new BrowserData();
                localData.Bookmarks.Add(new PageEntry("Synthetic", "https://example.test/", DateTimeOffset.Now));
                localData.History.Add(new PageEntry("Synthetic duplicate", "https://example.test/", DateTimeOffset.Now));
                localData.History.Add(new PageEntry("Synthetic token", "https://example.test/?token=synthetic", DateTimeOffset.Now));
                localData.History.Add(new PageEntry("Synthetic fragment", "https://example.test/#secret", DateTimeOffset.Now));
                localData.History.Add(new PageEntry("Synthetic invalid", "file:///C:/synthetic", DateTimeOffset.Now));
                Check(SearchPolicy.Suggest(localData, "synthetic").Count == 1 &&
                    SearchPolicy.Suggest(localData, "synthetic")[0].Url == "https://example.test/",
                    "local suggestions deduplicate and exclude sensitive URL forms");
                localData.Bookmarks.Add(new PageEntry("CSharp Reference", "https://www.docs.example.test/guide", DateTimeOffset.Now));
                Check(SearchPolicy.Suggest(localData, "docs.example.test").FirstOrDefault()?.SourceLabel == "북마크" &&
                    SearchPolicy.Suggest(localData, "reference guide").Any(item => item.Url.EndsWith("/guide")),
                    "local suggestions support www-free host and multiple terms");
                Check(SearchPolicy.Suggest(localData, "? synthetic").Count == 0 &&
                    SearchPolicy.Suggest(localData, "@google synthetic").Count == 0 &&
                    ResolveAddress("? example.test", "bing")?.Host == "www.bing.com" &&
                    ResolveAddress("@naver 한글 검색", "google")?.Host == "search.naver.com" &&
                    ResolveAddress("@google") is null, "explicit search selects provider only at submission");
                localData.Bookmarks.RemoveAt(1);
                localData.LocalAddressSuggestions = false;
                Check(SearchPolicy.Suggest(localData, "synthetic").Count == 0, "local suggestions disabled");
                var parentFolder = new BookmarkFolder(Guid.NewGuid(), "Parent");
                var childFolder = new BookmarkFolder(Guid.NewGuid(), "Child", parentFolder.Id);
                localData.BookmarkFolders.AddRange([parentFolder, childFolder]);
                localData.Bookmarks[0] = localData.Bookmarks[0] with { FolderId = childFolder.Id };
                Check(BookmarkPolicy.Valid(localData.BookmarkFolders, localData.Bookmarks), "nested bookmark hierarchy valid");
                Check(!BookmarkPolicy.MoveFolder(localData.BookmarkFolders, localData.Bookmarks, parentFolder.Id, childFolder.Id),
                    "bookmark folder cycle rejected");
                Check(BookmarkPolicy.RemoveFolder(localData.BookmarkFolders, localData.Bookmarks, childFolder.Id) &&
                    localData.Bookmarks[0].FolderId == parentFolder.Id, "deleting folder preserves bookmarks");
                Check(BookmarkPolicy.RemoveFolder(localData.BookmarkFolders, localData.Bookmarks, parentFolder.Id) &&
                    localData.Bookmarks[0].FolderId is null && localData.Bookmarks.Count == 1,
                    "deleting last folder promotes bookmarks to root");
                const string bookmarksHtml = """
                    <!DOCTYPE NETSCAPE-Bookmark-file-1>
                    <DL><p><DT><H3>Study &amp; Code</H3><DL><p>
                    <DT><A HREF="https://example.test/docs?a=1&amp;b=2">Docs &lt;3</A>
                    <DT><A HREF="javascript:alert(1)">Unsafe</A>
                    <DT><A HREF="https://example.test/docs?a=1&amp;b=2">Duplicate</A>
                    </DL><p></DL><p>
                    """;
                var importedBookmarks = BookmarkHtml.Parse(bookmarksHtml, []);
                Check(importedBookmarks is { Skipped: 2, Folders.Count: 1, Entries.Count: 1 } &&
                    importedBookmarks.Entries[0].FolderId == importedBookmarks.Folders[0].Id &&
                    importedBookmarks.Entries[0].Title == "Docs <3", "bookmark HTML imports folders, entities and skips unsafe or duplicate URLs");
                var roundTrip = BookmarkHtml.Parse(System.Text.Encoding.UTF8.GetString(
                    BookmarkHtml.Export(importedBookmarks.Folders, importedBookmarks.Entries)), []);
                Check(roundTrip.Folders[0].Name == "Study & Code" && roundTrip.Entries[0].Url == importedBookmarks.Entries[0].Url &&
                    roundTrip.Entries[0].FolderId == roundTrip.Folders[0].Id, "bookmark HTML round trip preserves hierarchy and URL");
                Check(LoginFormPolicy.Decode("https://example.test", """{"UserName":"synthetic","Password":"synthetic-pass"}""")?.Origin == "https://example.test" &&
                    LoginFormPolicy.Decode("http://example.test", """{"UserName":"synthetic","Password":"synthetic-pass"}""") is null &&
                    LoginFormPolicy.Decode("https://example.test", """{"UserName":"","Password":"synthetic-pass"}""") is null,
                    "login capture rejects invalid origin and empty username");
                Check(LoginFormPolicy.Probe("https://example.test").Contains("return true;") &&
                    !LoginFormPolicy.Probe("https://example.test").Contains("pass.value") &&
                    !LoginFormPolicy.Fill(new SavedLogin("https://example.test", "synthetic", "synthetic-pass")).Contains(".submit("),
                    "login probe reads no password and fill does not submit");
                var savedAccount = new SavedLogin("https://example.test", "synthetic", "original");
                var mergedAccounts = PasswordStore.MergeBackup([savedAccount],
                    [savedAccount with { Password = "different" }, new SavedLogin("https://example.test", "second", "synthetic")]);
                Check(mergedAccounts.Count == 2 && mergedAccounts[0] == savedAccount && mergedAccounts[1].UserName == "second",
                    "credential backup merge preserves existing account on conflict");
                var rule = new SitePermissionRule("https://example.test", "camera", SitePermissionPolicy.Ask);
                Check(SitePermissionPolicy.ValidRules([rule]) &&
                    !SitePermissionPolicy.ValidRules([rule, rule]) &&
                    !SitePermissionPolicy.ValidRules([rule with { Mode = "unknown" }]) &&
                    SitePermissionPolicy.ModeFor([rule], "https://example.test:8443", "camera") == SitePermissionPolicy.Block &&
                    SitePermissionPolicy.ModeFor([rule], "https://example.test", "microphone") == SitePermissionPolicy.Block &&
                    SitePermissionPolicy.Origin("http://example.test") is null, "permission policy defaults and exact scope");
                var unusedEngine = new WebView2BrowserEngine();
                Check(!unusedEngine.IsReady && unusedEngine.RuntimeVersion is null, "uninitialized engine state");
                bool refusedView = false;
                try { using var premature = unusedEngine.CreateView(); }
                catch (InvalidOperationException) { refusedView = true; }
                Check(refusedView, "engine refuses view before verified initialization");
                var previewEntry = new VaultEntry(Guid.NewGuid().ToString("N"), "synthetic.txt", 12, DateTimeOffset.Now, "example.test");
                Check(VaultPresentation.CanPreview(previewEntry) &&
                    !VaultPresentation.CanPreview(previewEntry with { Name = "synthetic.html" }) &&
                    !VaultPresentation.CanPreview(previewEntry with { Bytes = VaultPresentation.PreviewLimit + 1 }) &&
                    !VaultPresentation.CanPreview(previewEntry with { Recovered = true }), "text preview allowlist");
                Check(VaultPresentation.PreviewKind(previewEntry with { Name = "synthetic.PNG" }) == VaultPreviewKind.Png &&
                    VaultPresentation.PreviewKind(previewEntry with { Name = "synthetic.jpg" }) == VaultPreviewKind.Jpeg &&
                    !VaultPresentation.CanPreview(previewEntry with { Name = "synthetic.svg" }) &&
                    !VaultPresentation.CanPreview(previewEntry with { Name = "synthetic.png", Bytes = VaultPresentation.ImagePreviewLimit + 1 }),
                    "image preview format and byte limits");
                byte[] tinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR1sAAAAASUVORK5CYII=");
                Check(VaultImagePreview.Dimensions(tinyPng, VaultPreviewKind.Png) == (1, 1), "PNG header size");
                var oversizedPng = tinyPng.ToArray();
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(oversizedPng.AsSpan(16), 100000);
                bool rejectedImage = false;
                try { VaultImagePreview.Dimensions(oversizedPng, VaultPreviewKind.Png); }
                catch (InvalidDataException) { rejectedImage = true; }
                Check(rejectedImage, "oversized PNG rejected before decoder");
                Check(VaultPdfPreview.ValidHeader("%PDF-1.7\nsynthetic"u8) &&
                    !VaultPdfPreview.ValidHeader("<html>synthetic</html>"u8) &&
                    VaultPresentation.CanPreview(previewEntry with { Name = "synthetic.pdf" }) == VaultPdfPreview.Enabled,
                    "PDF preview header and development gate");
                Check(VaultPdfPreview.RenderSize(612, 792) is (1391, 1800) &&
                    VaultPdfPreview.RenderSize(double.NaN, 100) is null &&
                    VaultPdfPreview.RenderSize(100000, 1) is null, "PDF render dimension limits");
                Check(DownloadFailurePolicy.HttpStatus(System.Net.HttpStatusCode.ServiceUnavailable, true).Retryable &&
                    !DownloadFailurePolicy.HttpStatus(System.Net.HttpStatusCode.Forbidden, true).Retryable &&
                    !DownloadFailurePolicy.HttpStatus(System.Net.HttpStatusCode.ServiceUnavailable, false).Retryable &&
                    DownloadFailurePolicy.SafeRetryAddress(new Uri("https://example.test/file.bin")) &&
                    !DownloadFailurePolicy.SafeRetryAddress(new Uri("https://example.test/file.bin?token=synthetic")) &&
                    !DownloadFailurePolicy.SafeRetryAddress(new Uri("http://example.test/file.bin")),
                    "download retry only permits initial transient failures and restricted HTTPS addresses");
                Check(!DownloadFailurePolicy.Describe(new IOException("synthetic-secret"), DownloadPhase.Streaming, true)
                    .Contains("synthetic-secret"), "download failure detail does not expose raw exceptions");
                Check(InlineDownloadPolicy.Decode("data:application/octet-stream;base64,c3ludGhldGlj").SequenceEqual("synthetic"u8.ToArray()) &&
                    InlineDownloadPolicy.Decode("data:text/plain;charset=utf-8,a%20b+%00").SequenceEqual(new byte[] { 97, 32, 98, 43, 0 }) &&
                    InlineDownloadPolicy.SourceAllowed(new Uri("https://example.test/")) &&
                    !InlineDownloadPolicy.SourceAllowed(new Uri("http://example.test/")), "bounded inline data decode and source policy");
                foreach (string invalidData in new[] { "data:text/plain,%GG", "data:;base64,YQ==#fragment",
                    "data:;base64, YQ=", "data:;unknown=1,x", "data:," + new string('a', InlineDownloadPolicy.MaxBytes + 1) })
                {
                    bool refusedData = false;
                    try { InlineDownloadPolicy.Decode(invalidData); }
                    catch (InvalidDataException) { refusedData = true; }
                    Check(refusedData, "invalid inline data rejected");
                }
                using (var queued = new BrowserDownload("synthetic-queued.bin"))
                {
                    queued.WaitForSlot();
                    Check(queued.Phase == DownloadPhase.Waiting && queued.Bytes == 0 && queued.CanCancel,
                        "queued transfer waits before opening body");
                    queued.Cancel();
                    queued.WaitForTransfer();
                    Check(queued.Token.IsCancellationRequested && queued.Phase == DownloadPhase.Waiting,
                        "cancelled queue cannot advance");
                }
                var overviewIssue = new DownloadIssue(Guid.NewGuid(), "synthetic-failed.bin", DownloadVault.Failed, "DNS", DateTimeOffset.Now);
                Check(DownloadOverviewPolicy.Filter([previewEntry], [overviewIssue], "dns", DownloadFilter.Failed, VaultSort.Recent).Single().Outcome == DownloadVault.Failed &&
                    DownloadOverviewPolicy.Filter([], [overviewIssue], "", DownloadFilter.Stored, VaultSort.Recent).Count == 0 &&
                    DownloadOverviewPolicy.Filter([previewEntry], [overviewIssue], "", DownloadFilter.All, VaultSort.Smallest)[0].Bytes == previewEntry.Bytes,
                    "download overview search, locked storage omission and known-size sort");
                Check(VaultPresentation.DecodeText("synthetic"u8.ToArray()) == "synthetic" &&
                    VaultPresentation.ExportName(previewEntry with { Name = "CON.txt" }) == "VEIL-download.bin" &&
                    VaultPresentation.Filter([previewEntry], "example", VaultSort.Recent, false).Count == 1,
                    "vault presentation and filename policy");
                var sites = SiteDataPolicy.Origins(["https://example.test/path?secret=test", "https://example.test:443/",
                    "https://example.test:8443/", "http://example.test/", "file:///C:/test", "https://user@example.test/"]);
                Check(sites.Count == 3 && sites.Contains("https://example.test") &&
                    sites.Contains("https://example.test:8443") && sites.Contains("http://example.test"),
                    "site data origins isolate schemes and ports");
                var domains = SiteDataPolicy.Summarize(["example.test", ".example.test", "sub.example.test", "EXAMPLE.TEST"]);
                Check(domains.Count == 3 && domains.Single(item => item.Domain == "example.test").Count == 2 &&
                    !SiteDataPolicy.SameCookieDomain(".example.test", "example.test") &&
                    !SiteDataPolicy.SameCookieDomain("sub.example.test", "example.test"),
                    "cookie storage domains never use suffix matching");
                Check(ResolveAddress("127.0.0.1:8765")?.AbsoluteUri == "http://127.0.0.1:8765/", "fixture address");
                Check(ResolveAddress("file:///C:/secret") is null &&
                    ResolveAddress("https://username@example.test") is null, "address scheme and credentials rejected");
                foreach (var field in new[] { Address, FindText })
                {
                    // Reading the brush throws if the theme assigns a Color where a Brush is required.
                    Check(field.SelectionBrush is System.Windows.Media.SolidColorBrush, $"{field.Name} selection brush");
                    field.Text = "selection check";
                    field.Focus();
                    field.SelectAll();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    Check(field.SelectionLength == field.Text.Length, $"{field.Name} text selection renders");
                    field.Text = "";
                }

                if (stage == "write")
                {
                    VaultSetupDialog.VerifySetupWizard(this);
                    checks.Add("vault setup wizard rejects invalid credentials and completes all three steps");
                    NavigateAddress(origin);
                    await ReadyAt("/");
                    BookmarkClick(this, new RoutedEventArgs());
                    Check(data.Bookmarks.Count == 1, "bookmark created");
                    Check(BookmarkBar.Visibility == System.Windows.Visibility.Visible && BookmarkBarItems.Items.Count == 1,
                        "bookmark bar reflects saved bookmark");
                    ToggleBookmarkBarClick(this, new RoutedEventArgs());
                    Check(!data.ShowBookmarksBar && BookmarkBar.Visibility == System.Windows.Visibility.Collapsed,
                        "bookmark bar can be hidden");
                    ToggleBookmarkBarClick(this, new RoutedEventArgs());
                    Check(data.ShowBookmarksBar && BookmarkBar.Visibility == System.Windows.Visibility.Visible,
                        "bookmark bar can be shown");
                    using (var first = new BrowserDownload("synthetic-first"))
                    using (var second = new BrowserDownload("synthetic-second"))
                    {
                        first.Report(50, 100);
                        second.Report(100, 300);
                        var progress = AggregateDownloadProgress(new[] { first, second });
                        Check(progress is { Indeterminate: false } && Math.Abs(progress.Fraction - 0.375) < 0.001,
                            "download ring uses byte-weighted progress");
                        second.Report(0, null);
                        Check(AggregateDownloadProgress(new[] { first, second }).Indeterminate,
                            "unknown download length produces indeterminate ring");
                    }
                    // Restored management tabs have a native surface, not a privileged web page.
                    Check(IsManagementUrl("veil://settings") && IsManagementUrl("veil://vault") && IsManagementUrl("veil://passwords") &&
                        !IsManagementUrl("veil://settings/unknown"), "management route allowlist");
                    BookmarksClick(this, new RoutedEventArgs());
                    await WaitFor(() => Active?.Url == "veil://bookmarks" && Active.CancelManagement is not null, "bookmark manager opened in native tab");
                    Check(Active?.Title == "북마크", "bookmark library tab title");
                    Check(Active?.ManagementSurface is not null && Active.View.Visibility == System.Windows.Visibility.Hidden,
                        "bookmark management uses native tab surface");
                    CloseTab((TabItem)Tabs.SelectedItem);
                    await ReadyAt("/");
                    HistoryClick(this, new RoutedEventArgs());
                    await WaitFor(() => Active?.Url == "veil://history" && Active.CancelManagement is not null, "history manager opened in native tab");
                    Check(Active?.Title == "방문 기록", "history tab title");
                    CloseTab((TabItem)Tabs.SelectedItem);
                    await ReadyAt("/");
                    SettingsClick(this, new RoutedEventArgs());
                    await WaitFor(() => Active?.Url == "veil://settings" && Active.CancelManagement is not null, "settings opened in native tab");
                    Check(BrowserData.Load(statePath).Tabs.Contains("veil://settings"),
                        "selected native route persisted without WebView navigation");
                    var closingSettings = Active!;
                    var settingsClose = CloseTabAsync(closingSettings.Item);
                    Check(closingSettings.CancelManagement is null && closingSettings.ManagementSurface is null,
                        "closing native manager cancels its action before animation completes");
                    await settingsClose;
                    await ReadyAt("/");
                    await WaitFor(() => !session.SettingsUiOpen && !session.PermissionUiOpen, "closing settings releases manager guards");
                    var selectedBeforeBackground = Active;
                    var backgroundSettings = await OpenTab("veil://settings", select: false)
                        ?? throw new InvalidOperationException("background settings tab did not open");
                    Check(Active == selectedBeforeBackground && backgroundSettings.ManagementSurface is not null &&
                        backgroundSettings.CancelManagement is null &&
                        backgroundSettings.ManagementSurface.Visibility == Visibility.Collapsed &&
                        backgroundSettings.View.Visibility == Visibility.Hidden &&
                        BrowserData.Load(statePath).Tabs.Contains("veil://settings"),
                        "background native tab persisted without selecting or opening privileged controls");
                    await CloseTabAsync(backgroundSettings.Item);
                    Check(!BrowserData.Load(statePath).Tabs.Contains("veil://settings"), "closed background native route removed from state");
                    NavigateAddress(origin + "/second");
                    await ReadyAt("/second");
                    await WaitFor(() => Back.IsEnabled, "back button");
                    BackClick(this, new RoutedEventArgs());
                    await ReadyAt("/");
                    await WaitFor(() => Forward.IsEnabled, "forward button");
                    ForwardClick(this, new RoutedEventArgs());
                    await ReadyAt("/second");
                    ReloadClick(this, new RoutedEventArgs());
                    await ReadyAt("/second");
                    var additional = await OpenTab(origin);
                    Check(additional is not null && Tabs.Items.Count == 2, "new tab");
                    await ReadyAt("/");
                    var web = Active!.View.CoreWebView2;
                    await web.ExecuteScriptAsync("probe('write').then(result => window.smoke = Object.values(result).every(Boolean)); null;");
                    await WaitForScript("window.smoke === true", "site data seeded");

                    await Download("location.href = '/download?kind=get'", "get");
                    await Download("location.href = '/download?kind=auth'", "auth");
                    await Download("location.href = '/redirect'", "redirect");
                    await Download("document.querySelector('form').submit()", "post");
                    await Download("let a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['synthetic'])); a.download = 'veil-phase0-blob.bin'; a.click()", "blob");
                    await Download("let b = document.createElement('a'); b.href = 'data:application/octet-stream,synthetic'; b.download = 'veil-phase0-data.bin'; b.click()", "data");
                    await Download("location.href = '/sw-file'", "sw");
                    await Download("location.href = '/download?kind=large'", "large");

                    await web.ExecuteScriptAsync("window.open('/second','_blank'); null");
                    await WaitFor(() => Tabs.Items.Count == 3, "site popup opens in tab");
                    var popup = (TabItem)Tabs.Items[2];
                    await WaitFor(() => ((BrowserTab)popup.Tag).Url.EndsWith("/second"), "popup navigation");
                    CloseTab(popup);
                    await WaitFor(() => Tabs.Items.Count == 2, "tab closed");
                    RestoreClick(this, new RoutedEventArgs());
                    await ReadyAt("/second");
                    Check(Tabs.Items.Count == 3, "closed tab restored");
                    SetTabPinned((TabItem)Tabs.Items[0], true);
                    Check(PinnedTabCount == 1 && ((BrowserTab)((TabItem)Tabs.Items[0]).Tag).Pinned, "tab pinned");
                    Tabs.SelectedIndex = 1;
                    MoveSelectedTab(-1);
                    Check(Tabs.SelectedIndex == 1, "normal tab cannot cross pinned boundary");
                    SaveTabs();
                    Check(BrowserData.Load(statePath).PinnedCount == 1, "pinned count persisted");
                }
                else if (stage is "read" or "crash")
                {
                    Check(Tabs.Items.Count == 3 && Tabs.SelectedIndex == 1, "tabs and selection restored");
                    Check(PinnedTabCount == 1, "pinned tab restored");
                    // Only the normal read stage needs unpinned tabs for its existing reorder checks.
                    // The interrupted-write stage must preserve the pin for the next read process.
                    if (stage == "read") SetTabPinned((TabItem)Tabs.Items[0], false);
                    await ReadyAt("/");
                    Check(data.Bookmarks.Count == 1 && data.Bookmarks[0].Url == origin + "/", "bookmark restored");
                    Check(data.ShowBookmarksBar && BookmarkBarItems.Items.Count == 1, "bookmark bar restored");
                    Check(data.History.Any(entry => entry.Url.EndsWith("/second")), "history restored");
                    await Active!.View.ExecuteScriptAsync("probe('read').then(result => window.smoke = Object.values(result).every(Boolean)); null;");
                    await WaitForScript("window.smoke === true", "five site storage types restored");
                    if (stage == "read")
                    {
                        // Private windows are separate from the persisted regular session.
                        var regularState = File.ReadAllBytes(statePath);
                        int regularHistory = data.History.Count;
                        var privateWindow = OpenPrivateWindow();
                        try
                        {
                            await WaitFor(() => !privateWindow.IsRestoring && privateWindow.Active is not null, "private window initialized");
                            var privateTab = privateWindow.Active!;
                            var profile = privateTab.View.CoreWebView2.Profile;
                            Check(profile.IsInPrivateModeEnabled && profile.ProfileName == privateWindow.session.PrivateProfileName &&
                                !Active!.View.CoreWebView2.Profile.IsInPrivateModeEnabled, "private engine profile verified");
                            Check(privateWindow.data.Bookmarks.Count == 0 && !privateWindow.SettingsReady() &&
                                !privateWindow.PasswordWindowReady() && privateWindow.data.LocalAddressSuggestions == false,
                                "private persistent managers unavailable");
                            privateWindow.NavigateAddress(origin + "/second");
                            await WaitFor(() => privateTab.Url == origin + "/second" && !privateTab.Loading &&
                                privateTab.LastNavigationError is null, "private navigation");
                            Check((await privateTab.View.CoreWebView2.CookieManager.GetCookiesAsync(origin)).Count == 0,
                                "regular fixture cookies not visible in private profile");
                            await privateTab.View.ExecuteScriptAsync("localStorage.setItem('veil-private-marker','synthetic'); null;");
                            Check(await Active!.View.ExecuteScriptAsync("localStorage.getItem('veil-private-marker')") == "null",
                                "private local storage isolated");
                            Check(!privateWindow.CanMoveTo(privateTab.Item, this) &&
                                !CanMoveTo(Active!.Item, privateWindow), "cross-session tab transfer refused");
                            Check(data.History.Count == regularHistory && privateWindow.data.History.Count == 0 &&
                                File.ReadAllBytes(statePath).SequenceEqual(regularState), "private navigation not persisted");
                            Check(await privateWindow.OpenTab("veil://downloads") is null, "private vault route rejected");
                        }
                        finally { privateWindow.Close(); }
                        Check(!session.AllWindows.Contains(privateWindow), "private window released");
                        // Tabs move between windows without reloading; the second window is persisted.
                        var moving = Active!;
                        await moving.View.ExecuteScriptAsync("window.veilMarker = 42; null");
                        int count = Tabs.Items.Count;
                        var other = MoveTabToNewWindow(moving.Item);
                        await WaitFor(() => moving.Owner == other && !other.IsRestoring &&
                            other.Tabs.Items.Contains(moving.Item) && Tabs.Items.Count == count - 1, "tab moved to a new window");
                        Check(await moving.View.ExecuteScriptAsync("window.veilMarker") == "42", "moved tab kept its live page");
                        other.ToggleBookmarkBarClick(this, new RoutedEventArgs());
                        Check(!data.ShowBookmarksBar && BookmarkBar.Visibility == Visibility.Collapsed &&
                            other.BookmarkBar.Visibility == Visibility.Collapsed && !BrowserData.Load(statePath).ShowBookmarksBar,
                            "bookmark bar hidden across windows and persisted");
                        ToggleBookmarkBarClick(this, new RoutedEventArgs());
                        Check(data.ShowBookmarksBar && BookmarkBar.Visibility == Visibility.Visible &&
                            other.BookmarkBar.Visibility == Visibility.Visible && BrowserData.Load(statePath).ShowBookmarksBar,
                            "bookmark bar restored across windows");
                        SaveSession();
                        Check(BrowserData.Load(statePath).Windows is [{ Tabs.Count: 1 }], "second window persisted");
                        Check(other.CanMoveTo(moving.Item, this) && other.MoveToExistingWindow(moving.Item, this, 0),
                            "move existing tab to another regular window");
                        await WaitFor(() => !session.Windows.Contains(other) && Tabs.Items.IndexOf(moving.Item) == 0 &&
                            Active == moving && Tabs.Items.Count == count, "tab merged back at the front");
                        Check(await moving.View.ExecuteScriptAsync("window.veilMarker") == "42", "merged tab kept its live page");
                        MoveSelectedTab(1);
                        Check(Tabs.Items.IndexOf(moving.Item) == 1 && Active == moving, "keyboard tab reorder");
                        Check(BrowserData.Load(statePath).Windows is null, "merged window removed from state");
                        var selectedBefore = Tabs.SelectedItem;
                        SelectTab(Tabs.Items.Count + 3);
                        Check(Tabs.SelectedItem == selectedBefore, "out of range numbered shortcut leaves selection unchanged");
                        CycleTab(1);
                        CycleTab(-1);
                        Check(Tabs.SelectedItem == selectedBefore, "tab cycling wraps and preserves order");
                        // Tear-off path used by dragging a tab out of the strip (without real mouse input).
                        var torn = (TabItem)Tabs.Items[^1];
                        int windowCount = session.Windows.Count;
                        TearOff(torn);
                        await WaitFor(() => session.Windows.Count == windowCount + 1 && Tabs.Items.Count == count - 1 &&
                            session.Windows[^1].Tabs.Items.Contains(torn) && !session.Windows[^1].IsRestoring, "tab torn off into a new window");
                        var tornWindow = session.Windows[^1];
                        tornWindow.DetachTab(torn);
                        AttachTab(torn, Tabs.Items.Count);
                        await WaitFor(() => session.Windows.Count == windowCount && Tabs.Items.Count == count, "torn-off tab dropped back");
                    }
                    if (stage == "crash")
                    {
                        using var temporary = new FileStream(statePath + ".new", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        temporary.Write("{\"Bookmarks\":"u8);
                        temporary.Flush(flushToDisk: true);
                        File.WriteAllText(report, JsonSerializer.Serialize(new
                        {
                            browserPid = Active.View.CoreWebView2.BrowserProcessId,
                            interruptedStateReady = true
                        }));
                        // The harness terminates this process with the synthetic partial state still open.
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                    }
                }
                else if (stage == "browse")
                {
                    // SEC-004 long session: distinct pages, repeat visits and closed tabs give the engine's
                    // jump-list updater "frequent" and "recently closed" material, then time to write it.
                    for (int i = 0; i < 30; i++)
                    {
                        NavigateAddress($"{origin}/visit?n={i}");
                        await ReadyAt($"/visit?n={i}");
                    }
                    for (int round = 0; round < 5; round++)
                        foreach (int i in new[] { 1, 2, 3 })
                        {
                            NavigateAddress($"{origin}/visit?n={i}");
                            await ReadyAt($"/visit?n={i}");
                        }
                    for (int i = 100; i < 108; i++)
                    {
                        int count = Tabs.Items.Count;
                        Check(await OpenTab($"{origin}/visit?n={i}") is not null, $"browse tab {i}");
                        await ReadyAt($"/visit?n={i}");
                        CloseTab((TabItem)Tabs.SelectedItem);
                        await WaitFor(() => Tabs.Items.Count == count, $"browse tab {i} closed");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    Check(data.History.Count >= 53, "long browse session recorded");
                }
                else if (stage == "vault")
                {
                    // Phase 4-0: every supported download lands only in the encrypted vault; failures and
                    // unsupported requests leave nothing behind.
                    Check(session.Vault.Available, "vault bridge present");
                    var plan = new (string Kind, string Expected, long Bytes, bool Login)[]
                    {
                        // (A 401 without the login cookie is an error page, not a download: nothing to capture.)
                        ("slow", DownloadVault.Cancelled, 0, true),
                        ("slow-unknown", DownloadVault.Cancelled, 0, true),
                        ("small", DownloadVault.Stored, 1024, true),
                        ("large", DownloadVault.Stored, 8L * 1024 * 1024, true),
                        ("huge", DownloadVault.Stored, 64L * 1024 * 1024, true),
                        ("auth", DownloadVault.Stored, 4096, true),
                        ("redirect", DownloadVault.Stored, 2048, true),
                        ("drop", DownloadVault.Failed, 0, true),
                        ("post", DownloadVault.Blocked, 0, true),
                    };
                    bool loggedIn = false;
                    foreach (var (kind, expected, bytes, login) in plan)
                    {
                        if (login && !loggedIn)
                        {
                            NavigateAddress(origin + "/login");
                            await ReadyAt("/login");
                            loggedIn = true;
                        }
                        var original = Tabs.SelectedItem;
                        Check(await OpenTab(origin) is not null, $"vault tab ({kind})");
                        await ReadyAt("/");
                        var requestTab = (TabItem)Tabs.SelectedItem;
                        int count = session.Vault.Results.Count;
                        string trigger = kind == "post"
                            ? "let f = document.createElement('form'); f.method = 'post'; f.action = '/vault-dl?kind=post'; document.body.append(f); f.submit()"
                            : $"location.href = '/vault-dl?kind={kind}'";
                        await Active!.View.ExecuteScriptAsync(trigger + "; null");
                        if (expected == DownloadVault.Cancelled)
                        {
                            await WaitFor(() => session.ActiveDownloads.Any(item => item.Bytes >= 64 * 1024),
                                $"some bytes received ({kind})");
                            TabItem? queuedTab = null;
                            if (kind == "slow")
                            {
                                queuedTab = (await OpenTab(origin))!.Item;
                                await ReadyAt("/");
                                await Active!.View.ExecuteScriptAsync("location.href = '/vault-dl?kind=slow-queued'; null");
                                await WaitFor(() => session.ActiveDownloads.Count == 2, "two active downloads shown");
                            }
                            DownloadsClick(DownloadButton, new RoutedEventArgs());
                            await WaitFor(() => DownloadProgressPanel.IsVisible && FindProgressElement<Button>(ActiveDownloadsList) is not null,
                                $"Ctrl+J download progress and cancel button ({kind})");
                            if (queuedTab is not null)
                            {
                                Button? queued = FindProgressElement<Button>(ActiveDownloadsList, button =>
                                    button.DataContext is BrowserDownload { Name: "veil-vault-slow-queued.bin" });
                                Check(queued?.DataContext is BrowserDownload { Bytes: 0 }, "second download waits for the vault");
                                queued!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                await WaitFor(() => session.Vault.Results.Count == count + 1, "queued download cancelled");
                                var cancelled = session.Vault.Results[^1];
                                Check(cancelled is { Name: "veil-vault-slow-queued.bin", Outcome: DownloadVault.Cancelled, Bytes: 0, ObjectPath: null } &&
                                    session.ActiveDownloads.Single().CanCancel, "queued cancellation leaves first download running");
                                count++;
                                CloseTab(queuedTab);
                            }
                            var meter = FindProgressElement<ProgressBar>(ActiveDownloadsList);
                            Check(meter is not null && meter.IsIndeterminate == (kind == "slow-unknown") &&
                                (meter.IsIndeterminate || meter.Value is > 0 and < 100), $"correct progress meter ({kind})");
                            var cancelButton = FindProgressElement<Button>(ActiveDownloadsList)!;
                            Check(cancelButton.IsEnabled, $"download cancel enabled ({kind})");
                            cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            await WaitFor(() => session.Vault.Results.Count == count + 1 &&
                                session.ActiveDownloads.Count == 0 && ActiveDownloadsList.Visibility == Visibility.Collapsed &&
                                DownloadProgressPanel.IsVisible,
                                $"cancelled transfer clears active list but keeps download manager visible ({kind})");
                            CloseTab((TabItem)Tabs.SelectedItem);
                        }
                        for (int i = 0; i < 1600 && session.Vault.Results.Count == count; i++) await Task.Delay(75);
                        Check(session.Vault.Results.Count == count + 1, $"vault outcome ({kind})");
                        var result = session.Vault.Results[^1];
                        Check(result.Outcome == expected, $"vault {kind}: {result.Outcome} {result.Detail} (expected {expected})");
                        if (expected == DownloadVault.Stored)
                        {
                            Check(result.Bytes == bytes, $"vault {kind} size {result.Bytes}");
                            var entry = session.Vault.Entries.First(e => session.Vault.ObjectPath(e) == result.ObjectPath);
                            Check(await session.Vault.VerifyAsync(entry) == bytes, $"vault {kind} authenticates");
                        }
                        else if (expected == DownloadVault.Cancelled)
                        {
                            Check(result.Bytes > 0 && result.ObjectPath is null &&
                                session.Vault.Entries.All(entry => entry.Name != result.Name) &&
                                downloads[0].EndsWith(DownloadVault.Cancelled), $"cancelled download not published ({kind})");
                        }
                        checks.Add($"engine bytes before cancel ({kind}): received {result.EngineBytesBeforeCancel}, scratch {result.EngineBytesOnDisk}");
                        CloseTab(requestTab);
                        Tabs.SelectedItem = original;
                    }
                    File.WriteAllText(Path.ChangeExtension(report, ".vault.json"), JsonSerializer.Serialize(new
                    {
                        vault = session.Vault.Directory,
                        results = session.Vault.Results,
                    }));
                }
                else if (stage == "vault-reopen")
                {
                    // ADR-0005: a new VEIL process opens the persistent vault (passphrase, or the recovery code
                    // in VEIL_TEST_RECOVER) and exports every item into the evidence folder.
                    var vault = session.Vault;
                    string? recover = Environment.GetEnvironmentVariable("VEIL_TEST_RECOVER");
                    bool opened = recover is { Length: > 0 }
                        ? await vault.RecoverAsync(recover) is null
                        : await vault.TryOpenFromDevelopmentEnvironmentAsync();
                    checks.Add($"vault opened: {opened}");
                    var exports = new List<object>();
                    if (opened)
                    {
                        string target = Path.Combine(Path.GetDirectoryName(report)!, "exports-" + Guid.NewGuid().ToString("N")[..6]);
                        Directory.CreateDirectory(target);
                        foreach (var item in vault.Entries)
                        {
                            string destination = Path.Combine(target, item.Id + ".bin");
                            string? failure = null;
                            long exported = 0;
                            try { exported = await vault.ExportAsync(item, destination); }
                            catch (IOException exception) { failure = exception.Message; }
                            exports.Add(new { item.Id, item.Name, item.Bytes, item.Recovered, destination, exported, failure });
                        }
                    }
                    File.WriteAllText(Path.ChangeExtension(report, ".vault.json"), JsonSerializer.Serialize(new { opened, entries = vault.Entries, exports }));
                }
                else if (stage == "vault-backup")
                {
                    var vault = session.Vault;
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync(), "source vault opened");
                    string target = Path.GetDirectoryName(report)!;
                    var backup = new VaultBackupSource(Path.Combine(target, "synthetic.veil-backup"),
                        "synthetic-backup-vault-password", "SSSSS-TTTTT-UUUUU-VVVVV-WWWWW-XXXXX-YYYYY-ZZZZZ");
                    Check(await vault.BackupAsync(backup.Path, backup.Password) == 5, "five vault items backed up");
                    using var restored = new DownloadVault(Path.Combine(target, "restored-profile"), usesProfilePassword: true);
                    Directory.CreateDirectory(Path.Combine(target, "restored-profile"));
                    char[] password = "synthetic-restored-profile-password".ToCharArray();
                    try
                    {
                        Check(ProtectedVaultStartup.TryOpen(restored, password, () => false, () => null, _ => null,
                            setupFresh: () => (null, backup)), "backup restored and linked to profile password");
                        Check(vault.Entries.OrderBy(entry => entry.Id).SequenceEqual(restored.Entries.OrderBy(entry => entry.Id)),
                            "restored file metadata matches original");
                        restored.Lock();
                        Check(await restored.OpenWithProfilePasswordAsync(password) is null, "restored vault reopens");
                        var sample = restored.Entries.MaxBy(entry => entry.Bytes)!;
                        string oldFile = restored.ObjectPath(sample);
                        const string rotationCode = "33333-44444-55555-66666-77777-88888-99999-22222";
                        Check(await restored.RotateContentKeysAsync(password, rotationCode) == true &&
                            !File.Exists(oldFile) && File.Exists(restored.ObjectPath(sample)), "content key rotation switches and retires files");
                        string marker = Path.Combine(restored.Directory, "active-generation");
                        string previousGeneration = File.ReadAllText(marker);
                        using (File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            Check(await restored.RotateContentKeysAsync(password, backup.RecoveryCode) is null,
                                "failed content key publication reported");
                        Check(File.ReadAllText(marker) == previousGeneration &&
                            Directory.GetDirectories(restored.Directory, "gen-*").Length == 1,
                            "failed content rotation preserves active generation and cleans staging");
                        restored.Lock();
                        Check(await restored.RecoverAsync(backup.RecoveryCode) is not null, "superseded recovery code rejected");
                        Check(await restored.RecoverAsync(rotationCode) is null, "rotated vault reopens with new recovery code");
                        Check(restored.Entries.OrderBy(entry => entry.Id).SequenceEqual(vault.Entries.OrderBy(entry => entry.Id)),
                            "content rotation preserves download metadata after restart");
                        string destination = Path.Combine(target, "exports-backup");
                        Directory.CreateDirectory(destination);
                        string file = Path.Combine(destination, "large.bin");
                        Check(await restored.ExportAsync(sample, file) == sample.Bytes, "rotated large file authenticates");
#if DEBUG
                        string uiCode = await VaultWindow.VerifyMaintenanceUiAsync(this, restored, target,
                            "synthetic-restored-profile-password", Check);
                        restored.Lock();
                        Check(await restored.RecoverAsync(rotationCode) is not null, "previous recovery code rejected after UI rotation");
                        Check(await restored.RecoverAsync(uiCode) is null &&
                            restored.Entries.OrderBy(entry => entry.Id).SequenceEqual(vault.Entries.OrderBy(entry => entry.Id)),
                            "UI rotation recovery and metadata verified");
                        Check(await restored.VerifyAsync(sample) == sample.Bytes, "UI-rotated large file authenticates");
#endif
                        File.WriteAllText(Path.ChangeExtension(report, ".vault.json"),
                            JsonSerializer.Serialize(new { destination = file, sample.Bytes }));
                    }
                    finally { ProtectedProfileSession.ClearPassword(password); }
                }
                else if (stage == "vault-rotate")
                {
                    // Recovery-code unlock followed by a new passphrase and a new recovery code.
                    var vault = session.Vault;
                    string code = Environment.GetEnvironmentVariable("VEIL_DEV_VAULT_RECOVERY") ?? "";
                    Check(await vault.RecoverAsync(code) is null, "vault opened with recovery code");
                    string passphrase = Environment.GetEnvironmentVariable("VEIL_TEST_NEW_PASSPHRASE") ?? "";
                    string newCode = Environment.GetEnvironmentVariable("VEIL_TEST_NEW_RECOVERY") ?? "";
                    string bundle = Path.Combine(session.Root, "vault", "keyring.bundle");
                    byte[] previous = File.ReadAllBytes(bundle);
                    // Deny delete sharing so Windows rejects the final rename, after both envelopes are staged.
                    using (File.Open(bundle, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        Check(!await vault.CompleteRecoveryAsync(passphrase, newCode), "failed publication reported");
                    Check(!vault.Unlocked && vault.Entries.Count == 0, "failed rotation locked vault and cleared the list");
                    Check(previous.AsSpan().SequenceEqual(File.ReadAllBytes(bundle)), "failed publication preserved both envelopes");
                    Check(await vault.RecoverAsync(code) is null, "old code still opens after failed rotation");
                    Check(await vault.CompleteRecoveryAsync(passphrase, newCode), "vault recovery credentials updated");
                }
                else if (stage == "vault-profile")
                {
                    using var vault = new DownloadVault(session.Root, usesProfilePassword: true);
                    string previous = Environment.GetEnvironmentVariable("VEIL_TEST_OLD_PASSPHRASE") ?? "";
                    string previousCode = Environment.GetEnvironmentVariable("VEIL_TEST_OLD_RECOVERY") ?? "";
                    string newCode = Environment.GetEnvironmentVariable("VEIL_TEST_NEW_RECOVERY") ?? "";
                    char[] shared = "합성\tprofile 새 암호".ToCharArray();
                    char[] changed = "합성\tprofile 변경 암호".ToCharArray();
                    string? NoCodePrompt() => throw new InvalidOperationException("Unexpected recovery setup");
                    (string?, string?)? NoLegacyPrompt(string? _) => throw new InvalidOperationException("Unexpected unlock prompt");
                    try
                    {
                        Check(!ProtectedVaultStartup.TryOpen(vault, shared, () => false, NoCodePrompt, _ => null) &&
                            !vault.Unlocked, "cancelled migration leaves vault locked");
                        Check(ProtectedVaultStartup.TryOpen(vault, shared, () => false, NoCodePrompt, _ => (previous, null)) &&
                            vault.Entries.Count == 5, "legacy password links existing vault and preserves entries");
                        vault.Lock();
                        Check(await vault.UnlockAsync(previous) is not null, "separate password retired after linking");
                        Check(ProtectedVaultStartup.TryOpen(vault, shared, () => false, NoCodePrompt, NoLegacyPrompt),
                            "linked vault opens without a second prompt");
                        vault.Lock();
                        Check(!ProtectedVaultStartup.TryOpen(vault, changed, () => false, () => null, _ => (null, previousCode)) &&
                            !vault.Unlocked, "cancelled recovery does not expose vault");
                        Check(ProtectedVaultStartup.TryOpen(vault, changed, () => false, () => newCode, _ => (null, previousCode)),
                            "recovery relinks vault and issues new recovery code");
                        vault.Lock();
                        Check(await vault.RecoverAsync(previousCode) is not null, "old recovery code retired");
                        Check(ProtectedVaultStartup.TryOpen(vault, changed, () => false, NoCodePrompt, NoLegacyPrompt) &&
                            vault.Entries.Count == 5, "changed shared password reopens existing entries");

                        using var fresh = new DownloadVault(Path.Combine(session.Root, "fresh-linked-vault"), usesProfilePassword: true);
                        Check(!ProtectedVaultStartup.TryOpen(fresh, shared, () => false, () => null, NoLegacyPrompt) && !fresh.Exists,
                            "first use requires recovery confirmation");
                        Check(ProtectedVaultStartup.TryOpen(fresh, shared, () => false, () => newCode, NoLegacyPrompt),
                            "first use creates vault with shared password");
                        fresh.Lock();
                        Check(ProtectedVaultStartup.TryOpen(fresh, shared, () => false, NoCodePrompt, NoLegacyPrompt),
                            "new vault reopens without another prompt");
                    }
                    finally
                    {
                        ProtectedProfileSession.ClearPassword(shared);
                        ProtectedProfileSession.ClearPassword(changed);
                    }
                }
                else if (stage == "network")
                {
                    // ADR-0006 standard protection.
                    var none = new HashSet<string>();
                    Check(NetworkPolicy.HttpsUpgrade(new Uri("http://example.com/a?b=1"), none)?.AbsoluteUri == "https://example.com/a?b=1", "https-first upgrades public http");
                    Check(NetworkPolicy.HttpsUpgrade(new Uri("http://example.com:8080/"), none)?.AbsoluteUri == "https://example.com:8080/", "https-first keeps explicit port");
                    foreach (string local in new[] { "http://127.0.0.1:8765/", "http://localhost:3000/", "http://[::1]/", "http://192.168.0.10/", "http://10.1.2.3/", "http://172.20.0.1/", "http://169.254.1.1/", "http://intranet/", "http://printer.local/", "http://app.test/", "https://example.com/" })
                        Check(NetworkPolicy.HttpsUpgrade(new Uri(local), none) is null, $"https-first leaves {local}");
                    Check(NetworkPolicy.HttpsUpgrade(new Uri("http://example.com/"), new HashSet<string> { "example.com" }) is null, "https-first honours a user exception");
                    Check(new NetworkSettings(NetworkMode.Proxy, "ftp", "127.0.0.1", 9050).Problem is not null &&
                        new NetworkSettings(NetworkMode.Proxy, "socks5", "", 9050).Problem is not null &&
                        new NetworkSettings(NetworkMode.Proxy, "http", "127.0.0.1", 70000).Problem is not null &&
                        new NetworkSettings(NetworkMode.Proxy, "socks5", "127.0.0.1", 9050).Problem is null, "proxy settings validated");
                    Check(!NetworkPolicy.BrowserArguments(NetworkSettings.Standard).Any(), "standard mode adds no engine switches");
                    var proxyArgs = NetworkPolicy.BrowserArguments(new NetworkSettings(NetworkMode.Proxy, "socks5", "127.0.0.1", 9050)).ToList();
                    Check(proxyArgs.SequenceEqual(new[] { "--proxy-server=socks5://127.0.0.1:9050", "--proxy-bypass-list=<-loopback>",
                        "--webrtc-ip-handling-policy=disable_non_proxied_udp", "--disable-quic" }), "proxy mode engine switches");
                    Check(Active!.View.CoreWebView2.Profile.PreferredTrackingPreventionLevel == Microsoft.Web.WebView2.Core.CoreWebView2TrackingPreventionLevel.Strict, "tracking prevention strict");

                    // Live: a public http address is tried as https; the failure shows the warning page.
                    NavigateAddress("http://veil-https-first.invalid/page");
                    for (int i = 0; i < 300 && Active?.Url != HttpsWarningUrl; i++) await Task.Delay(75);
                    Check(Active!.Url == HttpsWarningUrl && Active.PendingHttp?.AbsoluteUri == "http://veil-https-first.invalid/page", "failed https upgrade shows the warning page");
                    // A script-initiated downgrade link must be ignored; only a real click may allow HTTP.
                    await Active.View.ExecuteScriptAsync("location.href = 'veil://allow-http'; null");
                    await Task.Delay(1500);
                    Check(Active.Url == HttpsWarningUrl && session.HttpAllowed.Count == 0,
                        $"script cannot downgrade to http (tab {Active.Url}, allowed [{string.Join(",", session.HttpAllowed)}])");
                    // The native bar is the only way: it is shown for the warning tab and its button allows the host.
                    Check(HttpsBar.Visibility == Visibility.Visible, "native downgrade bar shown");
                    AllowHttpClick(this, new RoutedEventArgs());
                    Check(session.HttpAllowed.Contains("veil-https-first.invalid") && Active.Url == "http://veil-https-first.invalid/page"
                        && HttpsBar.Visibility != Visibility.Visible, "native button allows http for that host only");
                    Check(session.Network.Mode == NetworkMode.Standard && NetworkIndicator.Text.StartsWith("표준 보호"), $"indicator: {NetworkIndicator.Text}");
                }
                else if (stage == "threats")
                {
                    // ADR-0008 local threat list. VEIL_TEST_THREAT_EXPECT: valid | invalid | none.
                    string expect = Environment.GetEnvironmentVariable("VEIL_TEST_THREAT_EXPECT") ?? "none";
                    var list = session.Security.ThreatList;
                    string local = new UriBuilder(uri) { Host = "localhost" }.Uri.AbsoluteUri; // listed host (valid case)
                    async Task Open(string address)
                    {
                        Active!.LastNavigationError = null;
                        NavigateAddress(address);
                        await Task.Delay(400);
                        for (int i = 0; i < 200 && (Active.Loading || Active.Url == "about:blank"); i++) await Task.Delay(75);
                    }
                    if (expect == "none")
                    {
                        Check(!list.Present && session.Security.Threats.Engines.Count == 0, "no list: no VEIL engine");
                        await Open(local);
                        Check(Active!.Url != ThreatWarningUrl, "no list: navigation not blocked");
                    }
                    else if (expect == "invalid")
                    {
                        Check(list is { Present: true, Valid: false }, $"damaged list reported ({list.Detail})");
                        Check(NetworkIndicator.Text.Contains("위협 목록 오류"), $"status shows list error: {NetworkIndicator.Text}");
                        await Open(local);
                        Check(Active!.Url != ThreatWarningUrl && Active.LastNavigationError is null or Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.Unknown,
                            $"fail-open: page still opens ({Active.Url})");
                        Check(LastThreatNotice?.Contains("위협 검사를 할 수 없어") == true, $"navigation notice explains the skipped check: {LastThreatNotice}");
                    }
                    else
                    {
                        Check(list is { Present: true, Valid: true } && list.Entries >= 3, $"signed list loaded ({list.Detail})");
                        // Downloads first (before the user allows the host for navigation).
                        Check(await session.Vault.TryOpenFromDevelopmentEnvironmentAsync(), "vault opened");
                        int before = session.Vault.Entries.Count;
                        async Task<string> Capture(string address)
                        {
                            try { await session.Vault.CaptureAsync(new Uri(address), new BrowserDownload("threat-check.bin"), Active!.View.CoreWebView2); return "stored"; }
                            catch (ThreatBlockedException blocked) { return "blocked: " + blocked.Message; }
                        }
                        string listedFile = await Capture(origin + "/vault-dl?kind=small");
                        Check(listedFile.StartsWith("blocked") && listedFile.Contains("파일"), $"listed file hash blocked before publishing ({listedFile})");
                        string listedHop = await Capture(origin + "/vault-dl?kind=redirect");
                        Check(listedHop.StartsWith("blocked") && listedHop.Contains("주소"), $"listed redirect target blocked ({listedHop})");
                        string listedSource = await Capture(local + "vault-dl?kind=large");
                        Check(listedSource.StartsWith("blocked") && listedSource.Contains("사이트"), $"listed source host blocked ({listedSource})");
                        Check(session.Vault.Entries.Count == before, "nothing published for blocked downloads");
                        Check(await Capture(origin + "/vault-dl?kind=large") == "stored", "unlisted download stored (Unknown passes)");
                        // Navigation: listed host and listed URL show the warning; others open.
                        await Open(local);
                        Check(Active!.Url == ThreatWarningUrl && Active.PendingThreat?.Host == "localhost", "listed host shows the warning page");
                        Check(ThreatBar.Visibility == Visibility.Visible, "native continue bar shown");
                        await Active.View.ExecuteScriptAsync("location.href = " + JsonSerializer.Serialize(local) + "; null");
                        await Task.Delay(1500);
                        Check(Active.Url == ThreatWarningUrl && session.ThreatAllowed.Count == 0, "page script cannot get past the warning");
                        ProceedThreatClick(this, new RoutedEventArgs());
                        for (int i = 0; i < 200 && Active.Loading; i++) await Task.Delay(75);
                        Check(session.ThreatAllowed.Contains("localhost") && Active.Url.StartsWith(local) && ThreatBar.Visibility != Visibility.Visible,
                            "native button opens the site for this run");
                        await Open(origin + "/second");
                        Check(Active.Url == ThreatWarningUrl && Active.PendingThreat?.AbsolutePath == "/second", "listed URL shows the warning page");
                        await Open(origin + "/");
                        Check(Active.Url == origin + "/", "unlisted address opens (Unknown is not blocked)");
                        var (tabs, _) = Snapshot();
                        Check(!tabs.Contains(ThreatWarningUrl), "warning page is saved as the original address");
                    }
                }
                else if (stage == "jumplist")
                {
                    // SEC-004 jump-list mitigation: only lists naming this profile are removed.
                    string engineProfile = Path.Combine(session.Root, "webview2");
                    string folder = JumpListScrubber.ListFolder;
                    Directory.CreateDirectory(folder);
                    string Synthetic() => Path.Combine(folder, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + ".customDestinations-ms");
                    string ours = Synthetic(), other = Synthetic(), utf8 = Synthetic();
                    byte[] Shell(string text, System.Text.Encoding encoding) => [0x02, 0x00, 0x00, 0x00, 0x01, .. encoding.GetBytes(text), 0xAB, 0xBA];
                    try
                    {
                        // Case differs from the profile path; one byte of padding makes the UTF-16 text odd-aligned.
                        File.WriteAllBytes(ours, Shell($"--user-data-dir=\"{engineProfile.ToUpperInvariant()}\\EBWebView\" --edge-jumplist-new-window", System.Text.Encoding.Unicode));
                        File.WriteAllBytes(utf8, Shell($"--user-data-dir=\"{engineProfile}\"", System.Text.Encoding.UTF8));
                        File.WriteAllBytes(other, Shell("--user-data-dir=\"C:\\Users\\someone\\AppData\\Local\\OtherApp\" --edge-jumplist-new-window", System.Text.Encoding.Unicode));
                        using var scrubber = new JumpListScrubber(engineProfile);
                        // The app's own scrubber may win the race; either way both lists must be gone.
                        scrubber.Scrub();
                        Check(!File.Exists(ours) && !File.Exists(utf8), "lists naming this profile removed (UTF-16 odd offset, other case, UTF-8)");
                        Check(File.Exists(other), "other applications' lists untouched");
                        // Watcher: a list written while running is removed without an explicit call.
                        string late = Synthetic();
                        File.WriteAllBytes(late, Shell($"--user-data-dir=\"{engineProfile}\"", System.Text.Encoding.Unicode));
                        for (int i = 0; i < 40 && File.Exists(late); i++) await Task.Delay(100);
                        Check(!File.Exists(late), "list written during the run removed by the watcher");
                    }
                    finally
                    {
                        foreach (string file in new[] { ours, other, utf8 }) File.Delete(file);
                    }
                }
                else if (stage == "security")
                {
                    await SecurityStage();
                }
                else if (stage == "visit")
                {
                    // Gate D internet-VM checks: open VEIL_TEST_URL as typed (HTTPS-first applies) and report the
                    // outcome; optionally wait for VEIL_TEST_EVAL to return a non-null value.
                    string target = Environment.GetEnvironmentVariable("VEIL_TEST_URL") ?? throw new ArgumentException("VEIL_TEST_URL");
                    Active!.LastNavigationError = null;
                    NavigateAddress(target);
                    await Task.Delay(800);
                    for (int i = 0; i < 400 && Active.Loading; i++) await Task.Delay(75);
                    string evaluated = "null";
                    if (Environment.GetEnvironmentVariable("VEIL_TEST_EVAL") is { Length: > 0 } expression)
                    {
                        for (int i = 0; i < 200 && evaluated == "null"; i++)
                        {
                            evaluated = await Active.View.ExecuteScriptAsync($"(() => {{ const v = ({expression}); return v === undefined ? null : v; }})()");
                            if (evaluated == "null") await Task.Delay(100);
                        }
                    }
                    string title = await Active.View.ExecuteScriptAsync("document.title");
                    checks.Add($"visit: url={Active.Url}");
                    checks.Add($"visit: error={Active.LastNavigationError?.ToString() ?? "none"}");
                    checks.Add($"visit: title={title}");
                    checks.Add($"visit: eval={evaluated}");
                    checks.Add($"visit: smartscreen={SecurityManager.Read(Active.View.CoreWebView2)}");
                }
                else if (stage == "dns-probe")
                {
                    // DNS protection experiments: open VEIL_TEST_DNS_URL and report how it ended.
                    string target = Environment.GetEnvironmentVariable("VEIL_TEST_DNS_URL") ?? throw new ArgumentException("VEIL_TEST_DNS_URL");
                    Active!.LastNavigationError = null;
                    Active.Url = target;
                    Active.View.CoreWebView2.Navigate(target);
                    await Task.Delay(500);
                    for (int i = 0; i < 300 && Active.Loading; i++) await Task.Delay(75);
                    string title = await Active.View.ExecuteScriptAsync("document.title");
                    checks.Add($"dns probe: error={Active.LastNavigationError?.ToString() ?? "none"} title={title}");
                }
                else if (stage == "network-proxy")
                {
                    // ADR-0006 proxy mode: browsing and VEIL's own vault requests use only the proxy.
                    var settings = session.Network;
                    Check(settings.Mode == NetworkMode.Proxy, "proxy mode applied from saved settings");
                    checks.Add($"indicator: {NetworkIndicator.Text}");
                    Check(NetworkIndicator.Text.StartsWith(NetworkPolicy.ProxyName) && NetworkPolicy.ProxyName.Contains("실험적") &&
                        BrowserSettingsCatalog.Build(session).Any(item => item.State?.Contains(NetworkPolicy.ProxyName) == true),
                        "proxy protection is labeled experimental in the status bar and settings (ADR-0010)");
                    checks.Add($"proxy reachable: {session.ProxyReachable}");
                    NavigateAddress(origin + "/second");
                    for (int i = 0; i < 200 && Active?.Loading != false; i++) await Task.Delay(75);
                    await Task.Delay(500);
                    string text = await Active!.View.ExecuteScriptAsync("document.title");
                    checks.Add($"page title: {text}");
                    bool vaultOpen = await session.Vault.TryOpenFromDevelopmentEnvironmentAsync();
                    checks.Add($"vault open: {vaultOpen}");
                    string vaultOutcome;
                    try
                    {
                        var entry = await session.Vault.CaptureAsync(new Uri(origin + "/vault-dl?kind=small"), new BrowserDownload("proxy-check.bin"), Active.View.CoreWebView2);
                        vaultOutcome = $"stored {entry.Bytes}";
                    }
                    catch (Exception exception) when (exception is IOException or System.Net.Http.HttpRequestException or InvalidOperationException or TaskCanceledException)
                    {
                        vaultOutcome = $"failed {exception.GetType().Name}";
                    }
                    checks.Add($"vault download: {vaultOutcome}");
                }
                else if (stage is "storm" or "storm-check")
                {
                    // Phase 3: the engine is killed while IndexedDB/localStorage commits are in flight.
                    // Each start must find the stored records readable and gap-free.
                    NavigateAddress(origin + "/storm");
                    await ReadyAt("/storm");
                    var state = await Evaluate("stormRead()");
                    Check(!state.TryGetProperty("error", out _), "storm storage opens");
                    int max = state.GetProperty("max").GetInt32();
                    Check(state.GetProperty("contiguous").GetBoolean(), $"storm records contiguous (max {max}, localStorage {state.GetProperty("ls").GetInt32()})");
                    if (stage == "storm")
                    {
                        await Active!.View.ExecuteScriptAsync($"stormStart({max}); null");
                        for (int i = 0; i < 400; i++)
                        {
                            if (int.TryParse(await Active!.View.ExecuteScriptAsync("window.stormGen"), out int gen) && gen >= max + 100) break;
                            await Task.Delay(75);
                        }
                        File.WriteAllText(report, JsonSerializer.Serialize(new
                        {
                            stormReady = true,
                            browserPid = Active!.View.CoreWebView2.BrowserProcessId,
                            before = max,
                        }));
                        // VEIL_STORM_HOLD_SECONDS keeps the writer running longer for the VM power-cut test.
                        int hold = int.TryParse(Environment.GetEnvironmentVariable("VEIL_STORM_HOLD_SECONDS"), out int seconds) ? seconds : 30;
                        for (var stopAt = DateTime.UtcNow.AddSeconds(hold); DateTime.UtcNow < stopAt && !session.EngineLost;)
                            await Task.Delay(75);
                        Check(session.EngineLost, "engine loss detected");
                    }
                }
#if DEBUG
                else if (IsPhase6Stage(stage))
                {
                    await Phase6StageAsync(stage, uri, checks);
                }
#endif
                else throw new ArgumentException("Unknown desktop smoke stage.");

                async Task<JsonElement> Evaluate(string expression)
                {
                    await Active!.View.ExecuteScriptAsync("window.__veil = undefined; Promise.resolve(" + expression +
                        ").then(r => window.__veil = JSON.stringify(r), e => window.__veil = JSON.stringify({ error: String(e) })); null");
                    for (int attempt = 0; attempt < 200; attempt++)
                    {
                        string raw = await Active!.View.ExecuteScriptAsync("window.__veil ?? null");
                        if (raw != "null") return JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw)!).RootElement.Clone();
                        await Task.Delay(75);
                    }
                    throw new TimeoutException(expression);
                }

                async Task SecurityStage()
                {
                    // ADR-0007 Security Manager. VEIL_TEST_SECURITY_EXPECT: on | off | recovered (saved state at start).
                    // VEIL_TEST_SECURITY_SET: on | off (change during this run). VEIL_TEST_SECURITY_URL: page opened at the end.
                    var security = session.Security;
                    string expect = Environment.GetEnvironmentVariable("VEIL_TEST_SECURITY_EXPECT") ?? "on";
                    Check(security.Saved.SmartScreen == (expect != "off") && security.Saved.Recovered == (expect == "recovered"),
                        $"saved policy at start: {expect}");
                    bool want = security.Saved.SmartScreen;
                    void AllViews(bool value, string when)
                    {
                        var states = session.Windows.SelectMany(window => window.CoreViews()).Select(SecurityManager.Read).ToList();
                        Check(states.Count > 0 && states.All(state => state == value), $"every view SmartScreen={value} {when} ({states.Count} views)");
                        Check(security.Verify() == SmartScreenState.Applied, $"state applied {when}");
                    }
                    AllViews(want, "after restore");
                    Check(await OpenTab("about:blank") is not null, "new tab");
                    AllViews(want, "after new tab");
                    var second = OpenWindow(["about:blank"], 0);
                    await WaitFor(() => !second.IsRestoring && second.CoreViews().Any(), "new window");
                    AllViews(want, "after new window");
                    Check(security.Threats.Engines.Count == 0, "no VEIL engine registered without a list");
                    Check(LocalThreatList.ProductionKeys.Length == 0, "no production list key yet (no list accepted in Release)");
                    Check((await security.Threats.CheckAsync(new Uri("https://example.test/"), TimeSpan.FromSeconds(1))).Verdict == ThreatVerdict.Unknown,
                        "no engine gives Unknown, not safe");

                    // A save failure changes nothing (saved or applied).
                    try { security.Change(new SecuritySettings(!want), _ => throw new IOException("simulated")); Check(false, "save failure surfaced"); }
                    catch (IOException) { checks.Add("save failure surfaced"); }
                    Check(security.Saved.SmartScreen == want, "save failure keeps saved policy");
                    AllViews(want, "after failed save");

                    if (Environment.GetEnvironmentVariable("VEIL_TEST_SECURITY_SET") is { } set)
                    {
                        bool value = set == "on";
                        Check(ChangeSecurity(new SecuritySettings(value)), $"change to {set} applied");
                        AllViews(value, "after change");
                        var stored = BrowserData.Load(statePath).Security ?? SecuritySettings.Default;
                        Check(stored.SmartScreen == value && !stored.Recovered, "change saved to profile state");
                        Check(await OpenTab("about:blank") is not null, "tab after change");
                        AllViews(value, "after change and new tab");
                        Check(value || NetworkIndicator.Text.Contains("SmartScreen 꺼짐"), $"indicator: {NetworkIndicator.Text}");
                        want = value;
                    }

#if DEBUG
                    // Threat interface with the test-only mock engine (separate registry; the session's stays empty).
                    var threats = new ThreatProtection();
                    Check(threats.Register(new MockThreatEngine(new Dictionary<string, ThreatVerdict>
                    {
                        ["bad.test"] = ThreatVerdict.Malicious, ["good.test"] = ThreatVerdict.NotDetected, ["broken.test"] = ThreatVerdict.Error,
                    })), "mock engine registered (Debug)");
                    async Task<ThreatResult> Probe(string host) => await threats.CheckAsync(new Uri($"https://{host}/path?token=x"), TimeSpan.FromSeconds(2));
                    Check((await Probe("bad.test")).Verdict == ThreatVerdict.Malicious, "mock: Malicious");
                    Check((await Probe("good.test")).Verdict == ThreatVerdict.NotDetected, "mock: NotDetected");
                    Check((await Probe("other.test")).Verdict == ThreatVerdict.Unknown, "mock: unlisted host is Unknown, not safe");
                    var failed = await Probe("broken.test");
                    Check(failed.Verdict == ThreatVerdict.Error && failed.Reason == nameof(InvalidOperationException), "mock: engine failure is Error");
                    Check(!failed.Reason.Contains("broken") && !failed.Reason.Contains("token"), "result carries no address");
                    var slow = new ThreatProtection();
                    slow.Register(new MockThreatEngine(new Dictionary<string, ThreatVerdict> { ["good.test"] = ThreatVerdict.NotDetected }, TimeSpan.FromSeconds(5)));
                    var late = await slow.CheckAsync(new Uri("https://good.test/"), TimeSpan.FromMilliseconds(200));
                    Check(late.Verdict == ThreatVerdict.Error && late.Reason == "timeout", "mock: timeout is Error, not Unknown");
                    threats.Register(new MockThreatEngine(new Dictionary<string, ThreatVerdict> { ["bad.test"] = ThreatVerdict.NotDetected, ["good.test"] = ThreatVerdict.Error }));
                    Check((await Probe("bad.test")).Verdict == ThreatVerdict.Malicious, "combined: Malicious wins over NotDetected");
                    Check((await Probe("good.test")).Verdict == ThreatVerdict.Error, "combined: Error wins over NotDetected");
                    AllViews(want, "after engine verdicts (SmartScreen not bypassed)");
#endif
                    if (Environment.GetEnvironmentVariable("VEIL_TEST_SECURITY_URL") is { } target)
                    {
                        Active!.Url = target;
                        Active.View.CoreWebView2.Navigate(target);
                        await Task.Delay(500);
                        for (int i = 0; i < 300 && Active.Loading; i++) await Task.Delay(75);
                        await Task.Delay(Environment.GetEnvironmentVariable("VEIL_TEST_SECURITY_DWELL") is { } dwell && int.TryParse(dwell, out int ms) ? ms : 0);
                        checks.Add($"navigation: error={Active.LastNavigationError?.ToString() ?? "none"}");
                    }
                    checks.Add($"smartscreen: {security.Describe()}");
                    second.Close();
                }

                async Task ReadyAt(string path)
                {
                    string url = new Uri(uri, path).AbsoluteUri;
                    await WaitFor(() => Active?.Url == url, $"navigated to {path}");
                    await WaitForScript($"location.href === {JsonSerializer.Serialize(url)} && document.readyState === 'complete'", $"document loaded at {path}");
                }

                async Task Download(string script, string type)
                {
                    var original = Tabs.SelectedItem;
                    Check(await OpenTab(origin) is not null, $"fresh download tab ({type})");
                    var downloadTab = (TabItem)Tabs.SelectedItem;
                    await ReadyAt("/");
                    if (type == "sw")
                        await WaitForScript("Boolean(navigator.serviceWorker.controller)", "service worker has control");
                    int count = downloads.Count;
                    await Active!.View.ExecuteScriptAsync(script + "; null");
                    await WaitFor(() => downloads.Count == count + 1, $"download cancelled ({type})");
                    // Phase 4-0: supported GET downloads go to the vault; others are blocked, never stored in plaintext.
                    string expected = type switch
                    {
                        "get" or "auth" or "redirect" or "large" => DownloadVault.Stored,
                        "data" when InlineDownloadPolicy.Enabled => DownloadVault.Stored,
                        "sw" => DownloadVault.Failed, // VEIL refetches from the network, where /sw-file does not exist
                        _ => DownloadVault.Blocked,
                    };
                    Check(downloads[0].StartsWith($"veil-phase0-{type}.bin · {expected}"), $"download {expected} ({type}): {downloads[0]}");
                    if (type == "data" && InlineDownloadPolicy.Enabled)
                    {
                        var entry = session.Vault.Entries.First(e => e.Name == "veil-phase0-data.bin");
                        Check(await session.Vault.VerifyAsync(entry) == 9, "inline data authenticated size");
                    }
                    Check(Active!.View.CoreWebView2.Profile.DefaultDownloadFolderPath.StartsWith(
                        Path.Combine(session.Root, ProfileScratch.FolderName), StringComparison.OrdinalIgnoreCase),
                        $"download scratch inside profile ({type})");
                    if (type == "get")
                    {
                        await WaitFor(() => DownloadButton.Visibility == Visibility.Visible, "download toolbar icon shown");
                        var before = Active;
                        int tabCount = Tabs.Items.Count;
                        DownloadToolbarClick(DownloadButton, new RoutedEventArgs());
                        Check(DownloadPopup.IsOpen && Active == before && Tabs.Items.Count == tabCount,
                            "toolbar download click opens recent popup without navigating");
                        DownloadsClick(this, new RoutedEventArgs());
                        DownloadsClick(this, new RoutedEventArgs());
                        await WaitFor(() => Active?.Url == "veil://downloads" && !DownloadPopup.IsOpen && !openingDownloads,
                            "all downloads opened in tab");
                        Check(Active?.Title == "다운로드" && Active.ManagementSurface is DownloadOverview,
                            "downloads tab uses native overview");
                        Check(Tabs.Items.OfType<TabItem>().Count(item => item.Tag is BrowserTab { Url: "veil://downloads" }) == 1,
                            "repeated download manager invocation opens only one tab");
                        CloseTab((TabItem)Tabs.SelectedItem);
                        CloseTab(downloadTab);
                        Tabs.SelectedItem = original;
                    }
                    else
                    {
                        CloseTab(downloadTab);
                        Tabs.SelectedItem = original;
                    }
                }
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
            }

            if (engine is not null && Tabs.Items.Count > 0 && session.EngineLost)
            {
                // The killed engine cannot report exit again; just release the windows and the profile.
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                foreach (var window in session.Windows.ToList()) window.Close();
            }
            else if (engine is not null && Tabs.Items.Count > 0)
            {
                var exit = new TaskCompletionSource();
                engine.BrowserProcessExited += (_, _) => exit.TrySetResult();
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Close();
                try { await exit.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (TimeoutException) { error ??= "WebView2 did not exit after closing the desktop window."; }
            }
            if (stage == "write" && error is null)
            {
                try
                {
                    var saved = BrowserData.Load(statePath);
                    if (saved.Tabs.Count != 3 || saved.Selected != 1 || saved.Bookmarks.Count != 1 ||
                        !saved.History.Any(entry => entry.Url.EndsWith("/second")))
                        throw new InvalidDataException("Saved desktop state does not match the open session.");
                    checks.Add("browser state persisted after closing");
                }
                catch (Exception exception)
                {
                    error = $"{exception.GetType().Name}: {exception.Message}";
                }
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { checks, error }));
            Application.Current.Shutdown(error is null ? 0 : 1);

            void Check(bool passed, string name)
            {
                if (!passed) throw new InvalidOperationException(name);
                checks.Add(name);
            }

            async Task WaitFor(Func<bool> condition, string name)
            {
                for (int attempt = 0; attempt < 120; attempt++)
                {
                    if (condition()) { Check(true, name); return; }
                    await Task.Delay(75);
                }
                throw new TimeoutException(name);
            }

            static T? FindProgressElement<T>(DependencyObject parent, Func<T, bool>? accept = null) where T : DependencyObject
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is T found && (accept is null || accept(found))) return found;
                    if (FindProgressElement(child, accept) is { } nested) return nested;
                }
                return null;
            }

            async Task WaitForScript(string script, string name)
            {
                for (int attempt = 0; attempt < 120; attempt++)
                {
                    var result = await Active!.View.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(4));
                    if (result == "true") { Check(true, name); return; }
                    await Task.Delay(75);
                }
                throw new TimeoutException(name);
            }
        };
    }
}
