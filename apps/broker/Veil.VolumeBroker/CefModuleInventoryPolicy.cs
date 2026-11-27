// SPDX-License-Identifier: MPL-2.0
using System.IO;

namespace Veil.VolumeBroker;

// Provisional disk inventory observation, never a mapped-code or sandbox verdict.
internal sealed record CefModuleInventoryObservation(int ApplicationModules, int UnverifiedSystemModules);

internal static class CefModuleInventoryPolicy
{
    internal static CefModuleInventoryObservation Verify(string registeredImage, string systemDirectory,
        IReadOnlyList<string> observed, Action<IEnumerable<string>> verifyApplicationModules,
        Func<string[]> observeAgain)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(verifyApplicationModules);
        ArgumentNullException.ThrowIfNull(observeAgain);
        if (!CanonicalLocalPath(registeredImage) || !CanonicalLocalPath(systemDirectory) ||
            !string.Equals(Path.GetFileName(registeredImage), "veil-cef-product.exe", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invalid registered CEF module roots.");

        string runtime = Path.GetDirectoryName(registeredImage)!;
        if (string.Equals(runtime, systemDirectory, StringComparison.OrdinalIgnoreCase) ||
            observed.Count is < 2 or > 1024)
            throw new UnauthorizedAccessException("Invalid CEF module inventory.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var application = new List<string>();
        int system = 0;
        foreach (string module in observed)
        {
            if (!CanonicalLocalPath(module) || !paths.Add(module))
                throw new UnauthorizedAccessException("Invalid or duplicate CEF module path.");
            string? parent = Path.GetDirectoryName(module);
            bool dll = string.Equals(Path.GetExtension(module), ".dll", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(parent, runtime, StringComparison.OrdinalIgnoreCase) &&
                (dll || string.Equals(module, registeredImage, StringComparison.OrdinalIgnoreCase)))
                application.Add(module);
            else if (dll && string.Equals(parent, systemDirectory, StringComparison.OrdinalIgnoreCase))
                system++; // Candidate system DLL only; no signer or hash verdict.
            else
                throw new UnauthorizedAccessException("Unexpected CEF module location or type.");
        }
        if (!paths.Contains(registeredImage) || !paths.Contains(Path.Combine(runtime, "libcef.dll")))
            throw new UnauthorizedAccessException("Required CEF images missing from observation.");

        // Existing lease checks the original SHA-256-pinned disk object,
        // canonical path and retained read-only file handles.
        verifyApplicationModules(application);
        string[] after = observeAgain();
        if (after.Length != paths.Count || !paths.SetEquals(after))
            throw new UnauthorizedAccessException("CEF module inventory changed during observation.");
        return new(application.Count, system);
    }

    private static bool CanonicalLocalPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
        !path.AsSpan(2).Contains(':') && !path.Contains('/') &&
        !path.Contains('\0') && !path.EndsWith('\\') &&
        string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
}
