// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

// Explicit internal integration seam, not invoked by Program or UI.
// Only for the registered startupEnabled=false candidate. No fallback.
internal static class CefDisabledLaunchCoordinator
{
    internal static uint RunPreflight(ProtectedProfileSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var profile = session.AcquireCefPreparationLease();
        using var runtime = profile.PrepareRegisteredRuntime();
        var environment = profile.PrepareChildEnvironment();
        // The candidate does not browse or start Chromium subprocesses.
        // Do not forward arbitrary inherited DLL/Chromium/.NET overrides.
        var minimal = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in new[] { "TEMP", "TMP", CefChildEnvironmentPlan.RootKey,
            CefChildEnvironmentPlan.CacheKey, CefChildEnvironmentPlan.LogKey })
            minimal.Add(key, environment[key]);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system = Environment.SystemDirectory;
        if (string.IsNullOrEmpty(windows) || string.IsNullOrEmpty(system))
            throw new InvalidOperationException("Windows runtime directory unavailable.");
        minimal.Add("SystemRoot", windows);
        minimal.Add("WINDIR", windows);
        minimal.Add("PATH", system);

        bool Current()
        {
            if (!profile.StillAuthorized()) return false;
            runtime.RequireCurrent();
            return true;
        }

        if (!Current()) throw new InvalidOperationException("CEF preparation unavailable.");
        using var child = CefContainedProcess.CreateSuspended(runtime.ImagePath, minimal);
        return CefDisabledPreflight.Run(child, () => profile.StageSuspendedChild(child), Current);
        // using disposal order: child job, runtime registration/files, profile
        // lock. The caller still owns the parent session and relock.
    }
}
