// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

// Pure prospective product binding. Not IPC, admission or an attestation.
// A future coordinator must construct observations from retained broker,
// runtime and profile handles, never from client assertions or preflight.
internal sealed record CefLibraryAuthorityObservation(object Generation, object RuntimeLease,
    object ProfileLease, VolumeStatus? Volume, bool AuthenticatedOwner, bool RuntimeAdmitted,
    bool ProfileLeaseCurrent, bool RuntimeLeaseCurrent, bool LockRequested);

// This type is not used by the product or development shell yet. It cannot
// acquire a lease, open a file, start CEF or validate a broker response.
internal sealed class CefProtectedLibraryAuthority
{
    private readonly object generation;
    private readonly object runtimeLease;
    private readonly object profileLease;
    private readonly string volumeGuid;
    private readonly Func<CefLibraryAuthorityObservation?> observe;
    private readonly CefLibraryLifetime lifetime;

    internal CefProtectedLibraryAuthority(object generation, object runtimeLease, object profileLease,
        string volumeGuid, Func<CefLibraryAuthorityObservation?> observe)
    {
        this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
        this.runtimeLease = runtimeLease ?? throw new ArgumentNullException(nameof(runtimeLease));
        this.profileLease = profileLease ?? throw new ArgumentNullException(nameof(profileLease));
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeGuid);
        this.volumeGuid = volumeGuid;
        this.observe = observe ?? throw new ArgumentNullException(nameof(observe));
        lifetime = new CefLibraryLifetime(Current);
    }

    internal bool IsRevoked => lifetime.IsRevoked;
    internal void Revoke() => lifetime.Revoke();

    private bool Current()
    {
        var current = observe();
        return current is not null &&
            ReferenceEquals(generation, current.Generation) &&
            ReferenceEquals(runtimeLease, current.RuntimeLease) &&
            ReferenceEquals(profileLease, current.ProfileLease) &&
            current.AuthenticatedOwner && current.RuntimeAdmitted &&
            current.ProfileLeaseCurrent && current.RuntimeLeaseCurrent && !current.LockRequested &&
            current.Volume is { FullyEncrypted: true, ProtectionOn: true, Unlocked: true, ProtectorPresent: true } volume &&
            string.Equals(volumeGuid, volume.VolumeGuid, StringComparison.OrdinalIgnoreCase);
    }

    // The writer must itself enforce the same authority at actual publication.
    // In particular, an already committed write cannot be rolled back merely
    // by observing revocation afterwards. No real writer is supplied here.
    internal bool TryCommit<T>(List<T> entries, Func<bool> authorized,
        Action change, Func<bool> valid, Func<bool> save) =>
        lifetime.TryCommit(entries, authorized, change, valid, save);
}
