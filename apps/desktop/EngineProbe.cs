using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Veil.Desktop;

internal sealed class EngineProbe : Window
{
    internal EngineProbe(string profile, string origin, string action, string report, ushort? proxyPort = null)
    {
        Title = "VEIL WebView2 Phase 0";
        Width = 600;
        Height = 400;
        ShowActivated = false;
        ShowInTaskbar = false;
        var view = new WebView2();
        Content = view;
        Loaded += async (_, _) =>
        {
            Dictionary<string, object?> result = [];
            try
            {
                var uri = new Uri(origin);
                bool downloadTrial = action is "download" or "download-feature";
                if (uri.Scheme != "http" || uri.Host is not ("127.0.0.1" or "[::1]") ||
                    (!downloadTrial && action is not ("write" or "read" or "proxy" or "crash-write" or "network-control")))
                    throw new ArgumentException("Expected a synthetic loopback experiment.");

                var engine = await Program.CreateEngine(profile, proxyPort, action == "download-feature")
                    .WaitAsync(TimeSpan.FromSeconds(15));
                var exited = new TaskCompletionSource();
                engine.BrowserProcessExited += (_, _) => exited.TrySetResult();
                bool controllerStarted = false;
                try
                {
                    await view.EnsureCoreWebView2Async(engine).WaitAsync(TimeSpan.FromSeconds(15));
                    controllerStarted = true;
                    var web = view.CoreWebView2;
                    result["engine"] = $"WebView2 {engine.BrowserVersionString}";
                    result["renderer"] = engine.GetProcessInfos().Any(info => info.Kind == CoreWebView2ProcessKind.Renderer);
                    var download = new TaskCompletionSource<CoreWebView2DownloadState>();
                    var downloadBody = new TaskCompletionSource<string>();
                    if (downloadTrial)
                    {
                        string downloadUrl = new Uri(uri, "/download?kind=get").AbsoluteUri;
                        string targetFolder = Path.Combine(profile, "download-observation");
                        Directory.CreateDirectory(targetFolder);
                        web.Profile.DefaultDownloadFolderPath = targetFolder;
                        web.WebResourceResponseReceived += async (_, args) =>
                        {
                            if (args.Request.Uri != downloadUrl) return;
                            try
                            {
                                var body = await args.Response.GetContentAsync();
                                if (body is null)
                                {
                                    downloadBody.TrySetResult("null");
                                    return;
                                }
                                string digest = await Task.Run(() =>
                                {
                                    using (body)
                                    {
                                        var buffer = new byte[4097];
                                        int bytes = body.ReadAtLeast(buffer, buffer.Length, false);
                                        return bytes == buffer.Length ? "over probe limit" :
                                            Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, bytes)));
                                    }
                                });
                                downloadBody.TrySetResult(digest);
                            }
                            catch (System.Runtime.InteropServices.COMException error)
                            {
                                downloadBody.TrySetResult($"COM error 0x{error.HResult:X8}");
                            }
                            catch (IOException)
                            {
                                downloadBody.TrySetResult("stream I/O error");
                            }
                        };
                    }
                    web.DownloadStarting += (_, args) =>
                    {
                        args.Handled = true;
                        args.Cancel = true;
                        var operation = args.DownloadOperation;
                        operation.StateChanged += (_, _) => download.TrySetResult(operation.State);
                    };
                    var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
                    string target = action switch
                    {
                        "proxy" => new Uri(uri, "/proxy-up").AbsoluteUri,
                        "network-control" => new Uri(uri, "/network-control").AbsoluteUri,
                        _ => uri.AbsoluteUri,
                    };
                    ulong? navigationId = null;
                    web.NavigationStarting += (_, args) =>
                    {
                        if (args.Uri == target) navigationId = args.NavigationId;
                    };
                    web.NavigationCompleted += (_, args) =>
                    {
                        if (args.NavigationId == navigationId) loaded.TrySetResult(args);
                    };
                    web.Navigate(target);
                    var navigation = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    if (!navigation.IsSuccess)
                        throw new InvalidOperationException($"Navigation failed: {navigation.WebErrorStatus}, HTTP {navigation.HttpStatusCode}");

                    if (action == "network-control")
                    {
                        result["directResponse"] = await web.ExecuteScriptAsync("document.body.textContent.trim()") == "\"VEIL_DIRECT_CONTROL\"";
                    }
                    else if (action == "proxy")
                    {
                        result["proxyResponse"] = await web.ExecuteScriptAsync("document.body.textContent.trim()") == "\"VEIL_PROXY_ONLY\"";
                        target = new Uri(uri, "/proxy-down").AbsoluteUri;
                        loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
                        web.Navigate(target);
                        var outage = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(12));
                        result["navigatedDuringOutage"] = outage.IsSuccess;
                        result["outageError"] = outage.WebErrorStatus.ToString();
                    }
                    else if (downloadTrial)
                    {
                        web.Navigate(new Uri(uri, "/download?kind=get").AbsoluteUri);
                        result["download"] = (await download.Task.WaitAsync(TimeSpan.FromSeconds(12))).ToString();
                        try { result["downloadBody"] = await downloadBody.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch (TimeoutException) { result["downloadBody"] = "no matching response event within 3 seconds"; }
                    }
                    else
                    {
                        var scriptAction = JsonSerializer.Serialize(action == "crash-write" ? "write" : action);
                        await web.ExecuteScriptAsync($"probe({scriptAction}).then(r => window.probeResult = r, e => window.probeResult = {{error: String(e)}}); null;");
                        for (var attempt = 0; attempt < 100; attempt++)
                        {
                            string json = await web.ExecuteScriptAsync("window.probeResult ?? null").WaitAsync(TimeSpan.FromSeconds(5));
                            if (json != "null")
                            {
                                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!)
                                    result[key] = value;
                                break;
                            }
                            await Task.Delay(75);
                        }
                        if (!result.ContainsKey("cookie") && !result.ContainsKey("error"))
                            throw new TimeoutException("Site storage result did not arrive.");
                        if (action == "crash-write" && !result.ContainsKey("error"))
                        {
                            result["browserPid"] = web.BrowserProcessId;
                            File.WriteAllText(report, JsonSerializer.Serialize(result));
                            // The harness terminates this synthetic host before the view is disposed.
                            await Task.Delay(Timeout.InfiniteTimeSpan);
                        }
                    }
                    result["renderer"] = engine.GetProcessInfos().Any(info => info.Kind == CoreWebView2ProcessKind.Renderer);
                }
                finally
                {
                    view.Dispose();
                    if (controllerStarted) await exited.Task.WaitAsync(TimeSpan.FromSeconds(12));
                }
            }
            catch (Exception error)
            {
                result["error"] = $"{error.GetType().Name}: {error.Message}";
            }
            finally
            {
                File.WriteAllText(report, JsonSerializer.Serialize(result));
                Application.Current.Shutdown(result.ContainsKey("error") ? 1 : 0);
            }
        };
    }
}
