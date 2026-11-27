// SPDX-License-Identifier: MPL-2.0
namespace Veil.VolumeBroker;

// CreatedFileTime comes from GetProcessTimes on the inspected live process.
internal sealed record ClientIdentity(int ProcessId, int SessionId, string UserSid,
    string ImagePath, long CreatedFileTime);
