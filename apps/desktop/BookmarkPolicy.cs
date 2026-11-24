namespace Veil.Desktop;

internal sealed record BookmarkFolder(Guid Id, string Name, Guid? ParentId = null);

/// <summary>Local bookmark organization. Stable folder IDs, bounded acyclic hierarchy, no network access.</summary>
internal static class BookmarkPolicy
{
    internal const int MaxDepth = 16;
    internal static bool ValidName(string? name) => name is { Length: > 0 and <= 120 } &&
        !string.IsNullOrWhiteSpace(name) && name == name.Trim() && !name.Any(char.IsControl);

    internal static bool Valid(List<BookmarkFolder>? folders, List<PageEntry> entries)
    {
        if (folders is null || folders.Any(f => f is null || f.Id == Guid.Empty || !ValidName(f.Name)) ||
            folders.Select(f => f.Id).Distinct().Count() != folders.Count) return false;
        var byId = folders.ToDictionary(f => f.Id);
        if (entries.Any(e => e.FolderId is Guid id && !byId.ContainsKey(id))) return false;
        foreach (var folder in folders)
        {
            var seen = new HashSet<Guid>();
            Guid? next = folder.Id;
            while (next is Guid id)
            {
                if (!seen.Add(id) || seen.Count > MaxDepth || !byId.TryGetValue(id, out var current)) return false;
                next = current.ParentId;
            }
        }
        return true;
    }

    internal static bool MoveFolder(List<BookmarkFolder> folders, List<PageEntry> entries, Guid id, Guid? parent)
    {
        int index = folders.FindIndex(f => f.Id == id);
        if (index < 0 || folders[index].ParentId == parent) return false;
        var old = folders[index];
        folders[index] = old with { ParentId = parent };
        if (Valid(folders, entries)) return true;
        folders[index] = old;
        return false;
    }

    // Removing a folder promotes its children to the parent, preserving every bookmark.
    internal static bool RemoveFolder(List<BookmarkFolder> folders, List<PageEntry> entries, Guid id)
    {
        var folder = folders.FirstOrDefault(f => f.Id == id);
        if (folder is null) return false;
        for (int i = 0; i < folders.Count; i++)
            if (folders[i].ParentId == id) folders[i] = folders[i] with { ParentId = folder.ParentId };
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].FolderId == id) entries[i] = entries[i] with { FolderId = folder.ParentId };
        folders.Remove(folder);
        return true;
    }

    internal static bool Shift<T>(List<T> items, T item, Func<T, bool> sibling, int direction)
    {
        if (direction is not (-1 or 1)) return false;
        int index = items.IndexOf(item);
        if (index < 0) return false;
        for (int i = index + direction; i >= 0 && i < items.Count; i += direction)
        {
            if (!sibling(items[i])) continue;
            (items[index], items[i]) = (items[i], items[index]);
            return true;
        }
        return false;
    }

    internal static string Path(List<BookmarkFolder> folders, Guid? id)
    {
        var names = new Stack<string>();
        for (int depth = 0; id is Guid key && depth < MaxDepth; depth++)
        {
            var folder = folders.FirstOrDefault(f => f.Id == key);
            if (folder is null) break;
            names.Push(folder.Name);
            id = folder.ParentId;
        }
        return names.Count == 0 ? "최상위" : "최상위 / " + string.Join(" / ", names);
    }
}
