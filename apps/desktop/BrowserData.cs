using System.IO;
using System.Text.Json;

namespace Veil.Desktop;

internal sealed record PageEntry(string Title, string Url, DateTimeOffset Visited, Guid? FolderId = null)
{
    public override string ToString() => $"{Title}   ·   {Url}";
}

/// <summary>Tabs of an additional browser window.</summary>
internal sealed record WindowTabs(List<string> Tabs, int Selected, int PinnedCount = 0);
internal sealed record ClosedTab(string Url, bool Pinned);

internal sealed class BrowserData
{
    public List<PageEntry> Bookmarks { get; init; } = [];
    public List<PageEntry> History { get; init; } = [];
    public List<BookmarkFolder> BookmarkFolders { get; init; } = [];
    public List<string> Tabs { get; set; } = [];
    public int Selected { get; set; }
    public int PinnedCount { get; set; }
    /// <summary>Windows other than the primary one; null when only one window was open.</summary>
    public List<WindowTabs>? Windows { get; set; }
    /// <summary>Network mode chosen by the user (ADR-0006); null means standard protection.</summary>
    public NetworkSettings? Network { get; set; }
    /// <summary>Security policy (ADR-0007); null means the safe defaults (SmartScreen on).</summary>
    public SecuritySettings? Security { get; set; }

    public List<SitePermissionRule>? SitePermissions { get; set; }
    public string? SearchEngine { get; set; }
    public bool? LocalAddressSuggestions { get; set; }
    public bool ShowBookmarksBar { get; set; } = true;

    internal static BrowserData Load(string path)
    {
        BrowserData? data;
        try
        {
            using var stream = File.OpenRead(path);
            data = JsonSerializer.Deserialize<BrowserData>(stream);
        }
        catch (FileNotFoundException)
        {
            if (File.Exists(path + ".new"))
                throw new JsonException("A write was interrupted before any browser state was committed.");
            return new BrowserData();
        }
        if (data is null || data.Bookmarks is null || data.History is null || data.Tabs is null ||
            data.Bookmarks.Concat(data.History).Any(entry => entry?.Title is null || entry.Url is null) ||
            data.Tabs.Any(url => url is null) ||
            (data.Windows is not null && data.Windows.Any(window => window?.Tabs is null || window.Tabs.Any(url => url is null))))
            throw new JsonException("Invalid VEIL developer profile state.");
        if (!BookmarkPolicy.Valid(data.BookmarkFolders, data.Bookmarks))
            throw new JsonException("Invalid bookmark folder hierarchy.");
        // The prefix count is optional for older profiles. Invalid counts never pin unrelated tabs.
        if (data.PinnedCount < 0 || data.PinnedCount > data.Tabs.Count) data.PinnedCount = 0;
        if (data.Windows is { } windows)
            data.Windows = windows.Select(window => window.PinnedCount < 0 || window.PinnedCount > window.Tabs.Count
                ? window with { PinnedCount = 0 } : window).ToList();
        // Missing policy means deny. Corrupted rules must never grant a permission.
        if (!SitePermissionPolicy.ValidRules(data.SitePermissions))
            data.SitePermissions = null;
        if (!SearchPolicy.IsKnown(data.SearchEngine)) data.SearchEngine = null;
        return data;
    }

    // CaptureOpenTabs replaces the layout collections; preserve the previous
    // references and scalar values until the combined library write succeeds.
    internal bool TryCommitTabLayout(Func<bool> commit)
    {
        var previous = (Tabs, Selected, PinnedCount, Windows);
        bool committed = false;
        try
        {
            committed = commit();
            return committed;
        }
        finally
        {
            if (!committed)
                (Tabs, Selected, PinnedCount, Windows) = previous;
        }
    }

    // Search provider and local suggestions are a single preference transaction.
    // The session supplies authorization and the existing profile writer.
    internal bool TryCommitSearchSettings(string? engine, bool localSuggestions,
        Func<bool> authorized, Func<bool> save)
    {
        if (!SearchPolicy.IsKnown(engine) || !authorized()) return false;
        var previous = (SearchEngine, LocalAddressSuggestions);
        bool committed = false;
        try
        {
            SearchEngine = engine == SearchPolicy.Default ? null : engine;
            LocalAddressSuggestions = localSuggestions ? null : false;
            committed = authorized() && save();
            return committed;
        }
        finally
        {
            if (!committed)
                (SearchEngine, LocalAddressSuggestions) = previous;
        }
    }

    // Accept a detached copy of the UI draft; never retain a mutable dialog list.
    internal bool TryCommitSitePermissions(IReadOnlyList<SitePermissionRule> rules,
        Func<bool> authorized, Func<bool> save)
    {
        if (!authorized()) return false;
        var candidate = rules.ToList();
        if (!SitePermissionPolicy.ValidRules(candidate)) return false;
        var previous = SitePermissions;
        bool committed = false;
        try
        {
            SitePermissions = candidate.Count == 0 ? null : candidate;
            committed = authorized() && save();
            return committed;
        }
        finally
        {
            if (!committed) SitePermissions = previous;
        }
    }

    internal void Save(string path)
    {
        string temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this);
            stream.Flush(flushToDisk: true);
        }
        // Both paths use the profile directory's ACL. A same-directory rename
        // publishes the new data without merging the old file's metadata.
        File.Move(temporary, path, overwrite: true);
    }
}
