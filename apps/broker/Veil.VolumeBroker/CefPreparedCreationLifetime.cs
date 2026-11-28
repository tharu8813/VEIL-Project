// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Veil.Desktop;

namespace Veil.VolumeBroker;

// Serialized lifetime for a suspended creation probe. The slot must own
// this object BEFORE any native creation. No resume or take/transfer API.
// Prerequisites and both private Jobs remain retained through exit proof.
internal sealed class CefPreparedCreationLifetime : ICefDiagnosticLifetime
{
    private readonly CefBrokerOwnedJob outer;
    private readonly CefBrokerOwnedJob inner;
    private readonly Action<CefBrokerOwnedJob, CefBrokerOwnedJob, CefContainedProcess.CreationCustody> create;
    private readonly CefContainedProcess.CreationCustody custody = new();
    private readonly CefRetainedLifetime lifetime;
    private bool attempted;
    private bool created;
    private bool handoffAttempted;
    private bool creating;
    private bool retired;

    internal CefPreparedCreationLifetime(CefBrokerOwnedJob outer, CefBrokerOwnedJob inner,
        IDisposable prerequisites,
        Action<CefBrokerOwnedJob, CefBrokerOwnedJob, CefContainedProcess.CreationCustody> create)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(prerequisites);
        ArgumentNullException.ThrowIfNull(create);
        if (ReferenceEquals(outer, inner))
            throw new ArgumentException("Expected distinct private Jobs.");
        this.outer = outer;
        this.inner = inner;
        this.create = create;
        lifetime = new CefRetainedLifetime(prerequisites, inner, outer);
    }

    internal void Create()
    {
        if (retired || attempted)
            throw new UnauthorizedAccessException("CEF creation lifetime unavailable.");
        attempted = true;
        creating = true;
        try
        {
            create(outer, inner, custody);
            if (retired || !custody.HasRetainedProcess)
                throw new UnauthorizedAccessException("CEF creation was retired or unconfirmed.");
            created = true;
        }
        finally { creating = false; }
    }

    // A successor borrows our retained resources and delegates cleanup to
    // us. The original slot must remain occupied throughout construction.
    internal T PrepareSuccessor<T>(
        Func<CefBrokerOwnedJob, CefBrokerOwnedJob, CefContainedProcess, Microsoft.Win32.SafeHandles.SafeProcessHandle, T> build)
        where T : class, ICefDiagnosticLifetime
    {
        ArgumentNullException.ThrowIfNull(build);
        if (!created || retired || creating || handoffAttempted)
            throw new UnauthorizedAccessException("CEF creation handoff unavailable.");
        handoffAttempted = true;
        creating = true;
        try
        {
            var successor = custody.BuildRetainedSession((child, root) => build(outer, inner, child, root));
            if (retired)
                throw new UnauthorizedAccessException("CEF creation handoff retired.");
            return successor;
        }
        catch
        {
            retired = true;
            throw;
        }
        finally { creating = false; }
    }

    public void Revoke() => retired = true;

    public bool RevokeAndRelease(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        Revoke();
        if (creating) return false;
        return lifetime.TryRelease(() => TryDrainAll(milliseconds,
            custody.TryDrainAndRelease, outer.TerminateAndDrain, inner.TerminateAndDrain,
            () => outer.HasNoActiveProcesses() && inner.HasNoActiveProcesses()));
    }

    // Each independently owned private Job gets a stop attempt even if
    // another drain refuses or throws. Never reinterpret a failed attempt
    // as proof of exit; the retained lifetime stays occupied for retry.
    // The callbacks share a single deadline (zero still attempts cleanup).
    internal static bool TryDrainAll(int milliseconds, Func<int, bool> drainCustody,
        Func<int, bool> drainOuter, Func<int, bool> drainInner, Func<bool> verifyEmpty)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        var elapsed = Stopwatch.StartNew();
        Exception? failure = null;
        int Remaining() => (int)Math.Max(0L, milliseconds - elapsed.ElapsedMilliseconds);
        bool Attempt(Func<int, bool> drain)
        {
            try { return drain(Remaining()); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure ??= error;
                return false;
            }
        }

        bool custodyDrained = Attempt(drainCustody);
        bool outerDrained = Attempt(drainOuter);
        bool innerDrained = Attempt(drainInner);
        if (failure is not null)
            throw new InvalidOperationException("CEF private drain attempt failed.", failure);
        return custodyDrained && outerDrained && innerDrained && verifyEmpty();
    }
}
