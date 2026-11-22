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
        Loaded += async (_, _) =>
        {
            var checks = new List<string>();
            string? error = null;
            try
            {
                var uri = new Uri(origin);
                Check(uri.Scheme == "http" && uri.Host == "127.0.0.1", "loopback fixture");
                await WaitFor(() => !restoring, "desktop initialization");
                Check(ResolveAddress("example.test")?.AbsoluteUri == "https://example.test/", "HTTPS address default");
                Check(ResolveAddress("한글 브라우저")?.Host == "duckduckgo.com", "search encoding");
                Check(ResolveAddress("127.0.0.1:8765")?.AbsoluteUri == "http://127.0.0.1:8765/", "fixture address");
                Check(ResolveAddress("file:///C:/secret") is null &&
                    ResolveAddress("https://username@example.test") is null, "address scheme and credentials rejected");

                if (stage == "write")
                {
                    NavigateAddress(origin);
                    await ReadyAt("/");
                    BookmarkClick(this, new RoutedEventArgs());
                    Check(data.Bookmarks.Count == 1, "bookmark created");
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
                    Check(File.Exists(statePath), "browser state saved");
                }
                else if (stage == "read")
                {
                    Check(Tabs.Items.Count == 3 && Tabs.SelectedIndex == 1, "tabs and selection restored");
                    await ReadyAt("/");
                    Check(data.Bookmarks.Count == 1 && data.Bookmarks[0].Url == origin + "/", "bookmark restored");
                    Check(data.History.Any(entry => entry.Url.EndsWith("/second")), "history restored");
                    await Active!.View.ExecuteScriptAsync("probe('read').then(result => window.smoke = Object.values(result).every(Boolean)); null;");
                    await WaitForScript("window.smoke === true", "five site storage types restored");
                }
                else throw new ArgumentException("Unknown desktop smoke stage.");

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
                    await ReadyAt("/");
                    if (type == "sw")
                        await WaitForScript("Boolean(navigator.serviceWorker.controller)", "service worker has control");
                    int count = downloads.Count;
                    await Active!.View.ExecuteScriptAsync(script + "; null");
                    await WaitFor(() => downloads.Count == count + 1, $"download cancelled ({type})");
                    Check(downloads[0] == $"veil-phase0-{type}.bin · 취소됨", $"download label ({type})");
                    CloseTab((TabItem)Tabs.SelectedItem);
                    Tabs.SelectedItem = original;
                }
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
            }

            if (engine is not null && Tabs.Items.Count > 0)
            {
                var exit = new TaskCompletionSource();
                engine.BrowserProcessExited += (_, _) => exit.TrySetResult();
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Close();
                try { await exit.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (TimeoutException) { error ??= "WebView2 did not exit after closing the desktop window."; }
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
