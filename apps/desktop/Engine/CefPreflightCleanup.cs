// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

// Strict, testable close policy for the disabled CEF preflight only.
// Never substitutes a successful status observation for a refused command.
internal static class CefPreflightCleanup
{
    internal static void RequireClosed(string expectedGuid, bool attachedHere,
        Func<VolumeStatus?> query, Action lockVolume, Action detach)
    {
        if (string.IsNullOrWhiteSpace(expectedGuid))
            throw new InvalidOperationException("Missing authenticated volume identity.");
        RequireSame(query(), expectedGuid);
        lockVolume();
        var locked = RequireSame(query(), expectedGuid);
        if (locked.Unlocked || !locked.ReadyToUnlock)
            throw new InvalidOperationException("Registered volume lock could not be verified.");
        if (attachedHere)
        {
            detach();
            if (query() is not null)
                throw new InvalidOperationException("Registered volume detach could not be verified.");
        }
    }

    private static VolumeStatus RequireSame(VolumeStatus? status, string expectedGuid)
    {
        if (status is null || !string.Equals(status.VolumeGuid, expectedGuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Authenticated volume identity changed during cleanup.");
        return status;
    }
}
