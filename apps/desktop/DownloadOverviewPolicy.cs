namespace Veil.Desktop;

internal sealed record DownloadIssue(Guid Id, string Name, string Outcome, string? Detail, DateTimeOffset At);
internal sealed record DownloadOverviewRow(string Id, string Name, string Outcome, string? Detail, DateTimeOffset At, long? Bytes, string? Host)
{
    internal string Summary => Outcome + (Detail is null ? "" : " · " + Detail) +
        (Bytes is long bytes ? " · " + VaultPresentation.Size(bytes) : "") +
        (Host is null ? "" : " · " + Host) + $" · {At.LocalDateTime:yyyy-MM-dd HH:mm}";
}
internal enum DownloadFilter { All, Stored, Failed, Blocked, Cancelled, Recovered }
internal static class DownloadOverviewPolicy
{
    internal static IReadOnlyList<DownloadOverviewRow> Filter(IEnumerable<VaultEntry> stored,
        IEnumerable<DownloadIssue> issues, string query, DownloadFilter filter, VaultSort sort)
    {
        query = query.Trim();
        var rows = stored.Select(e => new DownloadOverviewRow(e.Id, e.Name, DownloadVault.Stored,
                e.Recovered ? "복구됨" : null, e.SavedAt, e.Recovered ? null : e.Bytes, e.SourceHost))
            .Concat(issues.Select(i => new DownloadOverviewRow("issue-" + i.Id.ToString("N"), i.Name,
                i.Outcome, i.Detail, i.At, null, null)))
            .Where(r => filter switch
            {
                DownloadFilter.Stored => r.Outcome == DownloadVault.Stored,
                DownloadFilter.Failed => r.Outcome == DownloadVault.Failed,
                DownloadFilter.Blocked => r.Outcome == DownloadVault.Blocked,
                DownloadFilter.Cancelled => r.Outcome == DownloadVault.Cancelled,
                DownloadFilter.Recovered => r.Outcome == DownloadVault.Stored && r.Detail == "복구됨",
                _ => true,
            })
            .Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.Outcome.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (r.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.Host?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        var ordered = sort switch
        {
            VaultSort.Oldest => rows.OrderBy(r => r.At),
            VaultSort.Name => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            VaultSort.Largest => rows.OrderBy(r => r.Bytes is null).ThenByDescending(r => r.Bytes),
            VaultSort.Smallest => rows.OrderBy(r => r.Bytes is null).ThenBy(r => r.Bytes),
            _ => rows.OrderByDescending(r => r.At),
        };
        return ordered.ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }
}
