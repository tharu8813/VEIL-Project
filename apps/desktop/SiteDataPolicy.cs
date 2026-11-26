namespace Veil.Desktop;

internal sealed record CookieDomainSummary(string Domain, int Count);
internal sealed record SiteDataSelection(string? Origin, string? CookieDomain, bool ClearAllCookies,
    bool ClearAllStorage, bool ClearDiskCache, bool InspectUsage = false);

[Flags]
internal enum EngineProfileDataKinds
{
    None = 0,
    Cookies = 1,
    DomStorage = 2,
    DiskCache = 4,
}

internal enum SiteDataScope { Unavailable, Product, SyntheticFixture }

// A conservative capability description. Native profile and authorization
// checks are still required at dispatch, even for an advertised operation.
internal sealed record SiteDataCapabilities(SiteDataScope Scope,
    bool ReadCookieDomains, bool DeleteCookieDomain, bool InspectOrigin, bool ClearOrigin,
    EngineProfileDataKinds ProfileKinds, bool AllowMixedProfileKinds)
{
    internal static readonly SiteDataCapabilities Unavailable = new(SiteDataScope.Unavailable,
        false, false, false, false, EngineProfileDataKinds.None, false);
    internal static readonly SiteDataCapabilities StandardWebView2 = new(SiteDataScope.Product,
        true, true, true, true, EngineProfileDataKinds.Cookies | EngineProfileDataKinds.DomStorage |
        EngineProfileDataKinds.DiskCache, true);
    internal static readonly SiteDataCapabilities CefTabFixture = new(SiteDataScope.SyntheticFixture,
        true, true, true, true, EngineProfileDataKinds.Cookies | EngineProfileDataKinds.DiskCache, false);

    internal bool SupportsProfile(EngineProfileDataKinds kinds)
    {
        const EngineProfileDataKinds known = EngineProfileDataKinds.Cookies |
            EngineProfileDataKinds.DomStorage | EngineProfileDataKinds.DiskCache;
        if (Scope == SiteDataScope.Unavailable || kinds == EngineProfileDataKinds.None ||
            (kinds & ~known) != 0 || (kinds & ~ProfileKinds) != 0) return false;
        return AllowMixedProfileKinds || kinds is EngineProfileDataKinds.Cookies or
            EngineProfileDataKinds.DomStorage or EngineProfileDataKinds.DiskCache;
    }

    internal bool Supports(SiteDataSelection selection)
    {
        if (Scope == SiteDataScope.Unavailable || !SiteDataPolicy.ValidSelection(selection)) return false;
        if (selection.CookieDomain is not null) return ReadCookieDomains && DeleteCookieDomain;
        if (selection.Origin is not null) return selection.InspectUsage ? InspectOrigin : ClearOrigin;
        return SupportsProfile(SiteDataPolicy.ProfileKinds(selection));
    }
}

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

    internal static bool ValidSelection(SiteDataSelection selection)
    {
        bool all = selection.ClearAllCookies || selection.ClearAllStorage || selection.ClearDiskCache;
        if (selection.CookieDomain is { } domain)
            return ValidDomain(domain) && selection.Origin is null && !all && !selection.InspectUsage;
        if (selection.Origin is { } origin)
            return Origin(origin) == origin && !all;
        return all && !selection.InspectUsage;
    }

    internal static EngineProfileDataKinds ProfileKinds(SiteDataSelection selection) =>
        (selection.ClearAllCookies ? EngineProfileDataKinds.Cookies : EngineProfileDataKinds.None) |
        (selection.ClearAllStorage ? EngineProfileDataKinds.DomStorage : EngineProfileDataKinds.None) |
        (selection.ClearDiskCache ? EngineProfileDataKinds.DiskCache : EngineProfileDataKinds.None);

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
