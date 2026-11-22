using System.IO;
using System.Text.Json;

namespace Veil.Desktop;

internal sealed record PageEntry(string Title, string Url, DateTimeOffset Visited)
{
    public override string ToString() => $"{Title}   ·   {Url}";
}

internal sealed class BrowserData
{
    public List<PageEntry> Bookmarks { get; init; } = [];
    public List<PageEntry> History { get; init; } = [];
    public List<string> Tabs { get; set; } = [];
    public int Selected { get; set; }

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
            return new BrowserData();
        }
        if (data is null || data.Bookmarks is null || data.History is null || data.Tabs is null ||
            data.Bookmarks.Concat(data.History).Any(entry => entry?.Title is null || entry.Url is null) ||
            data.Tabs.Any(url => url is null))
            throw new JsonException("Invalid VEIL developer profile state.");
        return data;
    }

    internal void Save(string path)
    {
        string temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
            File.Replace(temporary, path, null);
        else
            File.Move(temporary, path);
    }
}
