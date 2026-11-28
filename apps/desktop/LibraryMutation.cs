// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

// Engine-neutral policy wrapper. The protected profile still owns the writer.
internal static class LibraryMutation
{
    internal static bool TryCommit(List<PageEntry> entries, List<BookmarkFolder>? folders,
        Func<bool> authorized, Action change, Func<bool> save) =>
        CollectionMutation.TryCommit(entries, folders, authorized, change,
            () => folders is null || BookmarkPolicy.Valid(folders, entries), save);
}
