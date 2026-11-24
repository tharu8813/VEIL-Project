using System.Collections.ObjectModel;
using System.IO;
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
    internal BrowserSession(string root, DownloadVault vault)
    {
        Root = root;
        persistentStatePath = Path.Combine(root, "browser-state.json");
        Data = BrowserData.Load(persistentStatePath);
        // ADR-0006: an unusable proxy setting refuses to start instead of silently connecting directly.
        Network = Data.Network ?? NetworkSettings.Standard;
        if (Network.Problem is { } problem) throw new System.Text.Json.JsonException(problem);
        Vault = vault;
        Vault.ConfigureNetwork(Network);
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
    /// <summary>Proxy path check result for the UI; null until checked or in standard mode.</summary>
    internal bool? ProxyReachable { get; set; }
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
    internal Stack<ClosedTab> ClosedTabs { get; } = new();
    internal bool RestoringClosedTab { get; set; }
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
    internal List<(DateTimeOffset At, string Text)> RecentDownloadIssues { get; } = [];
    internal ObservableCollection<BrowserDownload> ActiveDownloads { get; } = [];
    internal List<BrowserWindow> Windows { get; } = [];
    // The first release has exactly one production backend. No fallback or runtime switching.
    internal IBrowserEngine EngineHost { get; } = new WebView2BrowserEngine();
    internal CoreWebView2Environment? Engine => (EngineHost as WebView2BrowserEngine)?.Environment;
    internal bool IsEnding { get => MainSession.ending; set => MainSession.ending = value; }
    /// <summary>The shared engine's browser process exited; no window can use it any more.</summary>
    internal bool EngineLost { get => MainSession.engineLost; set => MainSession.engineLost = value; }

    /// <summary>Writes the open windows. Skipped while any window is still restoring its tabs.</summary>
    internal void Save()
    {
        if (IsPrivate) return;
        var open = Windows.Where(window => !window.IsClosing).ToList();
        if (open.Count == 0 || open.Any(window => window.IsRestoring)) return;
        var (tabs, selected) = open[0].Snapshot();
        Data.Tabs = tabs;
        Data.Selected = selected;
        Data.PinnedCount = open[0].PinnedTabCount;
        Data.Windows = open.Count == 1 ? null :
            open.Skip(1).Select(window =>
            {
                var snapshot = window.Snapshot();
                return new WindowTabs(snapshot.Tabs, snapshot.Selected, window.PinnedTabCount);
            }).ToList();
        Data.Save(StatePath);
    }
}
