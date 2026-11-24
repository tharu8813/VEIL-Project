namespace Veil.Desktop;

internal sealed record AddressSuggestion(string Title, string Url, bool Bookmark)
{
    public string SourceLabel => Bookmark ? "북마크" : "방문 기록";
}

/// <summary>Fixed HTTPS providers and local-only, bounded suggestions. Never issues network requests.</summary>
internal static class SearchPolicy
{
    internal const string Default = "duckduckgo";
    internal static readonly (string Id, string Name, string Prefix)[] Providers =
    [
        (Default, "DuckDuckGo", "https://duckduckgo.com/?q="),
        ("google", "Google", "https://www.google.com/search?q="),
        ("bing", "Bing", "https://www.bing.com/search?q="),
        ("naver", "네이버", "https://search.naver.com/search.naver?query="),
    ];

    internal static bool IsKnown(string? id) => id is not null && Providers.Any(p => p.Id == id);
    internal static string Normalize(string? id) => IsKnown(id) ? id! : Default;
    internal static string Name(string? id) => Providers.First(p => p.Id == Normalize(id)).Name;
    internal static Uri Search(string? id, string text) =>
        new(Providers.First(p => p.Id == Normalize(id)).Prefix + Uri.EscapeDataString(text));

    // Explicit search syntax is recognized only on submission. No lookup or network request while typing.
    internal static bool TryExplicitSearch(string input, string? defaultEngine, out Uri? result)
    {
        result = null;
        if (input.StartsWith('?'))
        {
            string query = input[1..].Trim();
            if (query.Length > 0) result = Search(defaultEngine, query);
            return true;
        }
        if (!input.StartsWith('@')) return false;
        int end = input.IndexOfAny([' ', '\t']);
        string id = (end < 0 ? input[1..] : input[1..end]).ToLowerInvariant();
        if (!IsKnown(id)) return false;
        string text = end < 0 ? "" : input[(end + 1)..].Trim();
        if (text.Length > 0) result = Search(id, text);
        return true;
    }

    private static Uri? Candidate(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsoluteUri.Length > 2048)
            return null;
        return uri;
    }

    internal static IReadOnlyList<AddressSuggestion> Suggest(BrowserData data, string input)
    {
        string query = input.Trim();
        if (data.LocalAddressSuggestions == false || query.Length is < 2 or > 256 ||
            query.Any(char.IsControl) || TryExplicitSearch(query, data.SearchEngine, out _)) return [];
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 12) return [];
        var best = new Dictionary<string, (AddressSuggestion Suggestion, int Rank, DateTimeOffset Visited)>(StringComparer.Ordinal);
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;

        // Compare complete phrases first, then token matches. A canonical URL can appear in
        // both history and bookmarks: rank all candidates before resolving duplicates.
        void Add(IEnumerable<PageEntry> entries, bool bookmark)
        {
            foreach (var entry in entries)
            {
                if (Candidate(entry.Url) is not { } uri) continue;
                string title = entry.Title.Length > 240 ? entry.Title[..240] : entry.Title;
                title = new string(title.Where(c => !char.IsControl(c) &&
                    char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
                string url = uri.AbsoluteUri;
                string host = uri.IdnHost.StartsWith("www.", cmp) ? uri.IdnHost[4..] : uri.IdnHost;
                string shortUrl = host + uri.AbsolutePath;
                int rank = host.Equals(query, cmp) || url.Equals(query, cmp) ? 0 :
                    host.StartsWith(query, cmp) || url.StartsWith(query, cmp) || shortUrl.StartsWith(query, cmp) ? 1 :
                    title.StartsWith(query, cmp) ? 2 :
                    url.Contains(query, cmp) || title.Contains(query, cmp) ? 3 :
                    words.All(word => title.Contains(word, cmp) || url.Contains(word, cmp) || host.Contains(word, cmp)) ? 4 : -1;
                if (rank < 0) continue;
                var suggestion = new AddressSuggestion(string.IsNullOrWhiteSpace(title) ? uri.IdnHost : title, url, bookmark);
                if (!best.TryGetValue(url, out var old))
                    best[url] = (suggestion, rank, entry.Visited);
                else
                {
                    // A saved bookmark retains its source/title, while a matching history title
                    // can improve its rank. Do not display two rows for the same URL.
                    var preferred = old.Suggestion.Bookmark && !bookmark ? old.Suggestion :
                        bookmark && !old.Suggestion.Bookmark ? suggestion :
                        rank < old.Rank || (rank == old.Rank && entry.Visited > old.Visited) ? suggestion : old.Suggestion;
                    best[url] = (preferred, Math.Min(rank, old.Rank), entry.Visited > old.Visited ? entry.Visited : old.Visited);
                }
            }
        }
        Add(data.Bookmarks.Take(20000), true);
        Add(data.History.Take(4000), false);
        return best.Values.OrderBy(item => item.Rank).ThenByDescending(item => item.Suggestion.Bookmark)
            .ThenByDescending(item => item.Visited).ThenBy(item => item.Suggestion.Url, StringComparer.Ordinal)
            .Take(8).Select(item => item.Suggestion).ToArray();
    }
}
