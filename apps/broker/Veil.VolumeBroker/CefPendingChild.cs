// SPDX-License-Identifier: MPL-2.0
namespace Veil.VolumeBroker;

/// <summary>Owns the retained child and image resources. All calls serialized
/// by the broker; no external token or startup permission is returned.</summary>
internal sealed class CefPendingChild : IDisposable
{
    private readonly CefChildAdmission admission;
    private readonly IDisposable process;
    private readonly IDisposable image;
    private bool disposed;

    internal CefPendingChild(CefChildAdmission admission, IDisposable process, IDisposable image)
    {
        this.admission = admission;
        this.process = process;
        this.image = image;
    }

    internal bool IsExpired => disposed || admission.IsExpired;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        admission.Revoke();
        try { image.Dispose(); }
        finally { process.Dispose(); }
    }
}
