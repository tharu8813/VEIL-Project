// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

// A revocable edit lifetime, NOT a runtime admission or profile lease.
// The owner supplies a live session check; a refusal is terminal. The
// actual protected writer must independently verify its authority at publish.
internal sealed class CefLibraryLifetime(Func<bool> lifetimeCurrent)
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private int revoked;
    private bool editing;

    internal bool IsRevoked => Volatile.Read(ref revoked) != 0;
    internal void Revoke() => Interlocked.Exchange(ref revoked, 1);

    private bool Current()
    {
        if (Environment.CurrentManagedThreadId != ownerThread || IsRevoked) return false;
        try
        {
            if (lifetimeCurrent() && !IsRevoked) return true;
        }
        catch (Exception error) when (RecoverableFailure.Is(error)) { }
        Revoke();
        return false;
    }

    internal bool TryCommit<T>(List<T> entries, Func<bool> authorized,
        Action change, Func<bool> valid, Func<bool> save)
    {
        if (Environment.CurrentManagedThreadId != ownerThread || editing || IsRevoked) return false;
        editing = true;
        try
        {
            return CollectionMutation.TryCommit(entries,
                () => Current() && authorized() && !IsRevoked,
                change, valid, () => Current() && save());
        }
        finally { editing = false; }
    }

    // Stage must not replace the committed file. Publish returns true only
    // after the irreversible commit point; false/exception means unchanged.
    // The caller owns its staged resource and must discard only that resource.
    // A production publisher must serialize revocation and replacement in the
    // retained broker/profile boundary; these probes alone cannot do that.
    internal bool TryCommitStaged<T>(List<T> entries, Func<bool> authorized,
        Action change, Func<bool> valid, Action stage, Func<bool> publish, Action discard)
    {
        return TryCommit(entries, authorized, change, valid, () =>
        {
            bool published = false;
            try
            {
                stage();
                if (!Current() || !authorized() || IsRevoked || !valid() || !Current()) return false;
                published = publish();
                // Do not turn an irreversible successful write into a failed
                // transaction if authority was lost during or after publication.
                return published;
            }
            finally
            {
                if (!published)
                {
                    try { discard(); }
                    catch (Exception error) when (RecoverableFailure.Is(error))
                    {
                        // An unconfirmed cleanup blocks further writes in this lifetime.
                        Revoke();
                    }
                }
            }
        });
    }
}
