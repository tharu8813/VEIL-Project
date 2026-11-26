// SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Veil.Desktop;

namespace Veil.Cef;

public static class WpfSmoke
{
    [UnmanagedCallersOnly]
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window { Title = "VEIL CEF test", Width = 840, Height = 600 };
        var stack = new DockPanel();
        var toolbar = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(toolbar, Dock.Top);
        stack.Children.Add(toolbar);
        var go = new Button { Content = "Go", Padding = new Thickness(12, 2, 12, 2) };
        DockPanel.SetDock(go, Dock.Right);
        toolbar.Children.Add(go);
        var address = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(address);
        IBrowserEngine engine = new CefDevelopmentEngine();
        engine.InitializeAsync(CefDevelopmentEngine.FixtureProfile, NetworkSettings.Standard)
            .GetAwaiter().GetResult();
        var browser = (CefDevelopmentView)engine.CreateView();
        ulong closingHandle = 0;
        stack.Children.Add(browser.Element);
        window.Content = stack;

        void Navigate()
        {
            try
            {
                browser.Navigate(address.Text);
                window.Title = "VEIL CEF test";
            }
            catch (ArgumentException)
            {
                window.Title = "Enter a local HTTP test URL";
            }
        }

        go.Click += (_, _) => Navigate();
        address.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Navigate();
            e.Handled = true;
        };

        var fixture = Environment.GetEnvironmentVariable("VEIL_CEF_WPF_TEST_URL");
        var fixtureVault = Environment.GetEnvironmentVariable("VEIL_CEF_WPF_VAULT_EXE");
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var fixtureLog = System.IO.Path.Combine(CefDevelopmentEngine.FixtureProfile, "smoke.log");
        void Trace(string message)
        {
            if (fixture is not null) System.IO.File.AppendAllText(fixtureLog, message + Environment.NewLine);
        }
        var closing = false;
        var windowClosed = false;
        var phase = -1;
        var pendingRequests = new Dictionary<ulong, string>();
        var completedRequests = new HashSet<string>();
        var canceledByPolicy = false;
        var redirectMatched = false;
        var failedHttpLoad = false;
        FixtureVault? vaultStream = null;
        var vaultScenario = VaultScenario.Truncated;
        string? lastTitle = null;
        var observedSources = new HashSet<string>();
        var observedTitles = new HashSet<string>();
        int historyChanges = 0;
        browser.SourceChanged += (_, _) => observedSources.Add(browser.Source);
        browser.TitleChanged += (_, _) => observedTitles.Add(browser.DocumentTitle);
        browser.HistoryChanged += (_, _) => historyChanges++;
        bool PageStateMatches(string path, string expectedTitle) =>
            Uri.TryCreate(browser.Source, UriKind.Absolute, out var source) &&
            source.AbsolutePath == path && observedSources.Contains(browser.Source) &&
            observedTitles.Contains(expectedTitle) && browser.DocumentTitle == expectedTitle;
        var result = fixture is null ? 0 : 30;
        int unexpectedProcessFailures = 0;
        browser.ProcessFailed += (_, failure) =>
        {
            unexpectedProcessFailures++;
            Trace($"Unexpected engine process failure: {failure.Scope}, code={failure.NativeCode}");
            // Leave the normal close/pump path in control; do not close inside a native callback.
        };
        browser.NavigationStarting += (_, request) =>
        {
            if (fixture is null) return;
            var path = new Uri(request.Uri).AbsolutePath;
            request.Cancel = path == "/wpf-denied";
            Trace($"Navigation {request.NavigationId} start {path} redirect={request.IsRedirected}");
            if (request.IsRedirected && path == "/wpf-first" &&
                pendingRequests.TryGetValue(request.NavigationId, out var previous) && previous == "/wpf-address")
                redirectMatched = true;
            pendingRequests[request.NavigationId] = path;
        };
        browser.NavigationCompleted += (_, completed) =>
        {
            if (fixture is null) return;
            if (!pendingRequests.Remove(completed.NavigationId, out var path)) return;
            Trace($"Navigation {completed.NavigationId} complete {path} success={completed.IsSuccess} canceled={completed.IsCanceled} code={completed.NativeErrorCode}");
            if (completed.NativeResult.Domain != EngineErrorDomain.Cef ||
                ((completed.IsSuccess || completed.IsCanceled) != (completed.Error is null)))
            {
                Trace("Unexpected navigation error domain or success/cancellation state");
                return;
            }
            if (path == "/wpf-denied" && completed.IsCanceled) canceledByPolicy = true;
            // Empty error responses can surface as ERR_HTTP_RESPONSE_CODE_FAILURE.
            if (path == "/wpf-error" && !completed.IsSuccess && !completed.IsCanceled &&
                completed.Error is { Domain: EngineErrorDomain.Cef, NativeCode: 404 or -379 })
                failedHttpLoad = true;
            if (completed.IsSuccess) completedRequests.Add(path);
        };
        if (fixture is not null)
        {
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Loaded += async (_, _) =>
            {
                await browser.InitializeAsync();
                if (browser.NativeHandle == 0 || CefNative.VeilCefClosed(browser.NativeHandle) != 0 ||
                    CefNative.VeilCefClosed(0) == 0 || CefNative.VeilCefSource(0) == 0 ||
                    Marshal.PtrToStringUni(CefNative.VeilCefSource(0)) != "")
                {
                    Trace("Invalid CEF view handle contract");
                    window.Close();
                    return;
                }
                CefNative.VeilCefSetMuted(0, 1);
                if (browser.IsMuted != false)
                {
                    Trace("Invalid handle modified the live view");
                    window.Close();
                    return;
                }
                Trace("Invalid view handle rejected without affecting live view");
                int muteEvents = 0;
                EventHandler audioChanged = (_, _) => muteEvents++;
                browser.AudioStateChanged += audioChanged;
                browser.SetMuted(true);
                bool muted = browser.IsMuted == true;
                browser.SetMuted(false);
                browser.AudioStateChanged -= audioChanged;
                using var unsupportedFavicon = await browser.GetFaviconPngAsync();
                if (!muted || browser.IsMuted != false || muteEvents != 2 ||
                    browser.IsDocumentPlayingAudio is not null || unsupportedFavicon is not null)
                {
                    Trace("Shared audio/unsupported favicon contract failed");
                    window.Close();
                    return;
                }
                Trace("Observed shared mute state round trip; playback and favicon unavailable");
                if (!Rejects(() => engine.InitializeAsync("C:\\unexpected-profile", NetworkSettings.Standard)) ||
                    !Rejects(() => engine.InitializeAsync(CefDevelopmentEngine.FixtureProfile,
                        new NetworkSettings(NetworkMode.Proxy, "http", "127.0.0.1", 3128))) ||
                    !Rejects(() => engine.CreateView("private")) ||
                    !Rejects(() => browser.Navigate("http://example.invalid/")))
                {
                    Trace("Development adapter accepted an unsupported input");
                    window.Close();
                    return;
                }
                Trace("Development adapter rejected unsupported profile, network and navigation inputs");
                address.Text = new Uri(new Uri(fixture), "/wpf-denied").AbsoluteUri;
                Trace("WPF loaded and submitting address");
                go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
        }
        else
        {
            window.Loaded += async (_, _) => await browser.InitializeAsync();
        }

        var pump = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        window.Closing += (_, e) =>
        {
            closing = true;
            vaultStream?.Complete(false);
            Trace("Requesting CEF close");
            closingHandle = browser.NativeHandle;
            e.Cancel = CefNative.VeilCefTryClose(closingHandle) == 0;
        };
        window.Closed += (_, _) =>
        {
            browser.Dispose();
            windowClosed = true;
            Trace("WPF window closed");
        };
        pump.Tick += (_, _) =>
        {
            CefNative.VeilCefPump();
            if (windowClosed && CefNative.VeilCefClosed(closingHandle) != 0)
            {
                Trace("CEF browser closed");
                pump.Stop();
                app.Shutdown();
                return;
            }
            if (closing)
            {
                if (!windowClosed && CefNative.VeilCefReadyToClose(browser.NativeHandle) != 0) window.Close();
                return;
            }

            var title = browser.DocumentTitle;
            if (!string.IsNullOrEmpty(title) && title != lastTitle)
            {
                lastTitle = title;
                window.Title = title;
            }
            if (fixture is null) return;
            if (phase == -1 && canceledByPolicy)
            {
                phase = 0;
                Trace("Navigation request canceled before HTTP");
                address.Text = fixture;
                go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (phase == 0 && PageStateMatches("/wpf-first", "VEIL_CEF_WPF_OK") && redirectMatched && completedRequests.Remove("/wpf-first"))
            {
                phase = 1;
                Trace("Observed JavaScript title");
                address.Text = new Uri(new Uri(fixture), "/wpf-second").AbsoluteUri;
                go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (phase == 1 && PageStateMatches("/wpf-second", "VEIL_CEF_WPF_SECOND") && browser.CanGoBack && historyChanges > 0 &&
                     completedRequests.Remove("/wpf-second"))
            {
                phase = 2;
                browser.GoBack();
            }
            // A restored history entry may update the title without load callbacks.
            else if (phase == 2 && PageStateMatches("/wpf-first", "VEIL_CEF_WPF_OK") && browser.CanGoForward)
            {
                phase = 3;
                browser.GoForward();
            }
            else if (phase == 3 && PageStateMatches("/wpf-second", "VEIL_CEF_WPF_SECOND"))
            {
                phase = 4;
                Trace("Observed matched redirect, completion, and back and forward navigation");
                Trace("Observed shared engine source, title and history events");
                address.Text = new Uri(new Uri(fixture), "/wpf-error").AbsoluteUri;
                go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (phase == 4 && failedHttpLoad)
            {
                phase = 5;
                Trace("Observed HTTP failure completion");
                address.Text = new Uri(new Uri(fixture), "/wpf-policy").AbsoluteUri;
                go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (phase == 5 && title == "VEIL_CEF_RESPONSE_AND_PERMISSIONS_OK" &&
                     CefNative.VeilCefResponseMatches(browser.NativeHandle) == 1 && CefNative.VeilCefAttachmentFilteredBytes(browser.NativeHandle) >= 0 &&
                     CefNative.VeilCefPopupsBlocked(browser.NativeHandle) != 0 &&
                     CefNative.VeilCefPermissionsDenied(browser.NativeHandle) != 0 &&
                     CefNative.VeilCefMediaPermissionsDenied(browser.NativeHandle) != 0 &&
                     CefNative.VeilCefBlobDownloadsBlocked(browser.NativeHandle) != 0 &&
                     CefNative.VeilCefDownloadsBlocked(browser.NativeHandle) > CefNative.VeilCefBlobDownloadsBlocked(browser.NativeHandle))
            {
                phase = 6;
                Trace("Observed rejected HTTP and Blob downloads, geolocation and media permissions");
                Trace("Observed mandatory native popup denial");
                Trace($"Verified 98304 response bytes in memory. Blocked attachment passed {CefNative.VeilCefAttachmentFilteredBytes(browser.NativeHandle)} bytes to the filter.");
                if (fixtureVault is null)
                {
                    window.Close();
                    return;
                }
                vaultStream = new FixtureVault(fixtureVault, CefDevelopmentEngine.FixtureProfile, vaultScenario);
            }
            else if (phase == 6 && vaultStream is { Ready.IsCompleted: true })
            {
                var path = vaultScenario switch
                {
                    VaultScenario.Truncated => "/wpf-owned-truncated",
                    VaultScenario.Saturated => "/wpf-owned-saturated",
                    _ => "/wpf-owned-attachment",
                };
                var url = new Uri(new Uri(fixture), path).AbsoluteUri;
                if (vaultScenario == VaultScenario.Full &&
                    CefNative.VeilCefStartOwnedAttachment(browser.NativeHandle, url, vaultStream.ChunkCallback) != 0)
                {
                    Trace("CEF accepted an attachment without a page handoff");
                    window.Close();
                    return;
                }
                if (!vaultStream.Ready.Result || (vaultScenario == VaultScenario.Full
                    ? CefNative.VeilCefClickFixtureAttachment(browser.NativeHandle, url)
                    : CefNative.VeilCefStartOwnedAttachment(browser.NativeHandle, url, vaultStream.ChunkCallback)) == 0)
                {
                    Trace($"Could not start vault fixture for {path}");
                    window.Close();
                    return;
                }
                phase = vaultScenario == VaultScenario.Full ? 9 : 7;
            }
            else if (phase == 9 && CefNative.VeilCefPageAttachmentIntercepted(browser.NativeHandle) != 0)
            {
                Trace("Intercepted the page attachment before browser navigation reached HTTP");
                var url = new Uri(new Uri(fixture), "/wpf-owned-attachment").AbsoluteUri;
                if (CefNative.VeilCefStartOwnedAttachment(browser.NativeHandle, url, vaultStream!.ChunkCallback) == 0)
                {
                    window.Close();
                    return;
                }
                phase = 7;
            }
            else if (phase == 7 && CefNative.VeilCefOwnedAttachmentResult() != 0)
            {
                var nativeResult = CefNative.VeilCefOwnedAttachmentResult();
                var expected = vaultScenario switch
                {
                    VaultScenario.Truncated => -1,
                    VaultScenario.Saturated => -2,
                    _ => FixtureVault.BodySize,
                };
                vaultStream!.Complete(nativeResult == FixtureVault.BodySize);
                if (nativeResult != expected)
                {
                    Trace($"Unexpected CEF result for {vaultScenario}: {nativeResult}");
                    window.Close();
                    return;
                }
                phase = 8;
            }
            else if (phase == 8 && vaultStream is { Finished.IsCompleted: true })
            {
                var finished = vaultStream.Finished;
                if (!finished.IsCompletedSuccessfully || !finished.Result)
                {
                    Trace($"Rust vault fixture failed. Scenario: {vaultScenario}, native result: {CefNative.VeilCefOwnedAttachmentResult()}, error: {finished.Exception?.GetBaseException().Message}");
                    window.Close();
                }
                else if (vaultScenario != VaultScenario.Full)
                {
                    Trace(vaultScenario == VaultScenario.Truncated
                        ? "Rust vault aborted the truncated stream without publishing an object"
                        : "CEF canceled the saturated stream and Rust vault published no object");
                    vaultScenario = vaultScenario == VaultScenario.Truncated ? VaultScenario.Saturated : VaultScenario.Full;
                    vaultStream = new FixtureVault(fixtureVault!, CefDevelopmentEngine.FixtureProfile, vaultScenario);
                    phase = 6;
                }
                else
                {
                    result = 0;
                    Trace($"Rust vault streamed and restored all {FixtureVault.BodySize} bytes from the owned CEF request");
                    window.Close();
                }
            }
            else if (deadline.Elapsed > TimeSpan.FromSeconds(15))
            {
                Trace($"Fixture timed out at phase {phase}. Download decisions: {CefNative.VeilCefDownloadsBlocked(browser.NativeHandle)}, " +
                    $"Blob: {CefNative.VeilCefBlobDownloadsBlocked(browser.NativeHandle)}, permission prompts: {CefNative.VeilCefPermissionsDenied(browser.NativeHandle)}, " +
                    $"media: {CefNative.VeilCefMediaPermissionsDenied(browser.NativeHandle)}, response: {CefNative.VeilCefResponseMatches(browser.NativeHandle)}, " +
                    $"attachment bytes: {CefNative.VeilCefAttachmentFilteredBytes(browser.NativeHandle)}.");
                window.Close();
            }
        };
        pump.Start();
        app.Run(window);
        Trace("WPF dispatcher stopped");
        if (fixture is not null)
        {
            if (unexpectedProcessFailures != 0) return 31;
            Trace("Normal fixture shutdown emitted no unexpected process failure");
        }
        return result;
    }

    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { return true; }
    }
}
