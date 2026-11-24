using System.IO;
using System.Text;

namespace Veil.Desktop;

internal enum VaultSort { Recent, Oldest, Name, Largest, Smallest }
internal enum VaultPreviewKind { None, Text, Png, Jpeg, Pdf }

internal static class VaultPresentation
{
    internal const int PreviewLimit = 256 * 1024;
    internal const int ImagePreviewLimit = 8 * 1024 * 1024;

    internal static IReadOnlyList<VaultEntry> Filter(IEnumerable<VaultEntry> entries, string query, VaultSort sort, bool recoveredOnly)
    {
        query = query.Trim();
        var matches = entries.Where(entry => (!recoveredOnly || entry.Recovered) &&
            (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             (entry.SourceHost?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)));
        var ordered = sort switch
        {
            VaultSort.Oldest => matches.OrderBy(entry => entry.SavedAt),
            VaultSort.Name => matches.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            VaultSort.Largest => matches.OrderByDescending(entry => entry.Bytes),
            VaultSort.Smallest => matches.OrderBy(entry => entry.Bytes),
            _ => matches.OrderByDescending(entry => entry.SavedAt),
        };
        return ordered.ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }

    internal static VaultPreviewKind PreviewKind(VaultEntry entry)
    {
        if (entry.Recovered || entry.Bytes < 0) return VaultPreviewKind.None;
        return Path.GetExtension(entry.Name).ToLowerInvariant() switch
        {
            ".txt" when entry.Bytes <= PreviewLimit => VaultPreviewKind.Text,
            ".png" when entry.Bytes > 0 && entry.Bytes <= ImagePreviewLimit => VaultPreviewKind.Png,
            ".jpg" or ".jpeg" when entry.Bytes > 0 && entry.Bytes <= ImagePreviewLimit => VaultPreviewKind.Jpeg,
            ".pdf" when VaultPdfPreview.Enabled && entry.Bytes > 0 && entry.Bytes <= ImagePreviewLimit => VaultPreviewKind.Pdf,
            _ => VaultPreviewKind.None,
        };
    }

    internal static bool CanPreview(VaultEntry entry) => PreviewKind(entry) != VaultPreviewKind.None;

    internal static string DecodeText(byte[] bytes)
    {
        if (bytes.Length > PreviewLimit) throw new InvalidDataException("Preview size limit.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        if (text.Any(c => (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ||
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new InvalidDataException("Unsupported text content.");
        return text;
    }

    internal static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes:N0} B" : bytes < 1048576 ? $"{bytes / 1024d:N1} KiB" :
        bytes < 1073741824 ? $"{bytes / 1048576d:N1} MiB" : $"{bytes / 1073741824d:N2} GiB";

    internal static string ExportName(VaultEntry entry)
    {
        string name = Path.GetFileName(entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' ') || name is "." or ".." ||
            name.Any(char.IsControl) || name.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            return "VEIL-download.bin";
        string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             "123456789¹²³".Contains(stem[3])))
            return "VEIL-download.bin";
        return name;
    }
}
