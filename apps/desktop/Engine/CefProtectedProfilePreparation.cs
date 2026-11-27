// SPDX-License-Identifier: MPL-2.0
using System.IO;

namespace Veil.Desktop;

/// <summary>
/// Prepares only the fixed CEF directories inside an already unlocked and
/// exclusively held product profile. No CEF bootstrap, migration, environment
/// changes, log creation or cleanup. A successful return is not launch authority.
/// </summary>
internal static class CefProtectedProfilePreparation
{
    // The future caller must hold the authenticated profile session and
    // exclusive profile lock throughout this call and any subsequent use.
    // The callback must represent that lifetime, not merely a UI selection.
    internal static CefProfileLayout Prepare(string productRoot, IVolumeBroker broker, Func<bool> stillAuthorized)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        ProfileGate.RejectElevated();
        RequireAuthorization(stillAuthorized);
        var layout = new CefProfileLayout(productRoot);
        var initial = ProfileGate.Verify(layout.ProductRoot, broker, requireProtected: true);

        // Inspect every existing target before the first mutation, including
        // obstructing files and redirected descendants.
        CefProtectedProfilePreflight.Inspect(layout.ProductRoot, broker);
        foreach (var (parent, child) in layout.PreparationSteps)
        {
            RequireAuthorization(stillAuthorized);
            CefProtectedProfilePreflight.RequireSameVolume(initial,
                ProfileGate.Verify(layout.ProductRoot, broker, requireProtected: true));
            CefProtectedProfilePreflight.RequireSameVolume(initial,
                ProfileGate.Verify(parent, broker, requireProtected: true));
            // Reuses the existing nearest-ancestor check before creation.
            var prepared = ProfileGate.Prepare(child, broker, requireProtected: true);
            CefProtectedProfilePreflight.RequireSameVolume(initial, prepared);
            ProfileGate.RejectRedirectedChildren(child);
            RequireAuthorization(stillAuthorized);
        }

        // Preflight refuses collisions and redirects again; all four paths
        // must now exist, be protected and belong to the originally observed volume.
        CefProtectedProfilePreflight.Inspect(layout.ProductRoot, broker);
        foreach (string path in layout.Directories)
            CefProtectedProfilePreflight.RequireSameVolume(initial,
                ProfileGate.Verify(path, broker, requireProtected: true));
        RequireAuthorization(stillAuthorized);
        CefProtectedProfilePreflight.RequireSameVolume(initial,
            ProfileGate.Verify(layout.ProductRoot, broker, requireProtected: true));
        return layout;
    }

    private static void RequireAuthorization(Func<bool> stillAuthorized)
    {
        if (!stillAuthorized())
            throw new ProfileLocationException("The protected CEF preparation session is no longer authorized.");
    }
}
