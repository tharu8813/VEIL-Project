// SPDX-License-Identifier: MPL-2.0
using System.Collections.ObjectModel;
using System.IO;

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

    // Pure allowlist for a future broker-created protected CEF candidate.
    // Accept only independently verified canonical local paths from the
    // eventual broker authority. This does not verify volume protection,
    // directory objects or ownership, and is not an activation permit.
    internal static IReadOnlyDictionary<string, string?> BuildBrokerProtected(
        string registeredProductRoot, string windowsDirectory)
    {
        RequireCanonicalLocalDirectory(registeredProductRoot);
        RequireCanonicalLocalDirectory(windowsDirectory);
        // A protected profile must not be inside Windows or contain it.
        // Actual separate-volume and reparse checks belong to the caller.
        if (Overlaps(registeredProductRoot, windowsDirectory))
            throw new ArgumentException("CEF profile overlaps system directory.");
        var layout = new CefProfileLayout(registeredProductRoot);
        return new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windowsDirectory,
            ["WINDIR"] = windowsDirectory,
            ["TEMP"] = layout.Scratch,
            ["TMP"] = layout.Scratch,
            [RootKey] = layout.EngineRoot,
            [CacheKey] = layout.Cache,
            [LogKey] = layout.LogFile,
        });
    }

    private static bool Overlaps(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        return first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireCanonicalLocalDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            !string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.Ordinal) ||
            string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase) ||
            path.Contains('\0'))
            throw new ArgumentException("Expected a canonical local directory.", nameof(path));
        string root = Path.GetPathRoot(path)!;
        // Reject stream syntax and Win32 ambiguous path components rather
        // than normalize them to some other directory at process creation.
        foreach (string component in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (component.Length == 0 || component is "." or ".." ||
                component.EndsWith('.') || component.EndsWith(' ') ||
                component.IndexOfAny([':', '"', '<', '>', '|', '?', '*']) >= 0)
                throw new ArgumentException("Ambiguous CEF directory component.", nameof(path));
        }
    }

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
