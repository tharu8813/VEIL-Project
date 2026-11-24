#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AutomationProperties = System.Windows.Automation.AutomationProperties;

namespace Veil.Desktop;

/// <summary>
/// Phase 6 batch smoke (Debug only): drives the native management pages through their real controls.
/// Confirmations, notices and secret prompts are answered through <see cref="UserPrompts"/> hooks, so no
/// modal window blocks the run. Real keyboard/IME/DPI/screen reader and real devices stay manual.
/// </summary>
public partial class BrowserWindow
{
    private sealed class Phase6Harness
    {
        internal readonly Queue<bool> Answers = new();
        internal readonly List<(string Caption, string Message)> Confirms = [];
        internal readonly List<(string Caption, string Message)> Notices = [];
        internal readonly Queue<string?> Secrets = new();
        internal readonly List<string> SecretTitles = [];
        internal readonly List<string> Modals = [];

        internal void Install()
        {
            UserPrompts.SmokeConfirm = (caption, message) =>
            {
                Confirms.Add((caption, message));
                return Answers.Count > 0 && Answers.Dequeue();
            };
            UserPrompts.SmokeNotice = (caption, message) => Notices.Add((caption, message));
            UserPrompts.SmokeSecret = title =>
            {
                SecretTitles.Add(title);
                return Secrets.Count > 0 && Secrets.Dequeue() is { } value ? value.ToCharArray() : null;
            };
            UserPrompts.SmokeModal = Modals.Add;
        }
    }

    internal static bool IsPhase6Stage(string stage) => stage.StartsWith("p6-", StringComparison.Ordinal);

    private async Task Phase6StageAsync(string stage, Uri origin, List<string> checks)
    {
        var harness = new Phase6Harness();
        harness.Install();
        string http = origin.GetLeftPart(UriPartial.Authority);
        string httpsA = Environment.GetEnvironmentVariable("VEIL_TEST_HTTPS_A") ?? "";
        string httpsB = Environment.GetEnvironmentVariable("VEIL_TEST_HTTPS_B") ?? "";

        void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        async Task WaitFor(Func<bool> condition, string name, int attempts = 160)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (condition()) { Check(true, name); return; }
                await Task.Delay(75);
            }
            throw new TimeoutException(name);
        }
        async Task Settle()
        {
            for (int i = 0; i < 3; i++)
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        async Task<string> Eval(string expression)
        {
            await Active!.View.ExecuteScriptAsync("window.__p6 = undefined; Promise.resolve(" + expression +
                ").then(r => window.__p6 = JSON.stringify(r ?? null), e => window.__p6 = JSON.stringify({ error: String(e) })); null");
            for (int i = 0; i < 200; i++)
            {
                string raw = await Active!.View.ExecuteScriptAsync("window.__p6 ?? null");
                if (raw != "null") return JsonSerializer.Deserialize<string>(raw)!;
                await Task.Delay(75);
            }
            throw new TimeoutException(expression);
        }
        async Task Go(string address)
        {
            string url = new Uri(address).AbsoluteUri;
            NavigateAddress(url);
            await WaitFor(() => Active?.Url == url, $"navigated to {url}");
            for (int i = 0; i < 160; i++)
            {
                if (await Active!.View.ExecuteScriptAsync($"location.href === {JsonSerializer.Serialize(url)} && document.readyState === 'complete'") == "true")
                    return;
                await Task.Delay(75);
            }
            throw new TimeoutException($"document loaded at {url}");
        }
        // A real user gesture through the engine's input pipeline (not a script click).
        async Task Gesture(string id)
        {
            string rect = await Eval($"(() => {{ const r = document.getElementById({JsonSerializer.Serialize(id)}).getBoundingClientRect(); return {{ x: r.left + r.width / 2, y: r.top + r.height / 2 }}; }})()");
            using var json = JsonDocument.Parse(rect);
            double x = json.RootElement.GetProperty("x").GetDouble(), y = json.RootElement.GetProperty("y").GetDouble();
            foreach (string type in new[] { "mousePressed", "mouseReleased" })
                await Active!.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                    JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }));
        }
        async Task<string> WaitValue(string name, string label)
        {
            for (int i = 0; i < 120; i++)
            {
                string raw = await Active!.View.ExecuteScriptAsync($"window[{JsonSerializer.Serialize(name)}] ?? null");
                if (raw != "null") return JsonSerializer.Deserialize<string>(raw)!;
                await Task.Delay(75);
            }
            throw new TimeoutException(label);
        }
        static IEnumerable<DependencyObject> Tree(DependencyObject root)
        {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var nested in Tree(child)) yield return nested;
        }
        static T Find<T>(DependencyObject root, Func<T, bool> accept, string what) where T : DependencyObject =>
            Tree(root).OfType<T>().FirstOrDefault(accept) ?? throw new InvalidOperationException($"control not found: {what}");
        static T Named<T>(DependencyObject root, string name) where T : DependencyObject =>
            Find<T>(root, element => AutomationProperties.GetName(element) == name, name);
        static Button ButtonText(DependencyObject root, string label) =>
            Find<Button>(root, button => button.Content as string == label, label);
        void Click(Button button, string label)
        {
            // RaiseEvent ignores IsEnabled; a disabled control must not be "clicked" by the harness.
            Check(button.IsEnabled, $"{label} enabled");
            // A real click on a page in this window activates it first (another VEIL window may be in front).
            if (!IsActive) Activate();
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        async Task<FrameworkElement> OpenPage(Action open, string route)
        {
            // The previous handler releases its guards after its page closes.
            await WaitFor(() => !session.SettingsUiOpen && !session.PermissionUiOpen && !session.PasswordUiOpen,
                $"management guards released before {route}");
            if (!IsActive) Activate();
            open();
            FrameworkElement? page = null;
            await WaitFor(() => FindManagementTab(route) is { CancelManagement: not null, ManagementSurface: { } surface } &&
                (page = surface) is not null, $"{route} page opened");
            await Settle();
            return page!;
        }
        async Task ClosePage(FrameworkElement page, string route, string label = "닫기")
        {
            Click(ButtonText(page, label), $"{route} {label}");
            await WaitFor(() => FindManagementTab(route) is null && !openingManagement.Contains(route), $"{route} page closed");
            await Settle();
        }
        async Task Activated()
        {
            // Site permissions and password fill require the active browser window (PermissionContextMatches).
            for (int i = 0; i < 40 && !IsActive; i++)
            {
                Activate();
                await Task.Delay(100);
            }
            Check(IsActive, "browser window is the active window (required for permission and fill context)");
        }
        void BlockSave() => Directory.CreateDirectory(statePath + ".new");
        void UnblockSave() => Directory.Delete(statePath + ".new");

        // Native pages accept completion only from the active window, as with real clicks.
        await Activated();
        switch (stage)
        {
            case "p6-library":
            {
                await Go(http + "/");
                BookmarkClick(this, new RoutedEventArgs());
                await Active!.View.ExecuteScriptAsync("probe('write').then(r => window.p6seed = String(Object.values(r).every(Boolean))); null");
                Check(await WaitValue("p6seed", "site data seeded") == "true", "site data seeded for preservation check");
                await Go(http + "/second");
                BookmarkClick(this, new RoutedEventArgs());
                Check(data.Bookmarks.Count == 2, "two bookmarks created");

                var now = DateTimeOffset.Now;
                var seeded = new (string Label, TimeSpan Age)[]
                {
                    ("p6-h-30m", TimeSpan.FromMinutes(30)), ("p6-h-58m", TimeSpan.FromMinutes(58)), ("p6-h-62m", TimeSpan.FromMinutes(62)),
                    ("p6-h-2h", TimeSpan.FromHours(2)), ("p6-h-3d", TimeSpan.FromDays(3)), ("p6-h-10d", TimeSpan.FromDays(10)),
                    ("p6-h-40d", TimeSpan.FromDays(40)),
                };
                foreach (var (label, age) in seeded)
                    data.History.Add(new PageEntry(label, $"{http}/visit?n={label}", now - age));
                data.Save(statePath);
                int P6(Func<PageEntry, bool> where) => data.History.Count(entry => entry.Title.StartsWith("p6-h-") && where(entry));

                // Bookmarks: search, rename, save failure rollback, delete, other windows.
                var page = await OpenPage(() => BookmarksClick(this, new RoutedEventArgs()), "veil://bookmarks");
                var search = Named<TextBox>(page, "북마크 관리 검색");
                var list = Named<ListBox>(page, "북마크 관리");
                Check(list.Items.Count == 2, "bookmark manager lists both bookmarks");
                search.Text = "second";
                Check(list.Items.Count == 1, "bookmark search filters by URL");
                search.Text = "";
                list.SelectedIndex = 0;
                var name = Named<TextBox>(page, "북마크 이름");
                Check(name.Text == data.Bookmarks[0].Title, "rename box shows selected title");
                name.Text = "  P6 이름 변경  ";
                Click(ButtonText(page, "이름 저장"), "rename");
                Check(data.Bookmarks[0].Title == "P6 이름 변경" && data.Bookmarks[0].Url == http + "/", "bookmark renamed (trimmed, URL kept)");
                Check(BrowserData.Load(statePath).Bookmarks[0].Title == "P6 이름 변경", "rename persisted");
                var other = OpenWindow(["about:blank"], 0, cascade: true);
                await WaitFor(() => !other.IsRestoring, "second window ready");
                Check(other.BookmarkBarItems.Items.OfType<PageEntry>().Any(entry => entry.Title == "P6 이름 변경"),
                    "other window bookmark bar shows the rename");

                BlockSave();
                try
                {
                    list.SelectedIndex = 1;
                    Click(ButtonText(page, "선택 항목 삭제"), "delete bookmark (save blocked)");
                    Check(harness.Notices.Any(n => n.Message.Contains("저장하지 못했습니다")), "save failure reported");
                    Check(data.Bookmarks.Count == 2 && list.Items.Count == 2, "save failure restores the bookmark list");
                }
                finally { UnblockSave(); }
                Check(BrowserData.Load(statePath).Bookmarks.Count == 2, "state file kept after failed save");
                list.SelectedIndex = 1;
                Click(ButtonText(page, "선택 항목 삭제"), "delete bookmark");
                Check(data.Bookmarks.Count == 1 && BrowserData.Load(statePath).Bookmarks.Count == 1, "bookmark deleted and persisted");
                await Settle();
                Check(other.BookmarkBarItems.Items.Count == 1, "other window bookmark bar reflects deletion");
                await ClosePage(page, "veil://bookmarks");
                other.Close();
                await WaitFor(() => !session.Windows.Contains(other), "second window closed");

                // History: ranges, boundary, search independence, cancel, deletion scope.
                page = await OpenPage(() => HistoryClick(this, new RoutedEventArgs()), "veil://history");
                var range = Named<ComboBox>(page, "방문 기록 기간");
                var history = Named<ListBox>(page, "방문 기록");
                var historySearch = Named<TextBox>(page, "방문 기록 검색");
                historySearch.Text = "p6-h-";
                int[] expected = [7, 2, 4, 5, 6];
                for (int i = 0; i < expected.Length; i++)
                {
                    range.SelectedIndex = i;
                    Check(history.Items.Count == expected[i], $"history range {range.SelectedItem}: {expected[i]} seeded entries");
                }
                range.SelectedIndex = 1;
                historySearch.Text = "no-such-entry";
                Check(history.Items.Count == 0, "search hides all rows");
                int recent = data.History.Count(entry => entry.Visited >= DateTimeOffset.Now.AddHours(-1));
                int total = data.History.Count;
                harness.Answers.Enqueue(false);
                Click(ButtonText(page, "선택 기간 삭제"), "clear last hour (cancel)");
                var asked = harness.Confirms[^1];
                Check(asked.Message.Contains("최근 1시간") && asked.Message.Contains($"{recent}개") &&
                    asked.Message.Contains("검색어와 관계없이"), $"confirmation names scope and count ({recent})");
                Check(data.History.Count == total, "cancelled deletion keeps history");
                harness.Answers.Enqueue(true);
                Click(ButtonText(page, "선택 기간 삭제"), "clear last hour");
                Check(P6(_ => true) == 5 && P6(e => e.Title is "p6-h-30m" or "p6-h-58m") == 0 &&
                    P6(e => e.Title == "p6-h-62m") == 1, "last hour deleted regardless of search; older kept");
                Check(data.History.Count == total - recent, "only the selected range was deleted");
                range.SelectedIndex = 3;
                harness.Answers.Enqueue(true);
                Click(ButtonText(page, "선택 기간 삭제"), "clear last 7 days");
                Check(P6(_ => true) == 2 && P6(e => e.Title is "p6-h-10d" or "p6-h-40d") == 2, "7-day range deleted; 10 and 40 days kept");
                var stored = BrowserData.Load(statePath);
                Check(stored.History.Count(e => e.Title.StartsWith("p6-h-")) == 2, "history deletion persisted");
                Check(stored.Bookmarks.Count == 1, "history deletion keeps bookmarks");
                await ClosePage(page, "veil://history");

                await Go(http + "/");
                await Active!.View.ExecuteScriptAsync("probe('read').then(r => window.p6read = JSON.stringify(r)); null");
                string read = await WaitValue("p6read", "site storage readback");
                using (var json = JsonDocument.Parse(read))
                    Check(json.RootElement.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.True),
                        $"history deletion keeps cookies and site storage: {read}");
                break;
            }
            case "p6-library-read":
            {
                Check(data.Bookmarks is [{ Title: "P6 이름 변경" }], "renamed bookmark restored after restart");
                Check(data.History.Count(e => e.Title.StartsWith("p6-h-")) == 2 &&
                    !data.History.Any(e => e.Title is "p6-h-30m" or "p6-h-3d"), "history deletions restored after restart");
                break;
            }
            case "p6-bookmark-folders":
            {
                var page = await OpenPage(() => BookmarksClick(this, new RoutedEventArgs()), "veil://bookmarks");
                var folderView = Named<ComboBox>(page, "현재 폴더");
                var destination = Named<ComboBox>(page, "이동할 폴더");
                var folderName = Named<TextBox>(page, "폴더 이름");
                var search = Named<TextBox>(page, "북마크 관리 검색");
                var list = Named<ListBox>(page, "북마크 관리");
                void Select(ComboBox box, string label) => box.SelectedItem =
                    box.Items.Cast<object>().Single(item => item.ToString() == label);
                void Press(string label) => Click(ButtonText(page, label), label);
                string Snapshot(BrowserData value) => JsonSerializer.Serialize(new { value.Bookmarks, value.BookmarkFolders });
                string before = Snapshot(data);
                string temp = Path.Combine(session.Root, "p6-bookmark-fixtures-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                var picks = new Queue<(bool Save, string? Path)>();
                UserPrompts.SmokeFilePicker = save =>
                {
                    Check(picks.Count > 0, "expected file picker request");
                    var pick = picks.Dequeue();
                    Check(pick.Save == save, "correct picker kind");
                    return pick.Path;
                };
                try
                {
                    string input = Path.Combine(temp, "import.html");
                    string invalid = Path.Combine(temp, "invalid.html");
                    string output = Path.Combine(temp, "export.html");
                    File.WriteAllText(input, "<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><p>" +
                        "<DT><H3>P6 Parent</H3><DL><p><DT><H3>P6 Child</H3><DL><p>" +
                        "<DT><A HREF=\"https://bookmark-fixture.test/a?x=1&amp;y=2\">P6 &amp; A</A>" +
                        "<DT><A HREF=\"https://bookmark-fixture.test/b\">P6 B</A>" +
                        "<DT><A HREF=\"https://bookmark-fixture.test/b\">Duplicate</A>" +
                        "<DT><A HREF=\"javascript:alert(1)\">Unsafe</A></DL><p></DL><p></DL><p>");
                    File.WriteAllText(invalid, "<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><script>bad</script></DL>");
                    picks.Enqueue((false, null));
                    Press("HTML 가져오기");
                    Check(Snapshot(data) == before, "cancelled import preserves state");
                    picks.Enqueue((false, invalid));
                    Press("HTML 가져오기");
                    Check(Snapshot(data) == before && harness.Notices[^1].Message.Contains("읽지 못했습니다"), "active markup rejected");
                    BlockSave();
                    try
                    {
                        picks.Enqueue((false, input));
                        Press("HTML 가져오기");
                        Check(Snapshot(data) == before && harness.Notices[^1].Message.Contains("저장하지 못했습니다"), "import save failure rolls back both lists");
                    }
                    finally { UnblockSave(); }
                    Check(Snapshot(BrowserData.Load(statePath)) == before, "import rollback preserves persisted state");
                    picks.Enqueue((false, input));
                    Press("HTML 가져오기");
                    var parent = data.BookmarkFolders.Single(f => f.Name == "P6 Parent");
                    var child = data.BookmarkFolders.Single(f => f.Name == "P6 Child");
                    Check(child.ParentId == parent.Id && data.Bookmarks.Count(e => e.FolderId == child.Id) == 2,
                        "HTML imports nested folders and skips unsafe and duplicate URLs");
                    Check(harness.Notices[^1].Message.Contains("건너뜀 2개"), "import reports skipped items");
                    Select(folderView, "최상위 / P6 Parent / P6 Child");
                    Check(list.Items.Count == 2, "folder filters bookmark rows");
                    list.SelectedIndex = 1;
                    Press("북마크 ↑");
                    Check(data.Bookmarks.First(e => e.FolderId == child.Id).Title == "P6 B", "bookmark reordered");
                    list.SelectedIndex = 0;
                    Select(destination, "최상위 / P6 Parent");
                    Press("북마크 이동");
                    Check(data.Bookmarks.Single(e => e.Title == "P6 B").FolderId == parent.Id, "bookmark moved");
                    Select(folderView, "최상위 / P6 Parent");
                    Select(destination, "최상위 / P6 Parent / P6 Child");
                    Press("폴더 이동");
                    Check(data.BookmarkFolders.Single(f => f.Id == parent.Id).ParentId is null &&
                        harness.Notices[^1].Message.Contains("하위 폴더"), "folder cycle rejected");
                    folderName.Text = "P6 New";
                    Press("폴더 추가");
                    var added = data.BookmarkFolders.Single(f => f.Name == "P6 New");
                    Check(added.ParentId == parent.Id, "folder created under current folder");
                    folderName.Text = "P6 Renamed";
                    Press("이름 변경");
                    Check(data.BookmarkFolders.Single(f => f.Id == added.Id).Name == "P6 Renamed", "folder renamed");
                    Select(destination, "최상위");
                    Press("폴더 이동");
                    Check(data.BookmarkFolders.Single(f => f.Id == added.Id).ParentId is null, "folder moved to root");
                    Select(folderView, "최상위 / P6 Parent");
                    harness.Answers.Enqueue(false);
                    Press("폴더 삭제");
                    Check(data.BookmarkFolders.Any(f => f.Id == parent.Id), "cancelled folder delete");
                    harness.Answers.Enqueue(true);
                    Press("폴더 삭제");
                    Check(!data.BookmarkFolders.Any(f => f.Id == parent.Id) &&
                        data.BookmarkFolders.Single(f => f.Id == child.Id).ParentId is null &&
                        data.Bookmarks.Single(e => e.Title == "P6 B").FolderId is null, "folder removal promotes children without losing bookmarks");
                    search.Text = "P6 & A";
                    Check(list.Items.Count == 1, "search spans folders");
                    search.Text = "";
                    string saved = Snapshot(data);
                    harness.Answers.Enqueue(false);
                    Press("HTML 내보내기");
                    Check(!File.Exists(output), "plaintext export declined");
                    harness.Answers.Enqueue(true);
                    picks.Enqueue((true, null));
                    Press("HTML 내보내기");
                    Check(!File.Exists(output), "export picker cancelled");
                    harness.Answers.Enqueue(true);
                    picks.Enqueue((true, output));
                    Press("HTML 내보내기");
                    byte[] exported = File.ReadAllBytes(output);
                    var roundTrip = BookmarkHtml.Read(output, []);
                    var entry = roundTrip.Entries.Single(e => e.Title == "P6 & A");
                    Check(entry.Url == "https://bookmark-fixture.test/a?x=1&y=2" &&
                        roundTrip.Folders.Single(f => f.Id == entry.FolderId).Name == "P6 Child", "export round trip preserves escaped title, URL and folder");
                    harness.Answers.Enqueue(true);
                    picks.Enqueue((true, output));
                    Press("HTML 내보내기");
                    Check(File.ReadAllBytes(output).SequenceEqual(exported) && harness.Notices[^1].Message.Contains("덮어쓰지"), "export collision preserves existing file");
                    Check(Snapshot(data) == saved && Snapshot(BrowserData.Load(statePath)) == saved, "export leaves protected state unchanged");
                    Check(picks.Count == 0 && harness.Answers.Count == 0, "all scripted prompts consumed");
                }
                finally
                {
                    UserPrompts.SmokeFilePicker = null;
                    Directory.Delete(temp, recursive: true);
                }
                await ClosePage(page, "veil://bookmarks");
                break;
            }
            case "p6-bookmark-folders-read":
            {
                var child = data.BookmarkFolders.Single(f => f.Name == "P6 Child");
                Check(child.ParentId is null && data.BookmarkFolders.Any(f => f.Name == "P6 Renamed" && f.ParentId is null) &&
                    !data.BookmarkFolders.Any(f => f.Name == "P6 Parent"), "folder changes restored after restart");
                Check(data.Bookmarks.Single(e => e.Title == "P6 & A").FolderId == child.Id &&
                    data.Bookmarks.Single(e => e.Title == "P6 B").FolderId is null, "bookmark moves restored after restart");
                break;
            }
            case "p6-tabs":
            {
                while (Tabs.Items.Count > 1) await CloseTabAsync((TabItem)Tabs.Items[^1]);
                await Go(http + "/");
                foreach (string path in new[] { "/second", "/?t=3", "/?t=4" })
                    Check(await OpenTab(http + path) is not null, $"tab {path}");
                await WaitFor(() => Tabs.Items.Count == 4, "four tabs");
                var items = Tabs.Items.Cast<TabItem>().ToArray();
                SetTabPinned(items[2], true);
                SetTabPinned(items[3], true);
                Check(PinnedTabCount == 2 && Tabs.Items.IndexOf(items[2]) == 0 && Tabs.Items.IndexOf(items[3]) == 1,
                    "pinned tabs move to the front in pin order");
                var pinnedTab = (BrowserTab)items[2].Tag;
                Check(pinnedTab.Header.Close.Visibility == Visibility.Collapsed && pinnedTab.Header.Label.Visibility == Visibility.Collapsed,
                    "pinned tab is compact without a close button");
                var added = await OpenTab(http + "/?t=5", index: 0);
                Check(added is not null && Tabs.Items.IndexOf(added.Item) == 2 && !added.Pinned, "new normal tab is never inserted before pinned tabs");
                Tabs.SelectedItem = items[0];
                int before = Tabs.Items.IndexOf(items[0]);
                MoveSelectedTab(-1);
                MoveSelectedTab(-1);
                Check(Tabs.Items.IndexOf(items[0]) >= PinnedTabCount && Tabs.Items.IndexOf(items[0]) <= before, "normal tab stops at pinned boundary");
                Tabs.SelectedItem = items[3];
                MoveSelectedTab(1);
                Check(Tabs.Items.IndexOf(items[3]) < PinnedTabCount, "pinned tab cannot move into normal tabs");
                await CloseTabAsync(items[2]);
                Check(PinnedTabCount == 1, "closing a pinned tab");
                RestoreClick(this, new RoutedEventArgs());
                await WaitFor(() => PinnedTabCount == 2, "closed pinned tab restores as pinned");
                SaveTabs();
                Check(BrowserData.Load(statePath).PinnedCount == 2, "pinned count saved");
                // Close others keeps the clicked tab and protects pinned tabs (as in Chrome/Edge).
                var keep = Tabs.Items.Cast<TabItem>().Last();
                var pinnedBefore = Tabs.Items.Cast<TabItem>().Take(PinnedTabCount).ToList();
                CloseTabs(Tabs.Items.Cast<TabItem>().Where(item => item != keep).ToList());
                await WaitFor(() => Tabs.Items.Count == pinnedBefore.Count + 1 && Tabs.Items[^1] == keep &&
                    pinnedBefore.All(Tabs.Items.Contains), "close other tabs keeps pinned tabs and the clicked tab");
                foreach (var pinned in pinnedBefore) SetTabPinned(pinned, false);
                CloseTabs(Tabs.Items.Cast<TabItem>().Where(item => item != keep).ToList());
                await WaitFor(() => Tabs.Items.Count == 1 && Tabs.Items[0] == keep, "unpinned tabs close with close others");
                // Abnormal pinned counts never pin unrelated tabs.
                string bad = Path.Combine(session.Root, "p6-bad-pins.json");
                File.WriteAllText(bad, JsonSerializer.Serialize(new { Bookmarks = Array.Empty<object>(), History = Array.Empty<object>(),
                    Tabs = new[] { "https://a.test/", "https://b.test/" }, Selected = 0, PinnedCount = 99,
                    Windows = new[] { new { Tabs = new[] { "https://c.test/" }, Selected = 0, PinnedCount = -3 } } }));
                var loaded = BrowserData.Load(bad);
                Check(loaded.PinnedCount == 0 && loaded.Windows is [{ PinnedCount: 0 }], "invalid pinned counts reset to zero");
                File.Delete(bad);

                // Tab search across windows, and a tab closed while the search page is open.
                await Go(http + "/second");
                var other = OpenWindow([http + "/?p6=other-window"], 0, cascade: true);
                await WaitFor(() => !other.IsRestoring && other.Tabs.Items.Count == 1 &&
                    ((BrowserTab)((TabItem)other.Tabs.Items[0]).Tag).Url.Contains("other-window"), "second window tab");
                var page = await OpenPage(() => TabSearchClick(this, new RoutedEventArgs()), "veil://tabs");
                var query = Named<TextBox>(page, "탭 검색어");
                var found = Named<ListBox>(page, "열린 탭");
                query.Text = "other-window";
                Check(found.Items.Count == 1 && ((TabSearchRow)((ListBoxItem)found.Items[0]).Tag).Window == 2, "tab search finds the other window's tab");
                query.Text = "no-such-tab";
                Check(found.Items.Count == 0, "tab search empty result");
                query.Text = "other-window";
                Click(ButtonText(page, "탭으로 이동"), "go to tab");
                await WaitFor(() => FindManagementTab("veil://tabs") is null, "tab search closed");
                Check(((BrowserTab)((TabItem)other.Tabs.SelectedItem).Tag).Url.Contains("other-window"), "tab search selects the tab in its window");

                Activate();
                page = await OpenPage(() => TabSearchClick(this, new RoutedEventArgs()), "veil://tabs");
                query = Named<TextBox>(page, "탭 검색어");
                query.Text = "other-window";
                var stale = (TabItem)other.Tabs.Items[0];
                Check(await other.OpenTab(http + "/?p6=keep") is not null, "replacement tab");
                await other.CloseTabAsync(stale);
                Click(ButtonText(page, "탭으로 이동"), "go to closed tab");
                await WaitFor(() => FindManagementTab("veil://tabs") is null, "tab search closed after stale choice");
                Check(other.Tabs.Items.Count == 1 && !((BrowserTab)((TabItem)other.Tabs.Items[0]).Tag).Url.Contains("other-window"),
                    "choosing a closed tab does nothing");
                other.Close();
                await WaitFor(() => !session.Windows.Contains(other), "second window closed");
                break;
            }
            case "p6-search":
            {
                await Go(http + "/");
                data.Bookmarks.Add(new PageEntry("P6 북마크 veil", "https://veil-bookmark.test/", DateTimeOffset.Now.AddDays(-5)));
                for (int i = 0; i < 12; i++)
                    data.History.Add(new PageEntry($"P6 기록 veil {i}", $"https://veil-history-{i}.test/", DateTimeOffset.Now.AddMinutes(-i)));
                data.History.Add(new PageEntry("P6 veil token", "https://veil-secret.test/?token=p6", DateTimeOffset.Now));
                data.History.Add(new PageEntry("P6 veil bookmark dup", "https://veil-bookmark.test/", DateTimeOffset.Now));
                var suggestions = SearchPolicy.Suggest(data, "veil");
                Check(suggestions.Count == 8, "at most 8 local suggestions");
                Check(suggestions[0].Url == "https://veil-bookmark.test/" && suggestions.Count(s => s.Url == "https://veil-bookmark.test/") == 1,
                    "bookmark ranks first and duplicate URL appears once");
                Check(suggestions.All(s => !s.Url.Contains('?')), "URLs with query strings are never suggested");
                Check(SearchPolicy.Suggest(data, "v").Count == 0 && SearchPolicy.Suggest(data, "veil\u0001").Count == 0,
                    "too-short and control-character input gives no suggestions");

                await Activated();
                Address.Focus();
                Address.Text = "veil-typed-p6marker";
                Address.Text = "veil";
                await Settle();
                Check(AddressSuggestionsPopup.IsOpen && AddressSuggestionsList.Items.Count == 8 && AddressSuggestionsList.SelectedIndex == -1,
                    "address popup opens without auto-selecting");
                MoveAddressSuggestion(1);
                Check(AddressSuggestionsList.SelectedIndex == 0, "arrow selects first suggestion");
                CloseAddressSuggestions();
                Check(!AddressSuggestionsPopup.IsOpen, "Esc path closes suggestions");

                // Ctrl+Enter completes bare names; Alt+Enter opens a new tab next to the current one.
                Address.Text = "veil-ctrl-enter";
                int tabCount = Tabs.Items.Count;
                SubmitAddress(ModifierKeys.Control);
                await WaitFor(() => Active?.Url.StartsWith("https://www.veil-ctrl-enter.com") == true, "Ctrl+Enter completes www.<name>.com");
                await Go(http + "/");
                Address.Text = http + "/second";
                var current = Active!;
                tabCount = Tabs.Items.Count;
                SubmitAddress(ModifierKeys.Alt);
                await WaitFor(() => Tabs.Items.Count == tabCount + 1 && Tabs.Items.IndexOf(current.Item) + 1 < Tabs.Items.Count &&
                    ((BrowserTab)((TabItem)Tabs.Items[Tabs.Items.IndexOf(current.Item) + 1]).Tag).Url == http + "/second" &&
                    current.Url == http + "/", "Alt+Enter opens next to current tab and keeps the current page");

                // Search settings: save, all windows updated, failed save rollback, corrupt id.
                var other = OpenWindow(["about:blank"], 0, cascade: true);
                await WaitFor(() => !other.IsRestoring, "second window ready");
                var page = await OpenPage(() => SearchSettingsClick(this, new RoutedEventArgs()), "veil://settings/search");
                var providers = Named<ComboBox>(page, "기본 검색 엔진");
                Check(providers.SelectedIndex == 0, "default provider is DuckDuckGo");
                providers.SelectedIndex = 3;
                Named<CheckBox>(page, "로컬 주소 추천").IsChecked = false;
                Click(ButtonText(page, "저장"), "save search settings");
                await WaitFor(() => data.SearchEngine == "naver", "search provider saved");
                var saved = BrowserData.Load(statePath);
                Check(saved.SearchEngine == "naver" && saved.LocalAddressSuggestions == false, "search settings persisted");
                Check(((string)other.Address.ToolTip).Contains("네이버") && ((string)Address.ToolTip).Contains("네이버"), "every window shows the new provider");
                Check(ResolveAddress("한글 & 검색", data.SearchEngine)?.AbsoluteUri.StartsWith("https://search.naver.com/search.naver?query=%ED%95%9C%EA%B8%80%20%26") == true,
                    "submitted search uses the saved provider with encoding");
                Check(SearchPolicy.Suggest(data, "veil").Count == 0, "local suggestions disabled");
                await WaitFor(() => FindManagementTab("veil://settings/search") is null && !session.SettingsUiOpen, "search page closed");
                BlockSave();
                try
                {
                    page = await OpenPage(() => SearchSettingsClick(this, new RoutedEventArgs()), "veil://settings/search");
                    Named<ComboBox>(page, "기본 검색 엔진").SelectedIndex = 1;
                    Click(ButtonText(page, "저장"), "save search settings (blocked)");
                    await WaitFor(() => Status.Text.Contains("검색 설정을 저장하지 못했습니다"), "failed save reported");
                    Check(data.SearchEngine == "naver" && data.LocalAddressSuggestions == false, "failed save keeps previous settings");
                }
                finally { UnblockSave(); }
                other.Close();
                string corrupt = Path.Combine(session.Root, "p6-search.json");
                File.WriteAllText(corrupt, """{"Bookmarks":[],"History":[],"Tabs":[],"Selected":0,"SearchEngine":"javascript:alert(1)"}""");
                Check(BrowserData.Load(corrupt).SearchEngine is null && SearchPolicy.Name(BrowserData.Load(corrupt).SearchEngine) == "DuckDuckGo",
                    "unknown provider id falls back to the default");
                File.Delete(corrupt);
                break;
            }
            case "p6-search-read":
            {
                Check(data.SearchEngine == "naver" && data.LocalAddressSuggestions == false && ((string)Address.ToolTip).Contains("네이버"),
                    "search settings restored after restart");
                break;
            }
            case "p6-permissions":
            {
                Check(httpsA.Length > 0 && httpsB.Length > 0, "HTTPS fixtures configured");
                await Activated();
                await Go(httpsA + "/perm");
                Check(SitePermissionPolicy.Origin(Active!.Url) == httpsA, "permission origin is the HTTPS fixture");
                async Task<string> Request(string id)
                {
                    await Active!.View.ExecuteScriptAsync($"window.{id}Result = undefined; null");
                    await Gesture(id);
                    return await WaitValue(id + "Result", $"{id} result");
                }
                Check(await Request("notify") == "denied" && harness.Confirms.Count == 0, "unset permission denies without prompting");

                var page = await OpenPage(() => SitePermissionsClick(this, new RoutedEventArgs()), "veil://settings/permissions");
                var sites = Named<ListBox>(page, "사이트 목록");
                Check(sites.Items.Count == 1 && (string)((ListBoxItem)sites.Items[0]).Tag == httpsA, "current HTTPS origin listed");
                Named<ComboBox>(page, "알림 권한").SelectedIndex = 2;
                Named<ComboBox>(page, "위치 권한").SelectedIndex = 1;
                harness.Answers.Enqueue(false);
                Click(ButtonText(page, "저장"), "save allow (declined)");
                Check(harness.Confirms[^1].Caption == "사이트 권한 허용" && data.SitePermissions is null &&
                    FindManagementTab("veil://settings/permissions") is not null, "declining the allow warning saves nothing");
                harness.Answers.Enqueue(true);
                Click(ButtonText(page, "저장"), "save allow");
                await WaitFor(() => data.SitePermissions is { Count: 2 }, "rules saved");
                Check(BrowserData.Load(statePath).SitePermissions is { Count: 2 }, "rules persisted");
                await WaitFor(() => FindManagementTab("veil://settings/permissions") is null && !session.PermissionUiOpen, "permissions page closed");

                await Go(httpsA + "/perm");
                Check(await Request("notify") == "granted", "allowed notification granted on user gesture");
                string scripted = await Eval("Notification.requestPermission()");
                checks.Add($"observed: script-initiated notification request after grant = {scripted}");
                await Go(httpsA + "/perm?fresh=1");
                int asks = harness.Confirms.Count;
                harness.Answers.Enqueue(false);
                string geo = await Request("geo");
                Check(harness.Confirms.Count == asks + 1 && harness.Confirms[^1].Message.Contains(httpsA) &&
                    harness.Confirms[^1].Message.Contains("위치"), "ask mode prompts with origin and kind");
                Check(geo == "err1", $"declined ask denies ({geo})");
                harness.Answers.Enqueue(true);
                await Go(httpsA + "/perm?fresh=2");
                geo = await Request("geo");
                Check(geo != "err1", $"accepted ask is not a permission denial ({geo}; no location device is expected)");

                await Go(httpsB + "/perm");
                asks = harness.Confirms.Count;
                Check(await Request("notify") == "denied" && harness.Confirms.Count == asks, "other port is a different origin (denied)");
                await Go(httpsA + "/perm-frame?child=" + Uri.EscapeDataString(httpsB + "/perm"));
                string framed = await WaitValue("frameResult", "cross-origin frame request");
                Check(framed is "denied" or "default", $"cross-origin iframe request denied ({framed})");
                await Go(http + "/");
                checks.Add($"observed: HTTP fixture notification request = {await Eval("Notification.requestPermission()")}");
                Check(data.SitePermissions is { Count: 2 } && harness.Confirms.Count == asks, "HTTP page request raises no prompt");

                // Revocation: next request is denied even after an earlier grant in the same session.
                await Go(httpsA + "/perm");
                page = await OpenPage(() => SitePermissionsClick(this, new RoutedEventArgs()), "veil://settings/permissions");
                Click(ButtonText(page, "선택 사이트 초기화"), "reset site");
                Click(ButtonText(page, "저장"), "save reset");
                await WaitFor(() => data.SitePermissions is null, "rules removed");
                await Go(httpsA + "/perm?revoked=1");
                Check(await Request("notify") == "denied", "revoked notification denied (no engine cache bypass)");

                // Restore one rule for the restart check.
                page = await OpenPage(() => SitePermissionsClick(this, new RoutedEventArgs()), "veil://settings/permissions");
                Named<ComboBox>(page, "카메라 권한").SelectedIndex = 1;
                Click(ButtonText(page, "저장"), "save ask rule");
                await WaitFor(() => data.SitePermissions is [{ Kind: "camera", Mode: "ask" }], "ask rule saved without allow warning");
                BlockSave();
                try
                {
                    page = await OpenPage(() => SitePermissionsClick(this, new RoutedEventArgs()), "veil://settings/permissions");
                    Named<ComboBox>(page, "마이크 권한").SelectedIndex = 1;
                    Click(ButtonText(page, "저장"), "save blocked");
                    await WaitFor(() => Status.Text.Contains("사이트 권한을 저장하지 못했습니다"), "failed permission save reported");
                    Check(data.SitePermissions is [{ Kind: "camera" }], "failed save keeps previous rules");
                }
                finally { UnblockSave(); }
                string corrupt = Path.Combine(session.Root, "p6-perm.json");
                File.WriteAllText(corrupt, JsonSerializer.Serialize(new { Bookmarks = Array.Empty<object>(), History = Array.Empty<object>(),
                    Tabs = Array.Empty<string>(), Selected = 0, SitePermissions = new[] {
                        new { Origin = httpsA, Kind = "camera", Mode = "allow" }, new { Origin = httpsA, Kind = "camera", Mode = "allow" } } }));
                Check(BrowserData.Load(corrupt).SitePermissions is null, "duplicate rules fail closed");
                File.WriteAllText(corrupt, JsonSerializer.Serialize(new { Bookmarks = Array.Empty<object>(), History = Array.Empty<object>(),
                    Tabs = Array.Empty<string>(), Selected = 0, SitePermissions = new[] { new { Origin = "http://127.0.0.1", Kind = "camera", Mode = "allow" } } }));
                Check(BrowserData.Load(corrupt).SitePermissions is null, "HTTP origin rule fails closed");
                File.Delete(corrupt);
                break;
            }
            case "p6-permissions-read":
            {
                Check(data.SitePermissions is [{ Kind: "camera", Mode: "ask" }] && data.SitePermissions[0].Origin == httpsA,
                    "permission rule restored after restart");
                break;
            }
            case "p6-sitedata":
            {
                await Go(httpsA + "/storage");
                Check(await Eval("seed('A')") == "true", "seed HTTPS A");
                await Go(httpsB + "/storage");
                Check(await Eval("seed('B')") == "true", "seed HTTPS B");
                await Go(http + "/");
                await Active!.View.ExecuteScriptAsync("probe('write').then(r => window.p6seed = String(Object.values(r).every(Boolean))); null");
                Check(await WaitValue("p6seed", "HTTP seed") == "true", "seed HTTP fixture");
                int bookmarks = data.Bookmarks.Count, historyCount = data.History.Count;

                // Blocked while a download is active.
                var pending = new BrowserDownload("p6-pending.bin");
                session.ActiveDownloads.Add(pending);
                SiteDataClick(this, new RoutedEventArgs());
                await Settle();
                Check(FindManagementTab("veil://settings/site-data") is null && Status.Text.Contains("다운로드가 끝나면"), "site data blocked during downloads");
                session.ActiveDownloads.Remove(pending);
                pending.Dispose();

                await Go(httpsA + "/storage");
                var page = await OpenPage(() => SiteDataClick(this, new RoutedEventArgs()), "veil://settings/site-data");
                var origins = Named<ListBox>(page, "사이트 저장소 출처 목록");
                var originList = origins.Items.Cast<ListBoxItem>().Select(item => (string)item.Tag).ToList();
                Check(originList.Contains(httpsA) && originList.Contains(httpsB) && originList.Contains(http),
                    $"origins listed separately by scheme and port ({string.Join(", ", originList)})");
                Check((string)((ListBoxItem)origins.SelectedItem).Tag == httpsA, "current origin preselected");
                var cookieList = Named<ListBox>(page, "쿠키 저장 도메인 목록");
                Check(cookieList.Items.Cast<ListBoxItem>().Any(item => ((CookieDomainSummary)item.Tag).Domain == "127.0.0.1"), "cookie domain listed");
                Named<TextBox>(page, "도메인 또는 출처 검색").Text = "no-such-domain";
                Check(origins.Items.Count == 0 && cookieList.Items.Count == 0, "site data search empty");
                Named<TextBox>(page, "도메인 또는 출처 검색").Text = "";
                Check((string)((ListBoxItem)origins.SelectedItem).Tag == httpsA, "current origin reselected");
                harness.Answers.Enqueue(false);
                Click(ButtonText(page, "선택 출처 저장소 삭제"), "clear origin (cancel)");
                Check(FindManagementTab("veil://settings/site-data") is not null, "cancelled deletion keeps the page");
                harness.Answers.Enqueue(true);
                Click(ButtonText(page, "선택 출처 저장소 삭제"), "clear origin A");
                await WaitFor(() => Status.Text.Contains("선택 출처의 지원 저장소 삭제"), "origin storage cleared");
                await Go(httpsA + "/storage?after=1");
                string a = await Eval("state()");
                Check(a.Contains("\"local\":null") && a.Contains("\"idb\":null") && a.Contains("\"cookie\":true"),
                    $"origin A storage removed, cookie kept ({a})");
                await Go(httpsB + "/storage?after=1");
                string b = await Eval("state()");
                Check(b.Contains("\"local\":\"B\"") && b.Contains("\"idb\":\"B\""), $"origin B (other port) storage kept ({b})");
                await Go(http + "/");
                await Active!.View.ExecuteScriptAsync("probe('read').then(r => window.p6read = String(Object.values(r).every(Boolean))); null");
                Check(await WaitValue("p6read", "HTTP readback") == "true", "HTTP origin storage kept");

                page = await OpenPage(() => SiteDataClick(this, new RoutedEventArgs()), "veil://settings/site-data");
                cookieList = Named<ListBox>(page, "쿠키 저장 도메인 목록");
                cookieList.SelectedItem = cookieList.Items.Cast<ListBoxItem>().First(item => ((CookieDomainSummary)item.Tag).Domain == "127.0.0.1");
                harness.Answers.Enqueue(true);
                Click(ButtonText(page, "선택 도메인 쿠키 삭제"), "delete cookie domain");
                for (int i = 0; i < 160 && !Status.Text.Contains("쿠키 삭제 확인") && !Status.Text.Contains("남아 있거나"); i++)
                    await Task.Delay(75);
                Check(Status.Text.Contains("선택 도메인 쿠키 삭제 확인"), "cookie domain deleted");
                var cookies = await Active!.View.CoreWebView2.CookieManager.GetCookiesAsync("");
                Check(!cookies.Any(cookie => cookie.Domain == "127.0.0.1"), "no cookies left for the selected domain");

                page = await OpenPage(() => SiteDataClick(this, new RoutedEventArgs()), "veil://settings/site-data");
                origins = Named<ListBox>(page, "사이트 저장소 출처 목록");
                origins.SelectedItem = origins.Items.Cast<ListBoxItem>().First(item => (string)item.Tag == httpsB);
                Click(ButtonText(page, "선택 출처 사용량 확인"), "usage");
                await WaitFor(() => harness.Notices.Any(n => n.Caption == "사이트 저장소 사용량" && n.Message.StartsWith(httpsB)), "usage reported without deleting");
                Check(data.Bookmarks.Count == bookmarks && data.History.Count >= historyCount, "site data actions keep bookmarks and history");
                break;
            }
            case "p6-passwords":
            {
                const string master = "p6-synthetic-master-passphrase";
                const string secret = "P6-Synthetic-Site-Secret-41";
                var store = new PasswordStore(session.Root);
                Check(store.Available && !store.Exists, "password store tool present and store not created");
                await Activated();
                await Go(httpsA + "/login");
                async Task<FrameworkElement?> OpenManager(params string?[] secrets)
                {
                    foreach (var value in secrets) harness.Secrets.Enqueue(value);
                    PasswordsClick(this, new RoutedEventArgs());
                    for (int i = 0; i < 200; i++)
                    {
                        if (FindManagementTab("veil://passwords") is { CancelManagement: not null, ManagementSurface: { } surface })
                        { await Settle(); return surface; }
                        if (!session.PasswordUiOpen) return null;
                        await Task.Delay(75);
                    }
                    throw new TimeoutException("password manager");
                }
                Check(await OpenManager("short-pass") is null && Status.Text.Contains("12자 이상") && !store.Exists, "short master passphrase refused");
                Check(await OpenManager(master, master + "x") is null && Status.Text.Contains("일치하지") && !store.Exists, "confirmation mismatch refused");
                var page = await OpenManager(master, master);
                Check(page is not null && store.Exists, "store created after confirmation");
                Named<TextBox>(page!, "사용자 이름").Text = "p6-user";
                Find<PasswordBox>(page!, _ => true, "password box").Password = secret;
                Click(ButtonText(page!, "현재 사이트에 저장 / 갱신"), "save login");
                await WaitFor(() => Status.Text.Contains("암호화해 저장"), "login saved");
                Check(!File.ReadAllText(Path.Combine(session.Root, "passwords.age")).Contains(secret), "store file has no plaintext secret");

                Check(await OpenManager("wrong-master-passphrase") is null && Status.Text.Contains("완료하지 못했습니다"), "wrong master passphrase refused");
                page = await OpenManager(master);
                var accounts = Named<ListBox>(page!, "저장된 계정");
                Check(accounts.Items.Count == 1 && !Tree(page!).OfType<TextBlock>().Any(t => t.Text.Contains(secret)), "list shows account without secret");
                accounts.SelectedIndex = 0;
                Click(ButtonText(page!, "현재 페이지 채우기"), "fill");
                await WaitFor(() => Status.Text.Contains("로그인 정보를 채웠습니다"), "login filled");
                string form = await Eval("formState()");
                Check(form.Contains("\"user\":\"p6-user\"") && form.Contains("\"pass\":true") && form.Contains("\"submitted\":0"),
                    $"fill sets fields and never submits ({form})");

                foreach (var (path, label) in new[] { ("/login-cross", "cross-origin form action"), ("/login-two", "two password fields"),
                             ("/login-new", "new-password field"), ("/login-hidden", "hidden password field") })
                {
                    await Go(httpsA + path);
                    page = await OpenManager(master);
                    accounts = Named<ListBox>(page!, "저장된 계정");
                    accounts.SelectedIndex = 0;
                    Click(ButtonText(page!, "현재 페이지 채우기"), $"fill {label}");
                    await WaitFor(() => Status.Text.Contains("지원되는 로그인 폼을 찾지 못했습니다"), $"{label} not filled");
                    Check(!(await Eval("formState()")).Contains("\"pass\":true"), $"{label}: password field left empty");
                }
                await Go(httpsA + "/login-frame?child=" + Uri.EscapeDataString(httpsA + "/login"));
                page = await OpenManager(master);
                Named<ListBox>(page!, "저장된 계정").SelectedIndex = 0;
                Click(ButtonText(page!, "현재 페이지 채우기"), "fill framed page");
                await WaitFor(() => Status.Text.Contains("지원되는 로그인 폼을 찾지 못했습니다"), "login in iframe is not filled");

                await Go(httpsB + "/login");
                page = await OpenManager(master);
                accounts = Named<ListBox>(page!, "저장된 계정");
                accounts.SelectedIndex = 0;
                Check(!ButtonText(page!, "현재 페이지 채우기").IsEnabled, "other port origin cannot fill the saved account");
                await ClosePage(page!, "veil://passwords");

                await Go(http + "/");
                page = await OpenManager(master);
                Check(!ButtonText(page!, "현재 사이트에 저장 / 갱신").IsEnabled, "HTTP page cannot save accounts");
                await ClosePage(page!, "veil://passwords");

                await Go(httpsA + "/login");
                page = await OpenManager(master);
                Named<ListBox>(page!, "저장된 계정").SelectedIndex = 0;
                harness.Secrets.Enqueue("wrong-master-passphrase");
                Click(ButtonText(page!, "비밀번호 보기"), "reveal");
                await WaitFor(() => !session.PasswordUiOpen, "reveal with wrong reauthentication finished");
                Check(harness.Modals.Count == 0, "wrong reauthentication reveals nothing");
                page = await OpenManager(master, master);
                Named<ListBox>(page!, "저장된 계정").SelectedIndex = 0;
                Click(ButtonText(page!, "비밀번호 보기"), "reveal (correct reauthentication)");
                await WaitFor(() => harness.Modals.Contains("비밀번호 확인"), "reveal after reauthentication");
                Check(harness.SecretTitles.Contains("비밀번호 열람 인증"), "reveal asked for reauthentication");

                page = await OpenManager(master);
                Named<ListBox>(page!, "저장된 계정").SelectedIndex = 0;
                harness.Answers.Enqueue(false);
                Click(ButtonText(page!, "삭제"), "delete (cancel)");
                Check(FindManagementTab("veil://passwords") is not null, "cancelled delete keeps the page");
                harness.Answers.Enqueue(true);
                Click(ButtonText(page!, "삭제"), "delete");
                await WaitFor(() => Status.Text.Contains("선택한 계정을 삭제"), "account deleted");
                Check((await store.ReadAsync(master.ToCharArray())).Count == 0, "store is empty after delete");
                // The harness scans the profile, report and logs for the synthetic secrets after exit.
                checks.Add("password secrets: " + JsonSerializer.Serialize(new[] { master, secret }).Length);
                break;
            }
            case "p6-password-maintenance":
            {
                const string master = "p6-synthetic-master-passphrase";
                const string next = "p6-synthetic-rotated-master-72";
                const string backupPass = "p6-synthetic-backup-pass-83";
                const string oldSecret = "P6-Maintenance-Old-Secret-19";
                const string newSecret = "P6-Maintenance-New-Secret-20";
                const string missingSecret = "P6-Maintenance-Missing-Secret-21";
                const string fixtureOrigin = "https://password-fixture.test";
                var store = new PasswordStore(session.Root);
                Check(store.Available, "password store tool present");
                async Task<List<SavedLogin>> Read(string pass)
                {
                    char[] chars = pass.ToCharArray();
                    try { return await store.ReadAsync(chars); }
                    finally { ProtectedProfileSession.ClearPassword(chars); }
                }
                async Task Write(List<SavedLogin> entries, bool replace)
                {
                    char[] chars = master.ToCharArray();
                    try { await store.WriteAsync(chars, entries, replace); }
                    finally { ProtectedProfileSession.ClearPassword(chars); }
                }
                async Task<FrameworkElement?> OpenManager(string pass)
                {
                    await WaitFor(() => !session.PasswordUiOpen && !session.PermissionUiOpen, "password guards released");
                    await Activated();
                    harness.Secrets.Enqueue(pass);
                    PasswordsClick(this, new RoutedEventArgs());
                    for (int i = 0; i < 200; i++)
                    {
                        if (FindManagementTab("veil://passwords") is { CancelManagement: not null, ManagementSurface: { } surface })
                        { await Settle(); return surface; }
                        if (!session.PasswordUiOpen) return null;
                        await Task.Delay(75);
                    }
                    throw new TimeoutException("password maintenance page");
                }
                async Task Act(string pass, string label, params string?[] secrets)
                {
                    var page = await OpenManager(pass);
                    Check(page is not null, "authenticated maintenance page");
                    foreach (var value in secrets) harness.Secrets.Enqueue(value);
                    Status.Text = "p6 maintenance pending";
                    Click(ButtonText(page!, label), label);
                    await WaitFor(() => !session.PasswordUiOpen && !session.PermissionUiOpen,
                        "maintenance finished: " + label, attempts: 1600);
                    Check(harness.Secrets.Count == 0, "all expected secret prompts consumed");
                }
                if (store.Exists)
                    Check((await Read(master)).Count == 0, "previous password smoke left an empty synthetic store");
                var old = new SavedLogin(fixtureOrigin, "p6-maintenance-existing", oldSecret);
                var missing = new SavedLogin(fixtureOrigin, "p6-maintenance-missing", missingSecret);
                await Write([old, missing], store.Exists);
                string temp = Path.Combine(session.Root, "p6-password-maintenance-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                string backup = Path.Combine(temp, "backup.age");
                string tampered = Path.Combine(temp, "tampered.age");
                string storePath = Path.Combine(session.Root, "passwords.age");
                var picks = new Queue<(bool Save, string? Path)>();
                UserPrompts.SmokeFilePicker = save =>
                {
                    Check(picks.Count > 0, "expected maintenance file picker");
                    var pick = picks.Dequeue();
                    Check(pick.Save == save, "correct maintenance picker kind");
                    return pick.Path;
                };
                try
                {
                    byte[] original = File.ReadAllBytes(storePath);
                    await Act(master, "암호화 백업", backupPass, backupPass + "x");
                    Check(!File.Exists(backup) && File.ReadAllBytes(storePath).SequenceEqual(original), "backup confirmation mismatch preserves store");
                    picks.Enqueue((true, null));
                    await Act(master, "암호화 백업", backupPass, backupPass);
                    Check(!File.Exists(backup), "cancelled backup picker creates no file");
                    picks.Enqueue((true, backup));
                    await Act(master, "암호화 백업", backupPass, backupPass);
                    Check(File.Exists(backup) && Status.Text.Contains("암호화 백업을 생성"), "encrypted backup created");
                    byte[] archived = File.ReadAllBytes(backup);
                    picks.Enqueue((true, backup));
                    await Act(master, "암호화 백업", backupPass, backupPass);
                    Check(File.ReadAllBytes(backup).SequenceEqual(archived) && Status.Text.Contains("완료하지 못했습니다"), "backup collision refused");
                    Check(File.ReadAllBytes(storePath).SequenceEqual(original), "backup operations preserve live ciphertext");

                    var current = old with { Password = newSecret };
                    await Write([current], replace: true);
                    byte[] beforeRestore = File.ReadAllBytes(storePath);
                    byte[] damaged = archived.ToArray();
                    damaged[damaged.Length - 1] ^= 0x55;
                    File.WriteAllBytes(tampered, damaged);
                    picks.Enqueue((false, backup));
                    await Act(master, "백업 복원", "p6-incorrect-backup-password");
                    Check(Status.Text.Contains("완료하지 못했습니다") && File.ReadAllBytes(storePath).SequenceEqual(beforeRestore), "wrong backup password refused");
                    picks.Enqueue((false, tampered));
                    await Act(master, "백업 복원", backupPass);
                    Check(Status.Text.Contains("완료하지 못했습니다") && File.ReadAllBytes(storePath).SequenceEqual(beforeRestore), "tampered backup refused");
                    picks.Enqueue((false, backup));
                    harness.Answers.Enqueue(false);
                    await Act(master, "백업 복원", backupPass);
                    Check(File.ReadAllBytes(storePath).SequenceEqual(beforeRestore), "cancelled restore preserves ciphertext");
                    picks.Enqueue((false, backup));
                    harness.Answers.Enqueue(true);
                    await Act(master, "백업 복원", backupPass);
                    Check(Status.Text.Contains("계정 1개를 복원") && (await Read(master)).SequenceEqual(new[] { current, missing }),
                        "restore adds missing account and preserves existing credential");
                    byte[] restored = File.ReadAllBytes(storePath);
                    picks.Enqueue((false, backup));
                    await Act(master, "백업 복원", backupPass);
                    Check(Status.Text.Contains("추가할 계정이 없습니다") && File.ReadAllBytes(storePath).SequenceEqual(restored),
                        "duplicate-only restore leaves ciphertext unchanged");

                    await Act(master, "관리자 암호 변경", "short");
                    Check(File.ReadAllBytes(storePath).SequenceEqual(restored), "short replacement refused");
                    await Act(master, "관리자 암호 변경", next, next + "x");
                    Check(File.ReadAllBytes(storePath).SequenceEqual(restored), "rotation confirmation mismatch preserves ciphertext");
                    await Act(master, "관리자 암호 변경", next, next);
                    Check(Status.Text.Contains("관리자 암호를 변경") && !File.ReadAllBytes(storePath).SequenceEqual(restored), "rotation publishes new ciphertext");
                    Check(await OpenManager(master) is null && Status.Text.Contains("완료하지 못했습니다"), "old master password refused");
                    Check((await Read(next)).SequenceEqual(new[] { current, missing }), "new master authenticates unchanged credentials");
                    char[] backupChars = backupPass.ToCharArray();
                    try { Check((await store.ReadBackupAsync(backup, backupChars)).SequenceEqual(new[] { old, missing }), "old backup remains independently decryptable"); }
                    finally { ProtectedProfileSession.ClearPassword(backupChars); }
                    Check(File.ReadAllBytes(backup).SequenceEqual(archived), "rotation leaves old backup ciphertext unchanged");
                    Check(picks.Count == 0 && harness.Answers.Count == 0, "all scripted prompts consumed");
                }
                finally
                {
                    UserPrompts.SmokeFilePicker = null;
                    Directory.Delete(temp, recursive: true);
                }
                break;
            }
            case "p6-password-maintenance-read":
            {
                var store = new PasswordStore(session.Root);
                char[] pass = "p6-synthetic-rotated-master-72".ToCharArray();
                try
                {
                    var entries = await store.ReadAsync(pass);
                    Check(entries.Count == 2 && entries.Any(e => e.UserName == "p6-maintenance-existing" && e.Password == "P6-Maintenance-New-Secret-20") &&
                        entries.Any(e => e.UserName == "p6-maintenance-missing" && e.Password == "P6-Maintenance-Missing-Secret-21"),
                        "rotated store and restored credentials survive restart");
                }
                finally { ProtectedProfileSession.ClearPassword(pass); }
                break;
            }
            case "p6-settings":
            {
                Check(new Exception[] { new InvalidDataException(), new System.Runtime.InteropServices.COMException(), new TimeoutException(),
                        new ObjectDisposedException("view"), new TaskCanceledException(), new System.Net.Http.HttpRequestException() }.All(RecoverableFailure.Is) &&
                    !RecoverableFailure.Is(new NullReferenceException()) && !RecoverableFailure.Is(new InvalidCastException()),
                    "UI handlers survive vault/engine/timeout failures but not programming errors");
                await Go(http + "/");
                var page = await OpenPage(() => SettingsClick(this, new RoutedEventArgs()), "veil://settings");
                var search = Named<TextBox>(page, "설정 검색");
                int Results() => Tree(page).OfType<Button>().Count(b => b.Content as string == "열기");
                search.Text = "비밀번호 자동완성";
                Check(Results() == 1 && Tree(page).OfType<TextBlock>().Any(t => t.Text == "비밀번호 관리자"), "multi-keyword search finds the password manager");
                search.Text = "no-such-setting-p6";
                Check(Tree(page).OfType<TextBlock>().Any(t => t.Text == "검색어에 해당하는 설정이 없습니다."), "empty settings search message");
                search.Text = "";
                var nav = Named<ListBox>(page, "설정 분류");
                nav.SelectedIndex = 1;
                await Settle();
                Check(Tree(page).OfType<TextBlock>().Any(t => t.Text == "사이트 권한"), "privacy section shows site permissions");
                Check(Tree(page).OfType<TextBlock>().Any(t => t.Text.Contains("개발 프로필 · 제품 보호 미적용")), "development profile state shown");
                search.Text = "검색 엔진";
                Click(Find<Button>(page, b => AutomationProperties.GetName(b) == "검색 및 주소창 열기", "search open"), "open search settings");
                await WaitFor(() => FindManagementTab("veil://settings/search") is { CancelManagement: not null }, "settings destination opens search page");
                page = FindManagementTab("veil://settings/search")!.ManagementSurface!;
                await ClosePage(page, "veil://settings/search", "취소");
                foreach (var (label, route) in new[] { ("사이트 권한 열기", "veil://settings/permissions"), ("방문 기록 열기", "veil://history"),
                             ("쿠키 및 사이트 데이터 열기", "veil://settings/site-data"), ("보안 열기", "veil://settings/security"),
                             ("네트워크 보호 및 보안 DNS 열기", "veil://settings/network") })
                {
                    page = await OpenPage(() => SettingsClick(this, new RoutedEventArgs()), "veil://settings");
                    search = Named<TextBox>(page, "설정 검색");
                    search.Text = label[..^3];
                    Click(Find<Button>(page, b => AutomationProperties.GetName(b) == label, label), label);
                    await WaitFor(() => FindManagementTab(route) is { CancelManagement: not null }, $"settings opens {route}");
                    var opened = FindManagementTab(route)!;
                    opened.CancelManagement!();
                    await WaitFor(() => FindManagementTab(route) is null && !session.SettingsUiOpen && !session.PermissionUiOpen, $"{route} closed");
                }
                // Web content cannot open native management routes.
                await Go(http + "/");
                int tabs = Tabs.Items.Count;
                await Active!.View.ExecuteScriptAsync("location.href = 'veil://settings'; window.open('veil://passwords'); null");
                await Task.Delay(1500);
                Check(Tabs.Items.Count == tabs && FindManagementTab("veil://settings") is null && FindManagementTab("veil://passwords") is null,
                    "page script cannot open settings or password manager");
                break;
            }
            default:
                throw new ArgumentException("Unknown Phase 6 smoke stage.");
        }
    }
}
#endif
