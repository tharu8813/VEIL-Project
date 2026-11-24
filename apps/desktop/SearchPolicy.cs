namespace Veil.Desktop;

internal sealed record AddressSuggestion(string Title, string Url);

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
            query.Any(char.IsControl)) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<(AddressSuggestion Suggestion, int Rank, bool Bookmark, DateTimeOffset Visited)>();
        // History is newest-first. Bound the work and keep bookmarks ahead for duplicate URLs.
        void Add(IEnumerable<PageEntry> entries, bool bookmark)
        {
            foreach (var entry in entries)
            {
                if (Candidate(entry.Url) is not { } uri) continue;
                string title = entry.Title.Length > 240 ? entry.Title[..240] : entry.Title;
                string url = uri.AbsoluteUri;
                int rank = uri.IdnHost.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 :
                    title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 :
                    url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    title.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
                if (rank < 0 || !seen.Add(url)) continue;
                // Strip formatting/control characters from untrusted page titles (URL is always visible).
                title = new string(title.Where(c => !char.IsControl(c) &&
                    char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
                matches.Add((new AddressSuggestion(string.IsNullOrWhiteSpace(title) ? uri.IdnHost : title, url),
                    rank, bookmark, entry.Visited));
            }
        }
        Add(data.Bookmarks.Take(2000), true);
        Add(data.History.Take(4000), false);
        return matches.OrderBy(item => item.Rank).ThenByDescending(item => item.Bookmark)
            .ThenByDescending(item => item.Visited).Take(8).Select(item => item.Suggestion).ToArray();
    }
}
