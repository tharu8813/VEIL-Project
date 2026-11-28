// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

// Engine-neutral synchronous edit transaction for an already authorized
// profile. No engine, path selection, or independent storage authority.
internal static class LibraryMutation
{
    internal static bool TryCommit(List<PageEntry> entries, List<BookmarkFolder>? folders,
        Func<bool> authorized, Action change, Func<bool> save)
    {
        if (!authorized()) return false;
        var previous = entries.ToArray();
        var previousFolders = folders?.ToArray();
        bool committed = false;
        try
        {
            change();
            committed = (folders is null || BookmarkPolicy.Valid(folders, entries)) &&
                authorized() && save();
            return committed;
        }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            return false;
        }
        finally
        {
            if (!committed)
            {
                entries.Clear();
                entries.AddRange(previous);
                if (folders is not null && previousFolders is not null)
                {
                    folders.Clear();
                    folders.AddRange(previousFolders);
                }
            }
        }
    }
}
