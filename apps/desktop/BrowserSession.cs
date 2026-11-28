using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

/// <summary>
/// State shared by every window of one profile: the engine, bookmarks/history, closed tabs and
/// cancelled downloads. Windows register themselves; the first open window is persisted as the
/// primary tab list and the others as additional windows.
/// </summary>
internal sealed class BrowserSession
{
    private readonly BrowserSession? parent;
    private readonly List<BrowserSession> privateSessions = [];
    private bool ending, engineLost;
    private readonly Dispatcher libraryDispatcher = Dispatcher.CurrentDispatcher;
    internal BrowserSession(string root, DownloadVault vault)
    {
        Root = root;
        persistentStatePath = Path.Combine(root, "browser-state.json");
        Data = BrowserData.Load(persistentStatePath);
        // ADR-0006: an unusable proxy setting refuses to start instead of silently connecting directly.
        Network = Data.Network ?? NetworkSettings.Standard;
        if (Network.Problem is { } problem) throw new System.Text.Json.JsonException(problem);
        Vault = vault;
        Vault.ConfigureNetwork(Network, HttpAllowed);
        // ADR-0007: one security policy for every view of this profile's engine (shared user data folder).
        Security = new SecurityManager(Data.Security, () => AllWindows.SelectMany(window => window.CoreViews()));
        // ADR-0008: signed local threat list on the profile volume; the vault uses the same engines.
        Security.LoadThreatList(Path.Combine(root, "security"));
        Vault.ConfigureThreats(Security.Threats);
    }

    private BrowserSession(BrowserSession owner)
    {
        parent = owner.MainSession;
        Root = parent.Root;
        Data = new BrowserData { SearchEngine = parent.Data.SearchEngine, LocalAddressSuggestions = false,
            ShowBookmarksBar = false, Network = parent.Network };
        Network = parent.Network;
        Vault = parent.Vault; // private commands never access the shared vault
        Security = parent.Security;
        EngineHost = parent.EngineHost;
        PrivateProfileName = "veil-private-" + Guid.NewGuid().ToString("N");
        parent.privateSessions.Add(this);
    }

    internal BrowserSession MainSession => parent ?? this;
    internal string? PrivateProfileName { get; }
    internal bool IsPrivate => PrivateProfileName is not null;
    internal IEnumerable<BrowserWindow> AllWindows => MainSession.Windows.Concat(
        MainSession.privateSessions.SelectMany(item => item.Windows));
    internal BrowserSession CreatePrivate() => new(MainSession);
    internal void ReleasePrivate()
    {
        if (parent is not null && Windows.Count == 0) parent.privateSessions.Remove(this);
    }

    internal SecurityManager Security { get; }
    // Shared across windows to prevent stacked prompts and edits during a pending decision.
    internal bool PermissionUiOpen { get; set; }
    internal bool PasswordUiOpen { get; set; }
    internal bool SettingsUiOpen { get; set; }
    internal bool VaultUiOpen { get; set; }

    /// <summary>Network mode applied to this run's engine (changes take effect after a restart).</summary>
    internal NetworkSettings Network { get; }
    // The private windows use the same engine and applied proxy as the main session.
    private DispatcherTimer? proxyTimer;
    private CancellationTokenSource? proxyMonitor;
    private bool proxyChecking;
    private bool? proxyReachable;
    private DateTimeOffset? proxyCheckedAt;
    internal bool? ProxyReachable => MainSession.proxyReachable;
    internal DateTimeOffset? ProxyCheckedAt => MainSession.proxyCheckedAt;

    internal Task StartProxyMonitorAsync() => MainSession.StartMainProxyMonitorAsync();

    private async Task StartMainProxyMonitorAsync()
    {
        if (Network.Mode != NetworkMode.Proxy || proxyMonitor is not null || IsEnding || EngineLost) return;
        proxyMonitor = new CancellationTokenSource();
        proxyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        proxyTimer.Tick += ProxyMonitorTick;
        proxyTimer.Start();
        try { await RefreshProxyAsync(); }
        catch (Exception) { StopProxyMonitor(); }
    }

    private async void ProxyMonitorTick(object? sender, EventArgs args)
    {
        try { await RefreshProxyAsync(); }
        catch (Exception) { StopProxyMonitor(); }
    }

    private async Task RefreshProxyAsync()
    {
        if (proxyMonitor is not { } monitor || proxyChecking || IsEnding || EngineLost) return;
        proxyChecking = true;
        proxyReachable = null;
        proxyCheckedAt = null;
        NotifyProxyWindows();
        try
        {
            bool reachable = await NetworkPolicy.ProxyReachableAsync(Network, TimeSpan.FromSeconds(3), monitor.Token);
            if (!ReferenceEquals(proxyMonitor, monitor) || IsEnding || EngineLost) return;
            proxyReachable = reachable;
            proxyCheckedAt = DateTimeOffset.Now;
        }
        finally
        {
            if (ReferenceEquals(proxyMonitor, monitor))
            {
                proxyChecking = false;
                NotifyProxyWindows();
            }
        }
    }

    private void NotifyProxyWindows()
    {
        foreach (var window in AllWindows.ToArray())
            if (!window.IsClosing) window.UpdateNetworkIndicator();
    }

    internal void StopProxyMonitorIfIdle()
    {
        var main = MainSession;
        if (!main.AllWindows.Any(window => !window.IsClosing)) main.StopProxyMonitor();
    }

    private void StopProxyMonitor()
    {
        if (proxyMonitor is null) return;
        if (proxyTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= ProxyMonitorTick;
            proxyTimer = null;
        }
        var monitor = proxyMonitor;
        proxyMonitor = null;
        monitor?.Cancel();
        monitor?.Dispose();
        proxyChecking = false;
        proxyReachable = null;
        proxyCheckedAt = null;
        NotifyProxyWindows();
    }
    /// <summary>Hosts the user explicitly allowed over plain HTTP in this run (HTTPS-first exceptions).</summary>
    internal HashSet<string> HttpAllowed { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Hosts the user explicitly chose to open despite a VEIL threat warning, for this run only.</summary>
    internal HashSet<string> ThreatAllowed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Encrypted download vault shared by all windows of this profile.</summary>
    internal DownloadVault Vault { get; }
    internal SemaphoreSlim VaultOpenGate { get; } = new(1, 1);

    internal string Root { get; }
    private readonly string? persistentStatePath;
    internal string StatePath => persistentStatePath ?? throw new InvalidOperationException("Private sessions have no state file.");
    internal BrowserData Data { get; }

    // The existing mounted profile owns the path. An engine or library UI
    // cannot choose a destination or borrow the main session's private data.
    internal bool TrySaveLibrary(Func<bool> authorized, bool captureTabs = false)
    {
        libraryDispatcher.VerifyAccess();
        if (IsPrivate || IsEnding || EngineLost || !EngineHost.IsReady || persistentStatePath is null ||
            !authorized()) return false;
        if (IsPrivate || IsEnding || EngineLost || !EngineHost.IsReady) return false;
        try
        {
            bool Commit()
            {
                // History and tab layout share one state file. During restoration
                // or transfer retain the last complete layout, not a partial snapshot.
                if (captureTabs) CaptureOpenTabs();
                if (!authorized() || IsPrivate || IsEnding || EngineLost || !EngineHost.IsReady ||
                    persistentStatePath is null) return false;
                Data.Save(persistentStatePath);
                return true;
            }
            return captureTabs ? Data.TryCommitTabLayout(Commit) : Commit();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    internal bool TryCommitSearchSettings(string? engine, bool localSuggestions, Func<bool> authorized)
    {
        libraryDispatcher.VerifyAccess();
        bool Authorized() => !IsPrivate && !IsEnding && !EngineLost && EngineHost.IsReady &&
            persistentStatePath is not null && authorized();
        // Reuse the protected-profile writer used by the library, without
        // capturing tabs or accepting a path from the settings dialog.
        return Data.TryCommitSearchSettings(engine, localSuggestions, Authorized,
            () => TrySaveLibrary(Authorized));
    }

    internal bool TryCommitSitePermissions(IReadOnlyList<SitePermissionRule> rules, Func<bool> authorized)
    {
        libraryDispatcher.VerifyAccess();
        bool Authorized() => !IsPrivate && !IsEnding && !EngineLost && EngineHost.IsReady &&
            persistentStatePath is not null && authorized();
        return Data.TryCommitSitePermissions(rules, Authorized,
            () => TrySaveLibrary(Authorized));
    }

    internal bool TryCommitNetworkSettings(NetworkSettings chosen, Func<bool> authorized)
    {
        libraryDispatcher.VerifyAccess();
        bool Authorized() => !IsPrivate && !IsEnding && !EngineLost && EngineHost.IsReady &&
            persistentStatePath is not null && authorized();
        // Do not modify Network, the vault transport or the live engine.
        return Data.TryCommitNetworkSettings(chosen, Authorized,
            () => TrySaveLibrary(Authorized));
    }

    internal bool TryCommitSecuritySettings(SecuritySettings chosen, Func<bool> authorized)
    {
        libraryDispatcher.VerifyAccess();
        bool Authorized() => !IsPrivate && !IsEnding && !EngineLost && EngineHost.IsReady &&
            persistentStatePath is not null && authorized();
        return Data.TryCommitSecuritySettings(chosen, Authorized,
            () => TrySaveLibrary(Authorized));
    }

    internal Stack<ClosedTab> ClosedTabs { get; } = new();
    internal bool RestoringClosedTab { get; set; }
    internal int TabTransferDepth { get; set; }
    internal void RememberClosedTab(ClosedTab tab)
    {
        if (IsPrivate || !BrowserWindow.IsWebUrl(tab.Url)) return;
        ClosedTabs.Push(tab);
        if (ClosedTabs.Count <= 50) return;
        var recent = ClosedTabs.Take(50).Reverse().ToArray();
        ClosedTabs.Clear();
        foreach (var entry in recent) ClosedTabs.Push(entry);
    }
    internal List<string> Downloads { get; } = [];
    internal List<DownloadIssue> RecentDownloadIssues { get; } = [];
    internal ObservableCollection<BrowserDownload> ActiveDownloads { get; } = [];
    internal bool InlineCapturePending { get; set; }
    internal List<BrowserWindow> Windows { get; } = [];
    // The first release has exactly one production backend. No fallback or runtime switching.
    internal IBrowserEngine EngineHost { get; } = new WebView2BrowserEngine();
    internal CoreWebView2Environment? Engine => (EngineHost as WebView2BrowserEngine)?.Environment;
    internal bool IsEnding { get => MainSession.ending; set { MainSession.ending = value; if (value) MainSession.StopProxyMonitor(); } }
    /// <summary>The shared engine's browser process exited; no window can use it any more.</summary>
    internal bool EngineLost { get => MainSession.engineLost; set => MainSession.engineLost = value; }

    /// <summary>Writes a complete open-window snapshot; false means no write was committed.</summary>
    internal bool Save()
    {
        libraryDispatcher.VerifyAccess();
        if (IsPrivate || IsEnding || TabTransferDepth != 0 || persistentStatePath is null) return false;
        // Engine loss is intentionally allowed here: the crash handler can save
        // the last managed tab layout before ending the session, without engine calls.
        return Data.TryCommitTabLayout(() =>
        {
            if (!CaptureOpenTabs() || IsEnding) return false;
            Data.Save(persistentStatePath);
            return true;
        });
    }

    private bool CaptureOpenTabs()
    {
        if (IsPrivate || TabTransferDepth != 0) return false;
        var open = Windows.Where(window => !window.IsClosing).ToList();
        if (open.Count == 0 || open.Any(window => window.IsRestoring)) return false;
        var (tabs, selected) = open[0].Snapshot();
        int pinned = open[0].PinnedTabCount;
        var additional = open.Count == 1 ? null :
            open.Skip(1).Select(window =>
            {
                var snapshot = window.Snapshot();
                return new WindowTabs(snapshot.Tabs, snapshot.Selected, window.PinnedTabCount);
            }).ToList();
        // Publish only after every window has supplied its complete snapshot.
        (Data.Tabs, Data.Selected, Data.PinnedCount, Data.Windows) =
            (tabs, selected, pinned, additional);
        return true;
    }
}
