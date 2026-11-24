using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Veil.Desktop;

/// <summary>Inert Netscape bookmark HTML interchange. Never renders HTML, loads URLs or reads browser profiles.</summary>
internal static class BookmarkHtml
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    private const int MaxItems = 20000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Tokens = new(
        """<!--[\s\S]*?-->|<(?<end>/)?(?<tag>[A-Za-z][A-Za-z0-9:_-]*)(?=[\s/>])(?<attrs>(?:[^"'<>]|"[^"]*"|'[^']*')*)>|[^<]+|<""",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Attributes = new(
        """(?<key>[^\s=/'"<>]+)(?:\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s"'<>]+)))?""",
        RegexOptions.CultureInvariant, RegexTimeout);

    internal sealed record Import(List<BookmarkFolder> Folders, List<PageEntry> Entries, int Skipped);

    internal static Import Read(string path, IEnumerable<PageEntry> existing)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new InvalidDataException("Bookmark HTML exceeds the 8 MiB limit.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Bookmark file changed during read.");
        return Parse(new UTF8Encoding(false, true).GetString(bytes), existing);
    }

    internal static Import Parse(string html, IEnumerable<PageEntry> existing)
    {
        if (html.Length > MaxBytes || !html.Contains("NETSCAPE-Bookmark-file-1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a Netscape bookmark HTML file.");
        var folders = new List<BookmarkFolder>();
        var entries = new List<PageEntry>();
        var seen = existing.Select(e => Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : e.Url).ToHashSet(StringComparer.Ordinal);
        var stack = new Stack<Guid?>();
        Guid? parent = null, pending = null;
        string? capture = null, href = null;
        var label = new StringBuilder();
        int skipped = 0, count = 0;
        bool sawList = false;
        foreach (Match token in Tokens.Matches(html))
        {
            string tag = token.Groups["tag"].Value.ToLowerInvariant();
            bool end = token.Groups["end"].Success;
            if (tag.Length == 0)
            {
                if (capture is not null && !token.Value.StartsWith("<!--", StringComparison.Ordinal))
                {
                    if (label.Length + token.Length > 8192) throw new InvalidDataException("Bookmark label too long.");
                    label.Append(token.Value);
                }
                continue;
            }
            if (tag is "script" or "style" or "iframe" or "object" or "embed" or "template")
                throw new InvalidDataException("Unexpected active markup in bookmark file.");
            if (!end && (tag is "h3" or "a"))
            {
                if (capture is not null || ++count > MaxItems) throw new InvalidDataException("Invalid bookmark item.");
                capture = tag;
                label.Clear();
                href = tag == "a" ? Href(token.Groups["attrs"].Value) : null;
                if (tag == "a") pending = null;
                continue;
            }
            if (end && tag == capture)
            {
                string name = Clean(WebUtility.HtmlDecode(label.ToString()));
                if (capture == "h3")
                {
                    if (name.Length == 0) name = "폴더";
                    if (name.Length > 120) name = name[..120].Trim();
                    var folder = new BookmarkFolder(Guid.NewGuid(), name, parent);
                    folders.Add(folder);
                    pending = folder.Id;
                }
                else if (href is not null && BrowserWindow.IsWebUrl(href) && href.Length <= 8192 &&
                    Uri.TryCreate(href, UriKind.Absolute, out var uri) && seen.Add(uri.AbsoluteUri))
                {
                    entries.Add(new PageEntry(name.Length == 0 ? uri.Host : name, uri.AbsoluteUri, DateTimeOffset.Now, parent));
                }
                else skipped++;
                capture = href = null;
                continue;
            }
            if (capture is not null) continue;
            if (tag == "dl")
            {
                if (!end)
                {
                    sawList = true;
                    stack.Push(parent);
                    if (stack.Count > BookmarkPolicy.MaxDepth + 1) throw new InvalidDataException("Folder nesting too deep.");
                    if (pending is Guid id) parent = id;
                    pending = null;
                }
                else
                {
                    if (stack.Count == 0) throw new InvalidDataException("Unbalanced bookmark folders.");
                    parent = stack.Pop();
                    pending = null;
                }
            }
        }
        if (!sawList || stack.Count != 0 || capture is not null || !BookmarkPolicy.Valid(folders, entries))
            throw new InvalidDataException("Invalid bookmark hierarchy.");
        return new Import(folders, entries, skipped);
    }

    private static string? Href(string attrs)
    {
        foreach (Match attr in Attributes.Matches(attrs))
            if (attr.Groups["key"].Value.Equals("href", StringComparison.OrdinalIgnoreCase))
                return attr.Groups["value"].Success ? WebUtility.HtmlDecode(attr.Groups["value"].Value).Trim() : null;
        return null;
    }

    private static string Clean(string value) =>
        new string(value.Where(c => !char.IsControl(c) &&
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();

    internal static byte[] Export(List<BookmarkFolder> folders, List<PageEntry> entries)
    {
        if (!BookmarkPolicy.Valid(folders, entries)) throw new InvalidDataException("Invalid bookmark hierarchy.");
        var output = new StringBuilder("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n");
        var byParent = folders.ToLookup(f => f.ParentId ?? Guid.Empty);
        var byFolder = entries.Where(e => BrowserWindow.IsWebUrl(e.Url)).ToLookup(e => e.FolderId ?? Guid.Empty);
        void Add(Guid parent, int depth)
        {
            string pad = new(' ', depth * 4);
            output.Append(pad).AppendLine("<DL><p>");
            foreach (var folder in byParent[parent])
            {
                output.Append(pad).Append("    <DT><H3>").Append(WebUtility.HtmlEncode(folder.Name)).AppendLine("</H3>");
                Add(folder.Id, depth + 1);
            }
            foreach (var entry in byFolder[parent])
            {
                output.Append(pad).Append("    <DT><A HREF=\"").Append(WebUtility.HtmlEncode(entry.Url))
                    .Append("\">").Append(WebUtility.HtmlEncode(entry.Title)).AppendLine("</A>");
                if (output.Length > MaxBytes) throw new InvalidDataException("Bookmark export too large.");
            }
            output.Append(pad).AppendLine("</DL><p>");
        }
        Add(Guid.Empty, 0);
        byte[] bytes = Encoding.UTF8.GetBytes(output.ToString());
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Bookmark export too large.");
        return bytes;
    }

    internal static void WriteNew(string path, byte[] bytes)
    {
        // User-selected explicit plaintext export; never overwrite an existing file.
        bool created = false;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
