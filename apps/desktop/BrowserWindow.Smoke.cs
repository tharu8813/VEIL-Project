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
                int searchWrites = 0, searchAuthorization = 0;
                bool SearchWrite() { searchWrites++; return true; }
                Check(!localData.TryCommitSearchSettings("unknown", true, () => true, SearchWrite) &&
                    !localData.TryCommitSearchSettings("google", true, () => false, SearchWrite) &&
                    searchWrites == 0 && localData.SearchEngine is null && localData.LocalAddressSuggestions == false,
                    "search settings reject invalid provider and denied authority without write");
                Check(!localData.TryCommitSearchSettings("google", true, () => ++searchAuthorization == 1, SearchWrite) &&
                    searchWrites == 0 && localData.SearchEngine is null && localData.LocalAddressSuggestions == false,
                    "search settings restore both fields when authorization is revoked");
                Check(!localData.TryCommitSearchSettings("google", true, () => true, () => false) &&
                    localData.SearchEngine is null && localData.LocalAddressSuggestions == false,
                    "search settings restore both fields after write refusal");
                bool searchException = false;
                try
                {
                    localData.TryCommitSearchSettings("google", true, () => true,
                        () => throw new IOException("Synthetic search write failure"));
                }
                catch (IOException) { searchException = true; }
                Check(searchException && localData.SearchEngine is null && localData.LocalAddressSuggestions == false,
                    "search settings restore both fields after write exception");
                Check(localData.TryCommitSearchSettings("google", true, () => true, SearchWrite) &&
                    searchWrites == 1 && localData.SearchEngine == "google" && localData.LocalAddressSuggestions is null &&
                    SearchPolicy.Suggest(localData, "synthetic").Count == 1,
                    "search settings commit provider and enable local suggestions together");
                Check(localData.TryCommitSearchSettings(SearchPolicy.Default, false, () => true, SearchWrite) &&
                    searchWrites == 2 && localData.SearchEngine is null && localData.LocalAddressSuggestions == false,
                    "search settings preserve default provider and disabled suggestions encoding");
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
                var originalBookmark = localData.Bookmarks[0];
                var editFolder = new BookmarkFolder(Guid.NewGuid(), "Transaction");
                int saves = 0, authorizationChecks = 0;
                Check(!LibraryMutation.TryCommit(localData.Bookmarks, localData.BookmarkFolders,
                    () => ++authorizationChecks == 1,
                    () => { localData.BookmarkFolders.Add(editFolder); localData.Bookmarks[0] = originalBookmark with { FolderId = editFolder.Id }; },
                    () => { saves++; return true; }) &&
                    saves == 0 && localData.BookmarkFolders.Count == 0 &&
                    ReferenceEquals(localData.Bookmarks[0], originalBookmark),
                    "library authorization revoked before save restores exact entry and folders");
                Check(!LibraryMutation.TryCommit(localData.Bookmarks, localData.BookmarkFolders, () => true,
                    () => { localData.BookmarkFolders.Add(editFolder); localData.Bookmarks.Clear(); },
                    () => { saves++; return false; }) &&
                    saves == 1 && localData.BookmarkFolders.Count == 0 &&
                    ReferenceEquals(localData.Bookmarks[0], originalBookmark),
                    "library save failure restores original entries and folders");
                Check(LibraryMutation.TryCommit(localData.Bookmarks, localData.BookmarkFolders, () => true,
                    () => localData.BookmarkFolders.Add(editFolder), () => { saves++; return true; }) &&
                    saves == 2 && localData.BookmarkFolders.Single() == editFolder,
                    "library authorized save commits once");
                localData.BookmarkFolders.Clear();
                var originalHistory = localData.History.ToArray();
                var pendingVisit = new PageEntry("Visit", "https://example.test/visit", DateTimeOffset.Now);
                Check(!LibraryMutation.TryCommit(localData.History, null, () => true,
                    () => localData.History.Insert(0, pendingVisit), () => false) &&
                    localData.History.SequenceEqual(originalHistory),
                    "failed automatic history commit restores prior list");
                Check(LibraryMutation.TryCommit(localData.History, null, () => true,
                    () => localData.History.Insert(0, pendingVisit), () => true) &&
                    ReferenceEquals(localData.History[0], pendingVisit),
                    "successful automatic history commit publishes exact entry");
                localData.History.RemoveAt(0);
                localData.Tabs = ["https://example.test/original", "https://example.test/selected"];
                localData.Selected = 1;
                localData.PinnedCount = 1;
                localData.Windows = [new WindowTabs(["https://example.test/other"], 0, 1)];
                var oldTabs = localData.Tabs;
                var oldWindows = localData.Windows;
                void CaptureReplacement()
                {
                    localData.Tabs = ["https://example.test/replacement"];
                    localData.Selected = 0;
                    localData.PinnedCount = 0;
                    localData.Windows = null;
                }
                bool OriginalLayout() => ReferenceEquals(localData.Tabs, oldTabs) &&
                    ReferenceEquals(localData.Windows, oldWindows) &&
                    localData.Selected == 1 && localData.PinnedCount == 1;
                Check(!LibraryMutation.TryCommit(localData.History, null, () => true,
                    () => localData.History.Insert(0, pendingVisit),
                    () => localData.TryCommitTabLayout(() => { CaptureReplacement(); return false; })) &&
                    localData.History.SequenceEqual(originalHistory) && OriginalLayout(),
                    "failed combined history and tab commit restores both snapshots");
                Check(!LibraryMutation.TryCommit(localData.History, null, () => true,
                    () => localData.History.Insert(0, pendingVisit),
                    () => localData.TryCommitTabLayout(() =>
                    {
                        CaptureReplacement();
                        throw new IOException("Synthetic state write failure");
                    })) && localData.History.SequenceEqual(originalHistory) && OriginalLayout(),
                    "combined state write exception restores history and tab layout");
                Check(LibraryMutation.TryCommit(localData.History, null, () => true,
                    () => localData.History.Insert(0, pendingVisit),
                    () => localData.TryCommitTabLayout(() => { CaptureReplacement(); return true; })) &&
                    ReferenceEquals(localData.History[0], pendingVisit) &&
                    localData.Tabs.Single() == "https://example.test/replacement" &&
                    localData.Selected == 0 && localData.PinnedCount == 0 && localData.Windows is null,
                    "successful combined commit retains history and replacement layout");
                localData.History.RemoveAt(0);
                // Exercise a real filesystem refusal in a synthetic directory inside
                // the fixture profile; do not touch the active browser-state.json.
                string layoutTestRoot = Path.Combine(Path.GetTempPath(), "layout-save-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(layoutTestRoot);
                try
                {
                    string layoutPath = Path.Combine(layoutTestRoot, "state.json");
                    localData.Tabs = oldTabs;
                    localData.Selected = 1;
                    localData.PinnedCount = 1;
                    localData.Windows = oldWindows;
                    localData.Save(layoutPath);
                    byte[] originalBytes = File.ReadAllBytes(layoutPath);
                    // A directory at the staging path prevents FileStream creation.
                    Directory.CreateDirectory(layoutPath + ".new");
                    bool writeRefused = false;
                    try
                    {
                        localData.TryCommitTabLayout(() =>
                        {
                            CaptureReplacement();
                            localData.Save(layoutPath);
                            return true;
                        });
                    }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                    {
                        writeRefused = true;
                    }
                    Check(writeRefused && OriginalLayout() &&
                        File.ReadAllBytes(layoutPath).SequenceEqual(originalBytes),
                        "ordinary tab save filesystem refusal keeps previous layout and committed bytes");
                    Directory.Delete(layoutPath + ".new");
                    Check(localData.TryCommitTabLayout(() =>
                    {
                        CaptureReplacement();
                        localData.Save(layoutPath);
                        return true;
                    }), "ordinary tab save retry commits replacement layout");
                    var savedLayout = BrowserData.Load(layoutPath);
                    Check(savedLayout.Tabs.SequenceEqual(localData.Tabs) &&
                        savedLayout.Selected == 0 && savedLayout.PinnedCount == 0 && savedLayout.Windows is null,
                        "ordinary tab save retry reads back replacement layout");
                }
                finally { Directory.Delete(layoutTestRoot, recursive: true); }
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
                Check(LoginFormPolicy.Probe("https://example.test", "https://example.test/login").Contains("return true;") &&
                    !LoginFormPolicy.Probe("https://example.test", "https://example.test/login").Contains("pass.value") &&
                    !LoginFormPolicy.Fill(new SavedLogin("https://example.test", "synthetic", "synthetic-pass"), "https://example.test/login").Contains(".submit("),
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
                var permissionData = new BrowserData { SitePermissions = [rule] };
                var originalRules = permissionData.SitePermissions;
                var allowRule = rule with { Mode = SitePermissionPolicy.Allow };
                int permissionWrites = 0, permissionChecks = 0;
                bool PermissionWrite() { permissionWrites++; return true; }
                Check(!permissionData.TryCommitSitePermissions([allowRule, allowRule], () => true, PermissionWrite) &&
                    !permissionData.TryCommitSitePermissions([allowRule with { Origin = "http://example.test" }], () => true, PermissionWrite) &&
                    !permissionData.TryCommitSitePermissions([allowRule], () => false, PermissionWrite) &&
                    permissionWrites == 0 && ReferenceEquals(permissionData.SitePermissions, originalRules),
                    "permission commit rejects invalid or unauthorized draft without write");
                Check(!permissionData.TryCommitSitePermissions([allowRule], () => ++permissionChecks == 1, PermissionWrite) &&
                    permissionWrites == 0 && ReferenceEquals(permissionData.SitePermissions, originalRules),
                    "permission commit restores original rules when authority is revoked");
                Check(!permissionData.TryCommitSitePermissions([allowRule], () => true, () => false) &&
                    ReferenceEquals(permissionData.SitePermissions, originalRules) &&
                    SitePermissionPolicy.ModeFor(permissionData.SitePermissions, rule.Origin, rule.Kind) == SitePermissionPolicy.Ask,
                    "permission commit refusal never publishes a new allow rule");
                bool permissionWriteFailed = false;
                try
                {
                    permissionData.TryCommitSitePermissions([allowRule], () => true,
                        () => throw new IOException("Synthetic permission write failure"));
                }
                catch (IOException) { permissionWriteFailed = true; }
                Check(permissionWriteFailed && ReferenceEquals(permissionData.SitePermissions, originalRules),
                    "permission commit exception restores exact previous rules");
                var permissionDraft = new List<SitePermissionRule> { allowRule };
                Check(permissionData.TryCommitSitePermissions(permissionDraft, () => true, PermissionWrite) &&
                    permissionWrites == 1 && !ReferenceEquals(permissionDraft, permissionData.SitePermissions),
                    "permission commit owns an independent copy");
                permissionDraft.Clear();
                Check(SitePermissionPolicy.ModeFor(permissionData.SitePermissions, rule.Origin, rule.Kind) == SitePermissionPolicy.Allow &&
                    SitePermissionPolicy.ModeFor(permissionData.SitePermissions, rule.Origin, "microphone") == SitePermissionPolicy.Block,
                    "committed permission remains exact-scope after dialog draft changes");
                Check(permissionData.TryCommitSitePermissions([], () => true, PermissionWrite) &&
                    permissionWrites == 2 && permissionData.SitePermissions is null &&
                    SitePermissionPolicy.ModeFor(permissionData.SitePermissions, rule.Origin, rule.Kind) == SitePermissionPolicy.Block,
                    "empty permission commit restores default deny");
                var unusedEngine = new WebView2BrowserEngine();
                Check(!unusedEngine.IsReady && unusedEngine.RuntimeVersion is null, "uninitialized engine state");
                bool refusedView = false;
                try { using var premature = unusedEngine.CreateView(); }
                catch (InvalidOperationException) { refusedView = true; }
                Check(refusedView, "engine refuses view before verified initialization");
                var previewEntry = new VaultEntry(Guid.NewGuid().ToString("N"), "synthetic.txt", 12, DateTimeOffset.Now, "example.test");
                Check(DownloadVault.ValidIndex([]) && DownloadVault.ValidIndex([previewEntry]) &&
                    !DownloadVault.ValidIndex(null) && !DownloadVault.ValidIndex([null!]) &&
                    !DownloadVault.ValidIndex([previewEntry, previewEntry]) &&
                    !DownloadVault.ValidIndex([previewEntry, previewEntry with { Id = previewEntry.Id.ToUpperInvariant() }]) &&
                    !DownloadVault.ValidIndex([previewEntry with { Id = "../invalid" }]) &&
                    !DownloadVault.ValidIndex([previewEntry with { Name = null! }]) &&
                    !DownloadVault.ValidIndex([previewEntry with { Bytes = -1 }]),
                    "vault index schema rejects duplicate or invalid authenticated records");
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
                foreach (int status in new[] { 408, 429, 500, 502, 503, 504, 401, 403, 404, 410, 501, 505 })
                {
                    bool allowed = status is 408 or 429 or 500 or 502 or 503 or 504;
                    Check(DownloadFailurePolicy.HttpStatus((System.Net.HttpStatusCode)status, true).Retryable == allowed &&
                        !DownloadFailurePolicy.HttpStatus((System.Net.HttpStatusCode)status, false).Retryable,
                        $"retry HTTP status policy ({status})");
                }
                foreach (string address in new[] { "https://user:pass@example.test/file.bin",
                    "https://example.test/file.bin#fragment", "https://example.test/file.bin?token=synthetic",
                    "http://example.test/file.bin", "file:///synthetic.bin",
                    "https://example.test/" + new string('a', 8192) })
                    Check(!DownloadFailurePolicy.SafeRetryAddress(new Uri(address)), "unsafe retry address rejected");

                Check(!DownloadFailurePolicy.Describe(new IOException("synthetic-secret"), DownloadPhase.Streaming, true)
                    .Contains("synthetic-secret"), "download failure detail does not expose raw exceptions");
                Check(DownloadFailurePolicy.Describe(DownloadVault.SealFailure("err\tStorageFull: synthetic-secret"),
                    DownloadPhase.Publishing, true) == "저장 공간 부족 · 보관 목록 확인 필요" &&
                    DownloadVault.SealFailure("err\tOther: StorageFull") is not VaultStorageFullException,
                    "Rust storage-full error classified without raw text or unsafe retry");
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
                Check(ResolveAddress("user@example.test") is null &&
                    ResolveAddress("example.test:invalid") is null &&
                    ResolveAddress(@"C:\private notes\file.txt") is null &&
                    ResolveAddress("? user@example.test", "google")?.Host == "www.google.com",
                    "invalid address is not an implicit search; explicit search remains available");

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
                    Check(Active?.ManagementSurface is not null && Active.EngineView.Element.Visibility == System.Windows.Visibility.Hidden,
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
                    long beforeCloseEpoch = closingSettings.NavigationEpoch;
                    var settingsClose = CloseTabAsync(closingSettings.Item);
                    Check(closingSettings.CancelManagement is null && closingSettings.ManagementSurface is null,
                        "closing native manager cancels its action before animation completes");
                    Check(closingSettings.Closing && closingSettings.NavigationEpoch > beforeCloseEpoch &&
                        !AcceptTabEvents(closingSettings) && !CanUseTab(closingSettings.Item), "close invalidates tab before animation");
                    await CloseTabAsync(closingSettings.Item);
                    await settingsClose;
                    Check(!Tabs.Items.Contains(closingSettings.Item) && !AcceptTabEvents(closingSettings),
                        "duplicate close leaves no live tab or accepted events");
                    await ReadyAt("/");
                    await WaitFor(() => !session.SettingsUiOpen && !session.PermissionUiOpen, "closing settings releases manager guards");
                    var selectedBeforeBackground = Active;
                    var backgroundSettings = await OpenTab("veil://settings", select: false)
                        ?? throw new InvalidOperationException("background settings tab did not open");
                    Check(Active == selectedBeforeBackground && backgroundSettings.ManagementSurface is not null &&
                        backgroundSettings.CancelManagement is null &&
                        backgroundSettings.ManagementSurface.Visibility == Visibility.Collapsed &&
                        backgroundSettings.EngineView.Element.Visibility == Visibility.Hidden &&
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
                    Check(BrowserData.Load(statePath).History.SequenceEqual(data.History),
                        "successful history commit persisted shared state");
                    var completedTab = Active!;
                    if (completedTab.CompletedNavigationId is ulong completedId)
                    {
                        int historyBeforeDuplicate = data.History.Count;
                        OnNavigationCompleted(completedTab, new EngineNavigationCompletedEventArgs(completedId,
                            true, false, new EngineNavigationError(EngineErrorDomain.WebView2, 0, "Synthetic duplicate")));
                        Check(data.History.Count == historyBeforeDuplicate,
                            "duplicate completion did not record another visit");
                    }
                    else throw new InvalidOperationException("Navigation completion identity not observed.");
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
#if DEBUG
                        other.smokeFailTabTransfer = true;
                        try
                        {
                            Check(!other.MoveToExistingWindow(moving.Item, this, 0) &&
                                moving.Owner == other && other.Tabs.Items.Contains(moving.Item) &&
                                !Tabs.Items.Contains(moving.Item) && session.Windows.Contains(other),
                                "failed transfer restores original window and tab");
                            Check(await moving.View.ExecuteScriptAsync("window.veilMarker") == "42" &&
                                BrowserData.Load(statePath).Windows is [{ Tabs.Count: 1 }],
                                "failed transfer preserves live page and saved layout");
                        }
                        finally { other.smokeFailTabTransfer = false; }
#endif
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
                        Check(tornWindow.MoveToExistingWindow(torn, this, Tabs.Items.Count), "move torn-off tab back");
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
#if DEBUG
                else if (stage == "vault-retry-https")
                {
                    var https = new Uri(Environment.GetEnvironmentVariable("VEIL_TEST_RETRY_ORIGIN") ?? "");
                    Check(https.Scheme == "https" && https.Host == "127.0.0.1" &&
                        https.AbsolutePath == "/" && https.UserInfo.Length == 0 && https.Query.Length == 0 &&
                        https.Fragment.Length == 0, "HTTPS retry fixture restricted to loopback");
                    string authority = https.GetLeftPart(UriPartial.Authority);
                    var vault = session.Vault;
                    Check(vault.Available && await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 0,
                        "new synthetic retry vault opened");
                    int prompts = 0;
                    bool consent = true;
                    UserPrompts.SmokeConfirm = (caption, _) =>
                    {
                        Check(caption == "다운로드 다시 시도", "native retry confirmation");
                        prompts++;
                        return consent;
                    };
                    try
                    {
                        async Task Go(string path)
                        {
                            NavigateAddress(authority + path);
                            await WaitForScript($"location.href === {JsonSerializer.Serialize(authority + path)} && document.readyState === 'complete'",
                                "HTTPS fixture page loaded");
                        }
                        async Task Start(string kind, bool offered)
                        {
                            await Go("/source/" + kind);
                            int before = vault.Results.Count;
                            await Active!.View.ExecuteScriptAsync($"location.href = '/retry-{kind}.bin'; null");
                            await WaitFor(() => vault.Results.Count == before + 1 && session.ActiveDownloads.Count == 0,
                                $"initial capture finished ({kind})");
                            var result = vault.Results[^1];
                            Check(result.Outcome == DownloadVault.Failed && result.ObjectPath is null &&
                                result.Detail?.Contains(kind == "forbidden" ? "403" : "503") == true,
                                $"initial HTTPS error without publication ({kind})");
                            UpdateDownloadToolbar();
                            Check((retryDownload is not null) == offered &&
                                (RetryDownloadButton.Visibility == Visibility.Visible) == offered,
                                $"retry offer policy ({kind})");
                        }
                        void ClickRetry()
                        {
                            RetryDownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        async Task Retry(string kind, string expected)
                        {
                            int before = vault.Results.Count, count = prompts;
                            consent = true;
                            DownloadToolbarClick(DownloadButton, new RoutedEventArgs());
                            Check(DownloadPopup.IsOpen, "recent downloads popup");
                            ClickRetry();
                            ClickRetry(); // candidate was consumed before confirmation or capture
                            await WaitFor(() => vault.Results.Count == before + 1 && session.ActiveDownloads.Count == 0,
                                $"explicit HTTPS retry finished ({kind})");
                            Check(prompts == count + 1 && retryDownload is null && vault.Results[^1].Outcome == expected,
                                $"single confirmation and no retry chain ({kind})");
                            Check((vault.Results[^1].ObjectPath is not null) == (expected == DownloadVault.Stored),
                                $"only successful retry publishes ({kind})");
                        }

                        await Start("ok", true);
                        await Retry("ok", DownloadVault.Stored);
                        Check(vault.Entries.Count == 1 && vault.Entries[0].Name == "veil-retry-ok.bin" &&
                            await vault.VerifyAsync(vault.Entries[0]) == 1024, "retry publishes exactly one authenticated object");
                        await Start("decline", true);
                        int declineCount = prompts, declineResults = vault.Results.Count;
                        consent = false;
                        ClickRetry();
                        ClickRetry();
                        Check(prompts == declineCount + 1 && retryDownload is null &&
                            vault.Results.Count == declineResults && session.ActiveDownloads.Count == 0,
                            "declined retry consumes candidate without transfer");
                        await Start("repeat", true);
                        await Retry("repeat", DownloadVault.Failed);
                        await Start("redirect", true);
                        await Retry("redirect", DownloadVault.Failed);
                        Check(vault.Results[^1].Detail?.Contains("리디렉션") == true, "retry redirect rejected");
                        await Start("forbidden", false);
                        await Start("expired", true);
                        retryDownload = retryDownload! with { Expires = DateTimeOffset.UtcNow.AddSeconds(-1) };
                        UpdateDownloadToolbar();
                        Check(retryDownload is null, "expired candidate removed");
                        await Start("navigate", true);
                        await Go("/source/changed");
                        Check(retryDownload is null, "navigation invalidates candidate");

                        // Hold the single capture slot with a cancellable synthetic transfer. The native
                        // retry click passes its first context checks but must not issue a queued replay.
                        await Start("queued", true);
                        using (var hold = new BrowserDownload("veil-retry-hold.bin"))
                        {
                            var holdTask = vault.CaptureAsync(new Uri(authority + "/hold.bin"), hold, Active!.View.CoreWebView2);
                            try
                            {
                                await WaitFor(() => hold.Bytes > 0, "capture slot occupied");
                                int before = vault.Results.Count;
                                consent = true;
                                ClickRetry();
                                await WaitFor(() => session.ActiveDownloads.Count == 1 &&
                                    session.ActiveDownloads[0].Phase == DownloadPhase.Waiting, "retry queued behind capture");
                                await Go("/source/queued-changed");
                                hold.Cancel();
                                try { await holdTask.WaitAsync(TimeSpan.FromSeconds(45)); }
                                catch (OperationCanceledException) when (hold.Token.IsCancellationRequested) { }
                                Check(holdTask.IsCanceled, "slot holder cancelled");
                                await WaitFor(() => vault.Results.Count == before + 1 && session.ActiveDownloads.Count == 0,
                                    "queued retry rejected after source changed");
                                Check(vault.Results[^1].Outcome == DownloadVault.Failed && vault.Results[^1].ObjectPath is null &&
                                    vault.Results[^1].Detail?.Contains("원래 다운로드 페이지 변경") == true,
                                    "live context required immediately before retry HTTP request");
                            }
                            finally
                            {
                                hold.Cancel();
                                try { await holdTask.WaitAsync(TimeSpan.FromSeconds(45)); }
                                catch (OperationCanceledException) when (hold.Token.IsCancellationRequested) { }
                            }
                        }
                        Check(vault.Entries.Count == 1, "no duplicate or failed objects in index");
                        vault.Lock();
                        Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 1 &&
                            await vault.VerifyAsync(vault.Entries[0]) == 1024, "single authenticated object persists after reopen");
                        File.WriteAllText(Path.ChangeExtension(report, ".vault.json"), JsonSerializer.Serialize(new
                        {
                            vault = vault.Directory,
                            results = vault.Results,
                        }));
                    }
                    finally { UserPrompts.SmokeConfirm = null; }
                }
#endif
#if DEBUG
                else if (stage == "vault-publication-faults")
                {
                    // Fresh synthetic vault only. Obstruct one directory at a time, preserving the
                    // original ciphertext. This tests filesystem write failure, not actual ENOSPC.
                    var vault = session.Vault;
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 0,
                        "fresh publication test vault");
                    NavigateAddress(origin);
                    await ReadyAt("/");
                    async Task<VaultResult> Capture(string kind, string name)
                    {
                        int before = vault.Results.Count;
                        await CaptureAsync(Active!.View.CoreWebView2, new Uri(origin + "/vault-dl?kind=" + kind), name, 0, 0);
                        Check(vault.Results.Count == before + 1 && session.ActiveDownloads.Count == 0,
                            "native capture completes once");
                        return vault.Results[^1];
                    }
                    string Digest(string path)
                    {
                        using var input = File.OpenRead(path);
                        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
                    }
                    // Reopen after HTTP headers but before any vault frame is sent.
                    // A stale request must never publish in the replacement session.
                    bool reopened = false;
                    vault.SmokeAfterResponse = async () =>
                    {
                        vault.Lock();
                        reopened = await vault.TryOpenFromDevelopmentEnvironmentAsync();
                    };
                    try
                    {
                        var stale = await Capture("small", "stale-vault-session.bin");
                        Check(reopened && stale.Outcome == DownloadVault.Failed && stale.ObjectPath is null &&
                            stale.Detail?.Contains("금고 세션 변경") == true && vault.Entries.Count == 0,
                            "request from previous vault session rejected without publication");
                    }
                    finally { vault.SmokeAfterResponse = null; }
                    var seed = await Capture("small", "publication-seed.bin");
                    Check(seed.Outcome == DownloadVault.Stored && vault.Entries.Count == 1, "seed published");
                    var original = vault.Entries.Single();
                    string originalPath = vault.ObjectPath(original);
                    string originalHash = Digest(originalPath);
                    string objects = Path.Combine(vault.Directory, "objects");
                    string index = Path.Combine(vault.Directory, "index");
                    string meta = Path.Combine(vault.Directory, "meta");
                    string[] ObjectIds() => Directory.GetFiles(objects, "*.veil")
                        .Select(path => Path.GetFileNameWithoutExtension(path)).Order().ToArray();
                    // Lock while a binary body frame is in progress, before the commit frame.
                    int interruptedFrames = 0;
                    vault.SmokeAfterFrame = () =>
                    {
                        interruptedFrames++;
                        vault.Lock();
                    };
                    try
                    {
                        var interrupted = await Capture("large", "interrupted-vault-session.bin");
                        Check(interruptedFrames == 1 && interrupted.Outcome == DownloadVault.Failed &&
                            interrupted.ObjectPath is null && !vault.Unlocked && vault.Entries.Count == 0,
                            "locking during frame transfer refuses publication");
                    }
                    finally { vault.SmokeAfterFrame = null; }
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() &&
                        vault.Entries.Count == 1 && ObjectIds().Length == 1 &&
                        Digest(originalPath) == originalHash && await vault.VerifyAsync(vault.Entries.Single()) == 1024 &&
                        !Directory.GetFiles(vault.Directory, ".veil-encrypted-*", SearchOption.AllDirectories).Any(),
                        "interrupted frame leaves original intact and staging is cleaned after reopen");
                    // Exercise the deadline with the real helper, without consuming its
                    // bounded read reply. The test barrier itself is cancellable.
                    vault.SmokeCommandTimeoutSeconds = 1;
                    vault.SmokeBeforeBoundedReply = token => Task.Delay(Timeout.Infinite, token);
                    bool timedOut = false;
                    try { await vault.LoadIndexAsync(); }
                    catch (IOException timeoutError) when (timeoutError.Message == "Vault pipe timeout.") { timedOut = true; }
                    finally
                    {
                        vault.SmokeBeforeBoundedReply = null;
                        vault.SmokeCommandTimeoutSeconds = null;
                    }
                    Check(timedOut && !vault.Unlocked && vault.Entries.Count == 0,
                        "bounded command timeout closes helper session");
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() &&
                        vault.Entries.Count == 1 && ObjectIds().Length == 1 &&
                        Digest(originalPath) == originalHash && await vault.VerifyAsync(vault.Entries.Single()) == 1024,
                        "timeout preserves authenticated original and permits explicit reopening");
                    vault.Lock();
                    bool openingInterrupted = false;
                    vault.SmokeBeforeIndexPublish = () =>
                    {
                        Check(!vault.Unlocked && vault.Entries.Count == 0,
                            "vault not advertised as unlocked during index recovery");
                        vault.Lock();
                    };
                    try { await vault.TryOpenFromDevelopmentEnvironmentAsync(); }
                    catch (IOException) { openingInterrupted = true; }
                    finally { vault.SmokeBeforeIndexPublish = null; }
                    Check(openingInterrupted && !vault.Unlocked && vault.Entries.Count == 0,
                        "lock during index recovery cannot publish stale entries or report successful open");
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() &&
                        vault.Entries.Count == 1 && Digest(originalPath) == originalHash &&
                        await vault.VerifyAsync(vault.Entries.Single()) == 1024,
                        "explicit reopen restores original after interrupted index recovery");
                    async Task<string> Fault(string directory, string kind, string name)
                    {
                        var before = ObjectIds();
                        var indexes = Directory.GetFiles(index, "*.veil").ToDictionary(path => Path.GetFileName(path)!, Digest);
                        string saved = directory + "-publication-test";
                        Check(!Directory.Exists(saved) && !File.Exists(saved), "fresh fault path");
                        Directory.Move(directory, saved);
                        bool obstructed = false;
                        try
                        {
                            using (var marker = new FileStream(directory, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                marker.WriteByte(0x52);
                            obstructed = true;
                            var result = await Capture(kind, name);
                            Check(result.Outcome == DownloadVault.Failed && result.ObjectPath is null && retryDownload is null &&
                                result.Detail?.Contains("보관 목록 확인 필요") == true,
                                "publication failure reported without automatic retry");
                            Check(!vault.Unlocked && vault.Entries.Count == 0, "uncertain metadata write locks vault");
                            Check(Digest(originalPath) == originalHash, "original encrypted object unchanged");
                            string previousIndex = directory == index ? saved : index;
                            Check(indexes.All(pair => Digest(Path.Combine(previousIndex, pair.Key)) == pair.Value) &&
                                Directory.GetFiles(previousIndex, "*.veil").Length == indexes.Count,
                                "old authenticated index generations unchanged");
                            string[] added = ObjectIds().Except(before).ToArray();
                            Check(added.Length == 1 && before.All(id => ObjectIds().Contains(id)),
                                "only one completed encrypted body retained for recovery");
                            return added.Single();
                        }
                        finally
                        {
                            if (obstructed) File.Delete(directory);
                            Directory.Move(saved, directory);
                        }
                    }
                    string orphan = await Fault(meta, "redirect", "publication-metadata-failure.bin");
                    Check(!File.Exists(Path.Combine(meta, orphan + ".veil")), "orphan has no published metadata");
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 2,
                        "reopen rebuilds index after metadata failure");
                    var recovered = vault.Entries.Single(entry => entry.Id == orphan);
                    Check(recovered.Recovered && await vault.VerifyAsync(recovered) == 2048 &&
                        await vault.VerifyAsync(vault.Entries.Single(entry => entry.Id == original.Id)) == 1024,
                        "orphan and original authenticate; missing filename not invented");
                    string indexed = await Fault(index, "large", "publication-index-failure.bin");
                    Check(File.Exists(Path.Combine(meta, indexed + ".veil")), "metadata retained after index failure");
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 3,
                        "reopen rebuilds index after index failure");
                    var fromMeta = vault.Entries.Single(entry => entry.Id == indexed);
                    Check(!fromMeta.Recovered && fromMeta.Name == "publication-index-failure.bin" &&
                        fromMeta.Bytes == 8L * 1024 * 1024 && await vault.VerifyAsync(fromMeta) == fromMeta.Bytes,
                        "metadata and authenticated content recovered");
                    var next = await Capture("small", "publication-after-recovery.bin");
                    Check(next.Outcome == DownloadVault.Stored && vault.Entries.Count == 4 && Digest(originalPath) == originalHash,
                        "subsequent explicit download works without changing original");
                    vault.Lock();
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 4,
                        "rebuilt index persists across reopen");
                    Check(vault.Entries.Select(entry => entry.Id).Distinct().Count() == 4 &&
                        ObjectIds().Length == 4 && !Directory.GetFiles(vault.Directory, ".veil-encrypted-*", SearchOption.AllDirectories).Any(),
                        "exact encrypted inventory, no duplicate or staging remnants");
                    var cacheBefore = Directory.GetFiles(index, "*.veil").ToDictionary(path => Path.GetFileName(path)!, Digest);
                    vault.SmokeIndexCacheLimit = 1;
                    try
                    {
                        var beyondCache = await Capture("small", "index-cache-limit.bin");
                        Check(beyondCache.Outcome == DownloadVault.Stored && vault.Entries.Count == 5 &&
                            vault.Unlocked && cacheBefore.Count == Directory.GetFiles(index, "*.veil").Length &&
                            cacheBefore.All(pair => Digest(Path.Combine(index, pair.Key)) == pair.Value),
                            "oversized index cache is skipped without discarding completed download");
                        vault.Lock();
                        Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 5 &&
                            cacheBefore.Count == Directory.GetFiles(index, "*.veil").Length &&
                            cacheBefore.All(pair => Digest(Path.Combine(index, pair.Key)) == pair.Value),
                            "reopen rebuilds oversized list without rewriting old index");
                        var cachedItem = vault.Entries.Single(e => e.Name == "index-cache-limit.bin");
                        Check(await vault.VerifyAsync(cachedItem) == 1024 && Digest(originalPath) == originalHash,
                            "uncached item and original remain authenticated");
                        await vault.DeleteAsync(cachedItem);
                        Check(vault.Entries.Count == 4 && ObjectIds().Length == 4, "uncached item can be deleted");
                    }
                    finally { vault.SmokeIndexCacheLimit = null; }
                    // Exercise a failure outside the Rust write itself, after body and metadata commit.
                    vault.SmokeBeforePublication = phase => phase == "index"
                        ? Task.FromException(new UnauthorizedAccessException("synthetic-publication-denied"))
                        : Task.CompletedTask;
                    try
                    {
                        var denied = await Capture("small", "publication-access-denied.bin");
                        Check(denied.Outcome == DownloadVault.Failed && denied.ObjectPath is null &&
                            denied.Detail == "금고 저장소 접근 권한 오류 · 보관 목록 확인 필요" &&
                            retryDownload is null && !vault.Unlocked && vault.Entries.Count == 0 &&
                            ObjectIds().Length == 5 && Digest(originalPath) == originalHash,
                            "post-commit access failure invalidates session and requires inventory check");
                    }
                    finally { vault.SmokeBeforePublication = null; }
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 5,
                        "post-commit access failure recovers complete item");
                    var deniedItem = vault.Entries.Single(e => e.Name == "publication-access-denied.bin");
                    Check(await vault.VerifyAsync(deniedItem) == 1024 && !deniedItem.Recovered &&
                        Digest(originalPath) == originalHash, "metadata and both objects authenticate after recovery");
                    await vault.DeleteAsync(deniedItem);
                    Check(vault.Entries.Count == 4 && ObjectIds().Length == 4, "disposable failure fixture removed");
                    // Deletion is tested with an extra disposable object. Keep the
                    // four original publication-test objects and evidence intact.
                    var extra = await Capture("small", "deletion-failure.bin");
                    Check(extra.Outcome == DownloadVault.Stored && vault.Entries.Count == 5, "deletion fixture published");
                    var victim = vault.Entries.Single(e => e.Name == "deletion-failure.bin");
                    string victimObject = vault.ObjectPath(victim);
                    string victimMeta = Path.Combine(meta, victim.Id + ".veil");
                    bool refusedObjectDelete = false;
                    using (File.Open(victimObject, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        try { await vault.DeleteAsync(victim); }
                        catch (IOException) { refusedObjectDelete = true; }
                    }
                    Check(refusedObjectDelete && !vault.Unlocked && vault.Entries.Count == 0 &&
                        File.Exists(victimObject) && File.Exists(victimMeta), "object delete failure invalidates session without deleting metadata");
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() &&
                        vault.Entries.Count == 5 && await vault.VerifyAsync(vault.Entries.Single(e => e.Id == victim.Id)) == 1024,
                        "failed object deletion preserves authenticated object on reopen");
                    victim = vault.Entries.Single(e => e.Id == victim.Id);
                    bool refusedMetaDelete = false;
                    using (File.Open(victimMeta, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        try { await vault.DeleteAsync(victim); }
                        catch (IOException) { refusedMetaDelete = true; }
                    }
                    Check(refusedMetaDelete && !vault.Unlocked && vault.Entries.Count == 0 &&
                        !File.Exists(victimObject) && File.Exists(victimMeta), "partial deletion invalidates session");
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 4 &&
                        !vault.Entries.Any(e => e.Id == victim.Id) && ObjectIds().Length == 4 &&
                        Digest(originalPath) == originalHash, "reopening reconciles index without resurrecting deleted object");
                    // Orphan metadata is encrypted and intentionally not automatically removed.
                    var verified = new List<object>();
                    foreach (var entry in vault.Entries)
                        verified.Add(new { entry.Id, entry.Name, entry.Recovered, bytes = await vault.VerifyAsync(entry) });
                    File.WriteAllText(Path.ChangeExtension(report, ".vault.json"), JsonSerializer.Serialize(new { entries = verified }));
                }
#endif
#if DEBUG
                else if (stage is "vault-power-arm" or "vault-power-check")
                {
                    var vault = session.Vault;
                    bool arming = stage == "vault-power-arm";
                    Check(vault.Available && vault.Exists != arming, "expected synthetic vault initial state");
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync(), "synthetic power loss vault opened");
                    NavigateAddress(origin);
                    await ReadyAt("/");
                    async Task<VaultEntry> Store(string kind, string name)
                    {
                        using var download = new BrowserDownload(name);
                        return await vault.CaptureAsync(new Uri(origin + "/vault-dl?kind=" + kind), download, Active!.View.CoreWebView2);
                    }
                    string Digest(string path)
                    {
                        using var stream = File.OpenRead(path);
                        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    }
                    string Meta(VaultEntry entry) => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(vault.ObjectPath(entry))!)!,
                        "meta", entry.Id + ".veil");
                    string[] Objects(VaultEntry entry) => Directory.GetFiles(Path.GetDirectoryName(vault.ObjectPath(entry))!, "*.veil");
                    string[] Staging() => Directory.GetFiles(vault.Directory, ".veil-encrypted-*", SearchOption.AllDirectories);
                    if (arming)
                    {
                        Check(vault.Entries.Count == 0, "fresh vault");
                        var seed = await Store("small", "power-seed.bin");
                        Check(await vault.VerifyAsync(seed) == 1024 && Objects(seed).Length == 1, "seed authenticated and published");
                        string seedHash = Digest(vault.ObjectPath(seed)), metaHash = Digest(Meta(seed));
                        using var pending = new BrowserDownload("power-uncommitted.bin");
                        var transfer = vault.CaptureAsync(new Uri(origin + "/vault-dl?kind=slow-power"), pending, Active!.View.CoreWebView2);
                        try
                        {
                            for (int i = 0; i < 600 && !transfer.IsCompleted && pending.Bytes < 1024 * 1024; i++)
                                await Task.Delay(100);
                            Check(!transfer.IsCompleted && pending.Phase == DownloadPhase.Streaming &&
                                pending.Bytes >= 1024 * 1024 && pending.Bytes < 32L * 1024 * 1024 &&
                                Staging().Length > 0 && Objects(seed).Length == 1, "uncommitted staging observed while streaming");
                            File.WriteAllText(report, JsonSerializer.Serialize(new
                            {
                                vaultPowerReady = true, seedId = seed.Id, seedHash, metaHash,
                                streamed = pending.Bytes, stagingObserved = true,
                            }));
                            await Task.WhenAny(transfer, Task.Delay(TimeSpan.FromSeconds(150)));
                            throw new InvalidOperationException("Power cut not observed during stream");
                        }
                        finally
                        {
                            pending.Cancel();
                            try { await transfer; }
                            catch (Exception failure) when (RecoverableFailure.Is(failure)) { }
                        }
                    }
                    else
                    {
                        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(report)!, "armed.json")));
                        var proof = receipt.RootElement;
                        Check(vault.Entries.Count == 1 && vault.Entries[0].Id == proof.GetProperty("seedId").GetString(), "only committed seed restored");
                        var seed = vault.Entries.Single();
                        Check(seed.Name == "power-seed.bin" && !seed.Recovered && seed.Bytes == 1024 &&
                            await vault.VerifyAsync(seed) == 1024 && Digest(vault.ObjectPath(seed)) == proof.GetProperty("seedHash").GetString() &&
                            Digest(Meta(seed)) == proof.GetProperty("metaHash").GetString(), "vault power seed authenticated and unchanged");
                        Check(Objects(seed).Length == 1 && Staging().Length == 0, "vault power interrupted object absent and staging cleaned");
                        var retry = await Store("large", "power-explicit-retry.bin");
                        Check(vault.Entries.Count == 2 && Objects(seed).Length == 2 && retry.Id != seed.Id &&
                            await vault.VerifyAsync(retry) == 8L * 1024 * 1024 && Digest(vault.ObjectPath(seed)) == proof.GetProperty("seedHash").GetString(),
                            "vault power explicit retry authenticated without duplicate");
                    }
                }
                else if (stage == "vault-bridge-disk-full")
                {
                    // External runner verifies the offline VM and separate small VHDX. This Debug
                    // stage never fills a disk itself; browser and evidence stay on the primary lab.
                    string small = Environment.GetEnvironmentVariable("VEIL_TEST_DISK_FULL_VAULT") ?? "";
                    Check(Path.IsPathFullyQualified(small) && Directory.Exists(small) &&
                        !string.Equals(Path.GetPathRoot(small), Path.GetPathRoot(session.Root), StringComparison.OrdinalIgnoreCase) &&
                        !Directory.EnumerateFileSystemEntries(small).Any(), "separate fresh synthetic vault root");
                    using var vault = new DownloadVault(small);
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync(), "disk-full bridge vault opened");
                    NavigateAddress(origin);
                    await ReadyAt("/");
                    async Task<VaultEntry> Store(string kind, string name)
                    {
                        using var download = new BrowserDownload(name);
                        return await vault.CaptureAsync(new Uri(origin + "/vault-dl?kind=" + kind), download, Active!.View.CoreWebView2);
                    }
                    var seed = await Store("small", "disk-full-seed.bin");
                    string originalPath = vault.ObjectPath(seed);
                    string Digest(string path)
                    {
                        using var stream = File.OpenRead(path);
                        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    }
                    string originalHash = Digest(originalPath);
                    string objects = Path.GetDirectoryName(originalPath)!;
                    string activeGeneration = Path.GetDirectoryName(objects)!;
                    string meta = Path.Combine(activeGeneration, "meta"), index = Path.Combine(activeGeneration, "index");
                    string[] Before(string directory) => Directory.GetFiles(directory, "*.veil").Order().ToArray();
                    string[] Inventory() => Before(objects).Concat(Before(meta)).Concat(Before(index)).Order().ToArray();
                    Check(Before(objects).Length == 1 && Before(meta).Length == 1 && Before(index).Length >= 1,
                        "seed object, metadata and index exist before fault injection");
                    var originals = Inventory().ToDictionary(path => path, Digest);
                    string controls = Path.GetDirectoryName(report)!;
                    async Task WaitSignal(string name)
                    {
                        for (int i = 0; i < 1800; i++)
                        {
                            if (File.Exists(Path.Combine(controls, name))) return;
                            await Task.Delay(100);
                        }
                        throw new TimeoutException("disk-full signal missing: " + name);
                    }
                    string publication = Environment.GetEnvironmentVariable("VEIL_TEST_DISK_FULL_PUBLICATION") ?? "";
                    Check(publication is "" or "meta" or "index", "known disk-full scenario");
                    if (publication.Length != 0)
                    {
                        string? retainedId = null;
                        int barriers = 0;
                        async Task BeforePublication(string phase)
                        {
                            if (phase != publication) return;
                            barriers++;
                            var added = Before(objects).Except(originals.Keys).ToArray();
                            Check(added.Length == 1 && Before(meta).Length == (phase == "meta" ? 1 : 2),
                                "body committed before requested publication boundary");
                            retainedId = Path.GetFileNameWithoutExtension(added.Single());
                            File.WriteAllText(Path.Combine(controls, "seed-ready"), phase);
                            await WaitSignal("filled");
                        }
                        vault.SmokeBeforePublication = BeforePublication;
                        try
                        {
                            using var failed = new BrowserDownload("disk-full-publication.bin");
                            bool refused = false;
                            try { _ = await vault.CaptureAsync(new Uri(origin + "/vault-dl?kind=large"), failed, Active!.View.CoreWebView2); }
                            catch (VaultStorageFullException storageError)
                            {
                                refused = true;
                                Check(failed.Phase == DownloadPhase.Publishing &&
                                    DownloadFailurePolicy.Describe(storageError, failed.Phase, true) == "저장 공간 부족 · 보관 목록 확인 필요",
                                    "actual storage-full publication failure classified");
                            }
                            Check(refused && barriers == 1 && retainedId is not null, "target publication failed with actual storage-full reply");
                        }
                        finally { vault.SmokeBeforePublication = null; }
                        Check(!vault.Unlocked && vault.Entries.Count == 0, "uncertain publication locked the vault");
                        Check(originals.All(pair => File.Exists(pair.Key) && Digest(pair.Key) == pair.Value) &&
                            Before(index).SequenceEqual(originals.Keys.Where(p => Path.GetDirectoryName(p) == index).Order()) &&
                            Before(objects).Length == 2 && Before(meta).Length == (publication == "meta" ? 1 : 2) &&
                            !Directory.GetFiles(vault.Directory, ".veil-encrypted-*", SearchOption.AllDirectories).Any(),
                            "old index and original ciphertext preserved; only completed encrypted body retained");
                        File.WriteAllText(Path.Combine(controls, "failure-checked"), "checked");
                        await WaitSignal("released");
                        Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 2,
                            "reopen rebuilt authenticated inventory");
                        var recovered = vault.Entries.Single(e => e.Id == retainedId);
                        Check(recovered.Recovered == (publication == "meta") &&
                            (publication == "meta" || recovered.Name == "disk-full-publication.bin") &&
                            await vault.VerifyAsync(recovered) == 8L * 1024 * 1024 &&
                            await vault.VerifyAsync(vault.Entries.Single(e => e.Id == seed.Id)) == 1024 &&
                            Digest(originalPath) == originalHash, "recovered object and original authenticate without inventing metadata");
                        var nextPublication = await Store("small", "disk-full-after-publication.bin");
                        Check(vault.Entries.Count == 3 && Before(objects).Length == 3 &&
                            vault.Entries.Select(e => e.Id).Distinct().Count() == 3 && await vault.VerifyAsync(nextPublication) == 1024,
                            "subsequent explicit request stored once");
                        File.WriteAllText(Path.ChangeExtension(report, ".vault.json"), JsonSerializer.Serialize(new
                        {
                            entries = vault.Entries, originalUnchanged = true, publication,
                        }));
                    }
                    else
                    {
                        File.WriteAllText(Path.Combine(controls, "seed-ready"), "ready");
                        await WaitSignal("filled");
                        using (var failed = new BrowserDownload("disk-full-failed.bin"))
                        {
                            bool refused = false;
                            try { _ = await vault.CaptureAsync(new Uri(origin + "/vault-dl?kind=large"), failed, Active!.View.CoreWebView2); }
                            catch (VaultStorageFullException storageError)
                            {
                                refused = true;
                                Check(DownloadFailurePolicy.Describe(storageError, failed.Phase, true).StartsWith("저장 공간 부족"),
                                    "fixed disk-full failure reason");
                            }
                            Check(refused, "actual Rust storage-full reply reached desktop bridge");
                        }
                        Check(vault.Entries.Count == 1 && originals.Keys.Order().SequenceEqual(Inventory()) &&
                            originals.All(pair => Digest(pair.Key) == pair.Value) &&
                            !Directory.GetFiles(vault.Directory, ".veil-encrypted-*", SearchOption.AllDirectories).Any(),
                            "full-volume failure publishes no new object, metadata, index or staging");
                        Check(await vault.VerifyAsync(seed) == 1024, "existing object still authenticates while full");
                        File.WriteAllText(Path.Combine(controls, "failure-checked"), "checked");
                        await WaitSignal("released");
                        vault.Lock();
                        Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 1 &&
                            await vault.VerifyAsync(vault.Entries.Single()) == 1024, "reopen after freeing space");
                        var next = await Store("large", "disk-full-explicit-retry.bin");
                        Check(vault.Entries.Count == 2 && await vault.VerifyAsync(next) == 8L * 1024 * 1024 &&
                            Digest(originalPath) == originalHash && Before(objects).Length == 2 && Before(meta).Length == 2,
                            "explicit download after space restored authenticates, no duplicate");
                        File.WriteAllText(Path.ChangeExtension(report, ".vault.json"), JsonSerializer.Serialize(new
                        {
                            entries = vault.Entries,
                            originalUnchanged = true,
                        }));
                    }
                }
#endif
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
                    foreach (string invalidHost in new[] { "proxy.example:80", "proxy.example;direct://",
                        "proxy.example,direct", "proxy.example\\direct", "proxy.example@host",
                        "proxy.example%20", "proxy.example=direct", "proxy.example\"", "[::1]:80",
                        "fe80::1%3", "bad..example", "-proxy.example", "proxy-.example" })
                    {
                        var invalidProxy = new NetworkSettings(NetworkMode.Proxy, "http", invalidHost, 8080);
                        Check(invalidProxy.Problem is not null && invalidProxy.ProxyUri is null,
                            "ambiguous proxy host rejected");
                    }
                    foreach (var invalidNetwork in new[] {
                        new NetworkSettings((NetworkMode)99),
                        new NetworkSettings(NetworkMode.Proxy, "http", "proxy.example:80", 8080),
                        new NetworkSettings(DnsTemplate: "http://resolver.example/dns-query") })
                    {
                        bool engineRefused = false, handlerRefused = false;
                        try { _ = NetworkPolicy.BrowserArguments(invalidNetwork).ToArray(); }
                        catch (InvalidOperationException) { engineRefused = true; }
                        try { using var refused = NetworkPolicy.Handler(invalidNetwork); }
                        catch (InvalidOperationException) { handlerRefused = true; }
                        Check(engineRefused && handlerRefused, "invalid network choice rejected by engine and vault");
                    }
                    foreach (string host in new[] { "::1", "[::1]", "127.0.0.1", "proxy.example", "xn--bcher-kva.example" })
                    {
                        var validProxy = new NetworkSettings(NetworkMode.Proxy, "http", host, 80);
                        using var handler = NetworkPolicy.Handler(validProxy);
                        Check(validProxy.Problem is null && handler.UseProxy &&
                            handler.Proxy?.IsBypassed(new Uri("http://localhost/")) == false &&
                            NetworkPolicy.BrowserArguments(validProxy).First() ==
                            $"--proxy-server=http://{NetworkPolicy.CanonicalProxyHost(host)}:80",
                            "single canonical proxy with explicit port and no local bypass");
                    }
                    var savedProxy = new NetworkSettings(NetworkMode.Proxy, "socks5", "127.0.0.1", 9050);
                    var nextNetwork = new NetworkSettings(DnsTemplate: "https://dns.google/dns-query");
                    var networkData = new BrowserData { Network = savedProxy };
                    int networkWrites = 0, networkChecks = 0;
                    bool NetworkWrite() { networkWrites++; return true; }
                    Check(!networkData.TryCommitNetworkSettings(new NetworkSettings((NetworkMode)99), () => true, NetworkWrite) &&
                        !networkData.TryCommitNetworkSettings(new NetworkSettings(DnsTemplate: "http://dns.google/dns-query"), () => true, NetworkWrite) &&
                        !networkData.TryCommitNetworkSettings(new NetworkSettings(NetworkMode.Proxy, "http", "proxy.example;direct://", 80), () => true, NetworkWrite) &&
                        !networkData.TryCommitNetworkSettings(nextNetwork, () => false, NetworkWrite) &&
                        networkWrites == 0 && ReferenceEquals(networkData.Network, savedProxy),
                        "network commit rejects invalid or unauthorized choice without write");
                    Check(!networkData.TryCommitNetworkSettings(nextNetwork, () => ++networkChecks == 1, NetworkWrite) &&
                        networkWrites == 0 && ReferenceEquals(networkData.Network, savedProxy),
                        "network commit restores exact proxy choice when authority is revoked");
                    Check(!networkData.TryCommitNetworkSettings(NetworkSettings.Standard, () => true, () => false) &&
                        ReferenceEquals(networkData.Network, savedProxy),
                        "network commit refusal cannot replace saved proxy with direct choice");
                    bool networkException = false;
                    try
                    {
                        networkData.TryCommitNetworkSettings(nextNetwork, () => true,
                            () => throw new IOException("Synthetic network write failure"));
                    }
                    catch (IOException) { networkException = true; }
                    Check(networkException && ReferenceEquals(networkData.Network, savedProxy),
                        "network commit exception restores exact previous settings");
                    var appliedBefore = session.Network;
                    Check(networkData.TryCommitNetworkSettings(nextNetwork, () => true, NetworkWrite) &&
                        networkWrites == 1 && ReferenceEquals(networkData.Network, nextNetwork) &&
                        ReferenceEquals(session.Network, appliedBefore),
                        "network commit preserves DoH next-start choice without changing live network");
                    Check(networkData.TryCommitNetworkSettings(NetworkSettings.Standard, () => true, NetworkWrite) &&
                        networkWrites == 2 && networkData.Network is null &&
                        ReferenceEquals(session.Network, appliedBefore),
                        "network commit retains standard default encoding and live network");
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
                        Check(Active!.Url != ThreatWarningUrl && (Active.LastNavigationError is null or { Domain: EngineErrorDomain.WebView2, NativeCode: (int)Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.Unknown }),
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
#if DEBUG
                    string? expectedRedirect = Environment.GetEnvironmentVariable("VEIL_TEST_DOWNGRADE_TARGET");
                    if (!string.IsNullOrEmpty(expectedRedirect))
                    {
                        smokeExpectedHttpRedirect = new Uri(expectedRedirect);
                        Check(smokeExpectedHttpRedirect.Scheme == "http" && !NetworkPolicy.IsLocal(smokeExpectedHttpRedirect.Host) &&
                            session.HttpAllowed.Count == 0, "fresh public HTTP downgrade probe");
                        smokeRedirectTab = Active;
                        smokeCancelledHttpRedirects = 0;
                    }
#endif
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
#if DEBUG
                    if (smokeExpectedHttpRedirect is not null)
                    {
                        checks.Add($"visit: cancelledHttpRedirects={smokeCancelledHttpRedirects}");
                        checks.Add($"visit: pendingHttp={Active.PendingHttp?.AbsoluteUri ?? ""}");
                        checks.Add($"visit: nativeHttpBar={HttpsBar.Visibility == Visibility.Visible}");
                        checks.Add($"visit: httpExceptions={session.HttpAllowed.Count}");
                        smokeRedirectTab = null;
                        smokeExpectedHttpRedirect = null;
                    }
#endif
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
#if DEBUG
                else if (stage == "network-transition")
                {
                    Check(session.Network.Mode == NetworkMode.Proxy && session.Network.ProxyScheme == "http" &&
                        session.Network.ProxyHost == "127.0.0.1", "fixed loopback proxy mode");
                    var vault = session.Vault;
                    Check(await vault.TryOpenFromDevelopmentEnvironmentAsync() && vault.Entries.Count == 0,
                        "synthetic empty vault");
                    string controls = Path.GetDirectoryName(report)!;
                    uint pid = Active!.View.CoreWebView2.BrowserProcessId;
                    foreach (string phase in new[] { "before", "blocked", "after" })
                    {
                        string signal = Path.Combine(controls, phase + ".go");
                        for (int i = 0; !File.Exists(signal) && i < 3000; i++) await Task.Delay(100);
                        Check(File.Exists(signal), "operator/runner released " + phase);
                        Check(Active!.View.CoreWebView2.BrowserProcessId == pid, "same engine before " + phase);
                        string target = origin + "/network-transition/" + phase;
                        Active.LastNavigationError = null;
                        NavigateAddress(target);
                        string marker = JsonSerializer.Serialize("VEIL transition " + phase);
                        string title = "";
                        for (int i = 0; i < 400; i++)
                        {
                            await Task.Delay(100);
                            if (Active.Loading) continue;
                            title = await Active.View.ExecuteScriptAsync("document.title");
                            if (Active.LastNavigationError is not null || (Active.Url == target && title == marker)) break;
                        }
                        bool loaded = Active.Url == target && title == marker && Active.LastNavigationError is null;
                        bool blocked = Active.LastNavigationError is not null && !loaded;
                        Check(phase == "blocked" ? blocked : loaded, "navigation result " + phase);
                        int count = vault.Entries.Count;
                        bool stored = false;
                        string? failureName = null;
                        using (var download = new BrowserDownload("transition-" + phase + ".bin"))
                        {
                            try
                            {
                                var entry = await vault.CaptureAsync(new Uri(origin + "/vault-dl?kind=small&phase=" + phase), download,
                                    Active.View.CoreWebView2);
                                stored = entry.Bytes == 1024 && await vault.VerifyAsync(entry) == 1024;
                            }
                            catch (Exception failure) when (failure is System.Net.Http.HttpRequestException or TaskCanceledException)
                            { failureName = failure.GetType().Name; }
                        }
                        Check(phase == "blocked" ? !stored && failureName is not null && vault.Entries.Count == count :
                            stored && vault.Entries.Count == count + 1, "vault request result " + phase);
                        Check(Active.View.CoreWebView2.BrowserProcessId == pid, "same engine after " + phase);
                        File.WriteAllText(Path.Combine(controls, phase + ".done.json"), JsonSerializer.Serialize(new
                        {
                            phase, pid, loaded, blocked, stored, failureName, entries = vault.Entries.Count,
                        }));
                    }
                    Check(vault.Entries.Count == 2, "exactly two authenticated downloads across transition");
                }
#endif
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
                        int committed = max;
                        for (int i = 0; i < 400; i++)
                        {
                            if (int.TryParse(await Active!.View.ExecuteScriptAsync("window.stormGen"), out int gen) && gen >= max + 100)
                            {
                                committed = gen;
                                break;
                            }
                            await Task.Delay(75);
                        }
                        Check(committed >= max + 100, "writer confirmed at least 100 completed transactions before arming");
                        File.WriteAllText(report, JsonSerializer.Serialize(new
                        {
                            stormReady = true,
                            browserPid = Active!.View.CoreWebView2.BrowserProcessId,
                            before = max,
                            committed,
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
                    var duplicate = JsonSerializer.Deserialize<SecuritySettings>("{\"SmartScreen\":true,\"SmartScreen\":false}");
                    Check(duplicate is { SmartScreen: true, Recovered: true }, "duplicate SmartScreen choice restores safe default");
                    string listLab = Path.Combine(session.Root, "security-expiry-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(listLab);
                    try
                    {
                        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                        string[] keys = [Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())];
                        var clock = new SmokeThreatClock(DateTimeOffset.UtcNow);
                        var expiry = clock.GetUtcNow().AddMinutes(1);
                        string listPath = Path.Combine(listLab, LocalThreatList.FileName);
                        string hash = new string('a', 64);
                        void WriteList(string?[] hosts, DateTimeOffset issued)
                        {
                            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                Schema = 1, Issued = issued, Expires = expiry, Hosts = hosts, Files = new[] { hash },
                            });
                            File.WriteAllBytes(listPath, bytes);
                            File.WriteAllText(listPath + ".sig", Convert.ToBase64String(key.SignData(bytes,
                                System.Security.Cryptography.HashAlgorithmName.SHA256,
                                System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence)));
                        }
                        WriteList(["bad.test"], clock.GetUtcNow());
                        var (loaded, listStatus) = LocalThreatList.Load(listLab, keys, clock.GetUtcNow(), clock);
                        Check(listStatus.Valid && loaded is not null, "signed unexpired list loaded");
                        Check((await loaded!.CheckAsync(new Uri("https://bad.test/"), default)).Verdict == ThreatVerdict.Malicious &&
                            (await loaded.CheckFileAsync(hash, default)).Verdict == ThreatVerdict.Malicious, "valid list blocks host and file");
                        clock.Now = expiry;
                        Check((await loaded.CheckAsync(new Uri("https://bad.test/"), default)).Verdict == ThreatVerdict.Error &&
                            (await loaded.CheckFileAsync(hash, default)).Verdict == ThreatVerdict.Error, "running list expiry yields Error for host and file");
                        clock.Now = expiry.AddMinutes(-1);
                        WriteList(["bad.test", null], clock.GetUtcNow());
                        var (malformed, malformedStatus) = LocalThreatList.Load(listLab, keys, clock.GetUtcNow(), clock);
                        Check(!malformedStatus.Valid && malformed is not null &&
                            (await malformed.CheckAsync(new Uri("https://bad.test/"), default)).Verdict == ThreatVerdict.Error, "signed malformed entry rejects entire list");
                        WriteList(["bad.test"], expiry);
                        var (_, invalidDates) = LocalThreatList.Load(listLab, keys, clock.GetUtcNow(), clock);
                        Check(!invalidDates.Valid, "issued date must precede expiry");
                    }
                    finally { Directory.Delete(listLab, recursive: true); }
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
#if DEBUG
    private sealed class SmokeThreatClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
#endif

}
