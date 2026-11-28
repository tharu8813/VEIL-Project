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
        return lifetime.TryRelease(() =>
        {
            var elapsed = Stopwatch.StartNew();
            int Remaining() => (int)Math.Max(0L, milliseconds - elapsed.ElapsedMilliseconds);
            if (!custody.TryDrainAndRelease(Remaining())) return false;
            // Also cover native refusal before a root was captured.
            // Both Jobs were created privately, never exported or reused.
            if (!outer.TerminateAndDrain(Remaining()) || !inner.TerminateAndDrain(Remaining()))
                return false;
            return outer.HasNoActiveProcesses() && inner.HasNoActiveProcesses();
        });
    }
}
