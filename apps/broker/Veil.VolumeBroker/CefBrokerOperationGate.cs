// SPDX-License-Identifier: MPL-2.0
namespace Veil.VolumeBroker;

// The same serialization boundary for broker commands, lock, owner
// revocation and prospective library publication. No new IPC or filesystem
// authority. Publication callbacks are synchronous, trusted broker-owned
// operations only; callers must not already hold this gate or reenter it.
internal sealed class CefBrokerOperationGate
{
    private readonly SemaphoreSlim serial = new(1, 1);

    internal Task WaitAsync(CancellationToken cancellation = default) => serial.WaitAsync(cancellation);
    internal void Release() => serial.Release();

    // Stage outside this boundary. Re-read exact generation, live owner,
    // runtime and retained profile authority inside it. Publisher must be
    // bounded and return true only after irreversible replacement; failure
    // must mean the original remains intact. Do not use cancellation or a
    // later authority observation to reinterpret a committed result.
    internal async Task<bool> TryPublishAsync(Func<bool> current, Func<bool> publish,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(publish);
        try { await WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        try
        {
            if (cancellation.IsCancellationRequested || !current() || cancellation.IsCancellationRequested)
                return false;
            return publish();
        }
        finally { Release(); }
    }
}
