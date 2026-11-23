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
    internal BrowserSession(string root)
    {
        Root = root;
        StatePath = Path.Combine(root, "browser-state.json");
        Data = BrowserData.Load(StatePath);
    }

    internal string Root { get; }
    internal string StatePath { get; }
    internal BrowserData Data { get; }
    internal Stack<string> ClosedTabs { get; } = new();
    internal List<string> Downloads { get; } = [];
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
