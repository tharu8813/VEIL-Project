// SPDX-License-Identifier: MPL-2.0
using System.Collections.ObjectModel;

namespace Veil.Desktop;

/// <summary>
/// Pure environment plan for a future protected CEF bootstrap process.
/// The native bootstrap must independently verify the protected session
/// and paths before CefInitialize; this plan is not launch authorization.
/// </summary>
internal static class CefChildEnvironmentPlan
{
    internal const string RootKey = "VEIL_CEF_PROTECTED_ROOT";
    internal const string CacheKey = "VEIL_CEF_PROTECTED_CACHE";
    internal const string LogKey = "VEIL_CEF_PROTECTED_LOG_FILE";

    internal static IReadOnlyDictionary<string, string?> Build(
        CefProfileLayout layout, IEnumerable<KeyValuePair<string, string?>> inherited)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(inherited);
        var child = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inherited)
        {
            // Windows environment names are case insensitive. Discard every
            // inherited VEIL CEF fixture or stale product path, plus the
            // Chromium log override and both temporary-directory variables.
            if (entry.Key.StartsWith("VEIL_CEF_", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Equals("CHROME_LOG_FILE", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Equals("TMP", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!child.TryAdd(entry.Key, entry.Value))
                throw new ArgumentException("Ambiguous inherited environment variable name.", nameof(inherited));
        }

        child.Add("TEMP", layout.Scratch);
        child.Add("TMP", layout.Scratch);
        child.Add(RootKey, layout.EngineRoot);
        child.Add(CacheKey, layout.Cache);
        child.Add(LogKey, layout.LogFile);
        return new ReadOnlyDictionary<string, string?>(child);
    }
}
