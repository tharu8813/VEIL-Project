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
    internal BrowserSession(string root, DownloadVault vault)
    {
        Root = root;
        StatePath = Path.Combine(root, "browser-state.json");
        Data = BrowserData.Load(StatePath);
        // ADR-0006: an unusable proxy setting refuses to start instead of silently connecting directly.
        Network = Data.Network ?? NetworkSettings.Standard;
        if (Network.Problem is { } problem) throw new System.Text.Json.JsonException(problem);
        Vault = vault;
        Vault.ConfigureNetwork(Network);
        // ADR-0007: one security policy for every view of this profile's engine (shared user data folder).
        Security = new SecurityManager(Data.Security, () => Windows.SelectMany(window => window.CoreViews()));
        // ADR-0008: signed local threat list on the profile volume; the vault uses the same engines.
        Security.LoadThreatList(Path.Combine(root, "security"));
        Vault.ConfigureThreats(Security.Threats);
    }

    internal SecurityManager Security { get; }

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
    internal string StatePath { get; }
    internal BrowserData Data { get; }
    internal Stack<string> ClosedTabs { get; } = new();
    internal List<string> Downloads { get; } = [];
    internal ObservableCollection<BrowserDownload> ActiveDownloads { get; } = [];
    internal List<BrowserWindow> Windows { get; } = [];
    internal CoreWebView2Environment? Engine { get; set; }
    internal bool IsEnding { get; set; }
    /// <summary>The shared engine's browser process exited; no window can use it any more.</summary>
    internal bool EngineLost { get; set; }

    /// <summary>Writes the open windows. Skipped while any window is still restoring its tabs.</summary>
    internal void Save()
    {
        var open = Windows.Where(window => !window.IsClosing).ToList();
        if (open.Count == 0 || open.Any(window => window.IsRestoring)) return;
        var (tabs, selected) = open[0].Snapshot();
        Data.Tabs = tabs;
        Data.Selected = selected;
        Data.Windows = open.Count == 1 ? null :
            open.Skip(1).Select(window => window.Snapshot()).Select(s => new WindowTabs(s.Tabs, s.Selected)).ToList();
        Data.Save(StatePath);
    }
}
