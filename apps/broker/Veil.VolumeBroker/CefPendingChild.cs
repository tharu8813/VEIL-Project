// SPDX-License-Identifier: MPL-2.0
namespace Veil.VolumeBroker;

/// <summary>Owns the retained child and image resources. All calls serialized
/// by the broker; no external token or startup permission is returned.</summary>
internal sealed class CefPendingChild : IDisposable
{
    private readonly CefChildAdmission admission;
    private IDisposable? process;
    private IDisposable? image;
    private IDisposable? runtime;
    private IDisposable? job;
    private bool disposed;

    internal CefPendingChild(CefChildAdmission admission, IDisposable process, IDisposable image, IDisposable? runtime = null, IDisposable? job = null)
    {
        this.admission = admission;
        this.process = process;
        this.image = image;
        this.runtime = runtime;
        this.job = job;
    }

    internal bool IsExpired => disposed || admission.IsExpired;

    internal bool MatchesChild(ClientIdentity child) => !disposed && !IsExpired && admission.MatchesChild(child);

    // These resources remain owned by this object, never transferred to the client.
    internal IDisposable? ProcessResource => process;

    // Transfer only within the serialized broker; the grant is permanently
    // revoked even if disposal/termination subsequently fails.
    internal IDisposable? DetachRevokedProcess()
    {
        admission.Revoke();
        var retained = process;
        process = null;
        return retained;
    }
    internal IDisposable? JobResource => job;
    internal IDisposable? DetachJob()
    {
        admission.Revoke();
        var retained = job;
        job = null;
        return retained;
    }
    internal IDisposable? ImageResource => image;
    internal IDisposable? RuntimeResource => runtime;

    // Transfer the file pins as one lease. After transfer, pending disposal
    // must not release them while the candidate or a failed stop is alive.
    internal (IDisposable? Image, IDisposable? Runtime) DetachFilePins()
    {
        admission.Revoke();
        var pins = (image, runtime);
        image = null;
        runtime = null;
        return pins;
    }

    internal bool TryRedeem(ClientIdentity owner, ClientIdentity child, object generation,
        bool ownerAlive, bool childAlive, bool imageVerified, bool volumeProtected) =>
        !disposed && admission.TryRedeem(owner, child, generation, ownerAlive, childAlive,
            imageVerified, volumeProtected);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        admission.Revoke();
        try { runtime?.Dispose(); }
        finally
        {
            try { image?.Dispose(); }
            finally
            {
                try { job?.Dispose(); }
                finally { process?.Dispose(); }
            }
        }
    }
}
