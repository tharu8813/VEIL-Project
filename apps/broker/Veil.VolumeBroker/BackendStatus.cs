// SPDX-License-Identifier: MPL-2.0
namespace Veil.VolumeBroker;

internal sealed record BackendStatus(bool Attached, string? VolumeGuid, bool FullyEncrypted, bool ProtectionOn,
    bool Unlocked, bool ProtectorPresent, bool ReadyToUnlock = false);
