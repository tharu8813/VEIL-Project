namespace Veil.Desktop;

internal sealed record CookieDomainSummary(string Domain, int Count);
internal sealed record SiteDataSelection(string? Origin, string? CookieDomain, bool ClearAllCookies,
    bool ClearAllStorage, bool ClearDiskCache, bool InspectUsage = false);

/// <summary>Separate cookie domain and storage origin scopes. No suffix/wildcard matching.</summary>
internal static class SiteDataPolicy
{
    // Explicit allowlist: never pass "all" or cookie clearing to the origin-scoped CDP command.
    internal const string OriginStorageTypes = "local_storage,indexeddb,cache_storage,service_workers,websql,file_systems";

    internal static string? Origin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0)
            return null;
        try
        {
            return new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port)
                .Uri.GetLeftPart(UriPartial.Authority);
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException) { return null; }
    }

    internal static bool SameCookieDomain(string stored, string selected) =>
        string.Equals(stored, selected, StringComparison.OrdinalIgnoreCase);

    internal static bool ValidDomain(string? domain) =>
        !string.IsNullOrWhiteSpace(domain) && domain.Length <= 255 &&
        !domain.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) ||
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ||
            c is '/' or '\\' or '@' or '?' or '#');

    internal static IReadOnlyList<CookieDomainSummary> Summarize(IEnumerable<string> domains) =>
        domains.Where(ValidDomain).GroupBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CookieDomainSummary(group.Key, group.Count())).ToArray();

    internal static IReadOnlyList<string> Origins(IEnumerable<string> urls) =>
        urls.Select(Origin).OfType<string>().Distinct(StringComparer.Ordinal)
            .OrderBy(origin => origin, StringComparer.Ordinal).ToArray();
}
