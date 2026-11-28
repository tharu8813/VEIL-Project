// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

// Synchronous, dispatcher-owned collection edit. Storage authority belongs
// to the caller; this code does not know paths, profiles, or engines.
internal static class CollectionMutation
{
    internal static bool TryCommit<T>(List<T> entries, Func<bool> authorized,
        Action change, Func<bool> valid, Func<bool> save) =>
        TryCommit<T, T>(entries, null, authorized, change, valid, save);

    internal static bool TryCommit<T, TAux>(List<T> entries, List<TAux>? auxiliary,
        Func<bool> authorized, Action change, Func<bool> valid, Func<bool> save)
    {
        if (!authorized()) return false;
        var previous = entries.ToArray();
        var previousAuxiliary = auxiliary?.ToArray();
        bool committed = false;
        try
        {
            change();
            committed = valid() && authorized() && save();
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
                if (auxiliary is not null && previousAuxiliary is not null)
                {
                    auxiliary.Clear();
                    auxiliary.AddRange(previousAuxiliary);
                }
            }
        }
    }
}
