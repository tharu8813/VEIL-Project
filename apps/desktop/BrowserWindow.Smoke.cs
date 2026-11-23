using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

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
                Check(ResolveAddress("example.test")?.AbsoluteUri == "https://example.test/", "HTTPS address default");
                Check(ResolveAddress("한글 브라우저")?.Host == "duckduckgo.com", "search encoding");
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
                    NavigateAddress(origin);
                    await ReadyAt("/");
                    BookmarkClick(this, new RoutedEventArgs());
                    Check(data.Bookmarks.Count == 1, "bookmark created");
                    BookmarksClick(this, new RoutedEventArgs());
                    await WaitFor(() => Active?.Url == "veil://bookmarks", "bookmark library opened in tab");
                    Check(Active?.Title == "북마크", "bookmark library tab title");
                    CloseTab((TabItem)Tabs.SelectedItem);
                    await ReadyAt("/");
                    HistoryClick(this, new RoutedEventArgs());
                    await WaitFor(() => Active?.Url == "veil://history", "history opened in tab");
                    Check(Active?.Title == "방문 기록", "history tab title");
                    CloseTab((TabItem)Tabs.SelectedItem);
                    await ReadyAt("/");
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
                    Tabs.SelectedIndex = 1;
                    SaveTabs();
                }
                else if (stage is "read" or "crash")
                {
                    Check(Tabs.Items.Count == 3 && Tabs.SelectedIndex == 1, "tabs and selection restored");
                    await ReadyAt("/");
                    Check(data.Bookmarks.Count == 1 && data.Bookmarks[0].Url == origin + "/", "bookmark restored");
                    Check(data.History.Any(entry => entry.Url.EndsWith("/second")), "history restored");
                    await Active!.View.ExecuteScriptAsync("probe('read').then(result => window.smoke = Object.values(result).every(Boolean)); null;");
                    await WaitForScript("window.smoke === true", "five site storage types restored");
                    if (stage == "read")
                    {
                        // Tabs move between windows without reloading; the second window is persisted.
                        var moving = Active!;
                        await moving.View.ExecuteScriptAsync("window.veilMarker = 42; null");
                        int count = Tabs.Items.Count;
                        var other = MoveTabToNewWindow(moving.Item);
                        await WaitFor(() => moving.Owner == other && !other.IsRestoring &&
                            other.Tabs.Items.Contains(moving.Item) && Tabs.Items.Count == count - 1, "tab moved to a new window");
                        Check(await moving.View.ExecuteScriptAsync("window.veilMarker") == "42", "moved tab kept its live page");
                        SaveSession();
                        Check(BrowserData.Load(statePath).Windows is [{ Tabs.Count: 1 }], "second window persisted");
                        other.DetachTab(moving.Item);
                        AttachTab(moving.Item, 0);
                        await WaitFor(() => !session.Windows.Contains(other) && Tabs.Items.IndexOf(moving.Item) == 0 &&
                            Active == moving && Tabs.Items.Count == count, "tab merged back at the front");
                        Check(await moving.View.ExecuteScriptAsync("window.veilMarker") == "42", "merged tab kept its live page");
                        MoveSelectedTab(1);
                        Check(Tabs.Items.IndexOf(moving.Item) == 1 && Active == moving, "keyboard tab reorder");
                        Check(BrowserData.Load(statePath).Windows is null, "merged window removed from state");
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
                    Check(downloads[0] == $"veil-phase0-{type}.bin · 취소됨", $"download label ({type})");
                    Check(Active!.View.CoreWebView2.Profile.DefaultDownloadFolderPath.StartsWith(
                        Path.Combine(session.Root, ProfileScratch.FolderName), StringComparison.OrdinalIgnoreCase),
                        $"download scratch inside profile ({type})");
                    if (type == "get")
                    {
                        await WaitFor(() => DownloadButton.Visibility == Visibility.Visible, "download toolbar icon shown");
                        DownloadsClick(this, new RoutedEventArgs());
                        await WaitFor(() => Active?.Url == "veil://downloads", "downloads opened in tab");
                        Check(Active?.Title == "다운로드", "downloads tab title");
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
