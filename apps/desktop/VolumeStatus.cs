// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

/// <summary>Protection state of a registered profile volume, as reported by the privileged broker.</summary>
internal sealed record VolumeStatus(string VolumeGuid, bool FullyEncrypted, bool ProtectionOn, bool Unlocked,
    bool ReadyToUnlock, bool ProtectorPresent);
