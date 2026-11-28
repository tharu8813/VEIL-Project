// SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Veil.Shared;

namespace Veil.VolumeBroker;

internal sealed record CefSystemModulePin(string Name, long Bytes, string Sha256, string SignerSha256);

// Provisional direct System32 disk-file verification. The caller must supply
// an independently authenticated, OS-build-specific baseline, never hashes
// learned from a running CEF process. No baseline producer or admission here.
internal sealed class CefSystemModuleVerifier
{
    private readonly Dictionary<string, CefSystemModulePin> pins = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root = Environment.SystemDirectory;

    private readonly Action? requireHostCurrent;

    internal CefSystemModuleVerifier(IEnumerable<CefSystemModulePin> authenticatedPins,
        Action? requireHostCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(authenticatedPins);
        this.requireHostCurrent = requireHostCurrent;
        foreach (var pin in authenticatedPins)
        {
            if (pin is null || !ValidName(pin.Name) || pin.Bytes <= 0 ||
                pin.Sha256 is not { Length: 64 } || !pin.Sha256.All(Uri.IsHexDigit) ||
                !CefSystemSignatureTrust.ValidSignerPin(pin.SignerSha256) ||
                !pins.TryAdd(pin.Name, pin) || pins.Count > 1024)
                throw new ArgumentException("Invalid or duplicate system module pin.");
        }
        if (pins.Count == 0) throw new ArgumentException("System module baseline unavailable.");
    }

    private static bool ValidName(string name) =>
        !string.IsNullOrEmpty(name) && name.Length is > 4 and <= 240 &&
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
        !name.StartsWith('.') && !name.Contains("..", StringComparison.Ordinal);

    internal void RequireModules(IEnumerable<string> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Expected Windows x64 module verification.");
        requireHostCurrent?.Invoke();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in modules)
        {
            string name = Path.GetFileName(path);
            if (!ValidName(name) || !string.Equals(path, Path.Combine(root, name), StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(name) || !pins.TryGetValue(name, out var pin))
                throw new UnauthorizedAccessException("System module outside authenticated baseline.");

            AdminOnlyFile.RequireProtectedExecutable(path);
            using var retained = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var original = RequireCanonical(retained.SafeFileHandle, path);
            if (retained.Length != pin.Bytes ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(retained), Convert.FromHexString(pin.Sha256)))
                throw new UnauthorizedAccessException("System module digest mismatch.");

            // Require offline Authenticode (embedded or SHA-256 catalog member)
            // plus Microsoft production root policy and the authenticated
            // leaf certificate pin. No live revocation or provenance claim.
            retained.Position = 0;
            if (!CefSystemSignatureTrust.Verify(path, retained.SafeFileHandle, pin.SignerSha256))
                throw new UnauthorizedAccessException("System module signature or authenticated signer pin mismatch.");
            AdminOnlyFile.RequireProtectedExecutable(path);
            using var again = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var current = RequireCanonical(again.SafeFileHandle, path);
            if (original.VolumeSerial != current.VolumeSerial || original.IndexHigh != current.IndexHigh ||
                original.IndexLow != current.IndexLow || again.Length != pin.Bytes)
                throw new UnauthorizedAccessException("System module disk object changed.");
        }
        requireHostCurrent?.Invoke();
    }

    private static FileInformation RequireCanonical(SafeFileHandle file, string path)
    {
        var resolved = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(file, resolved, (uint)resolved.Capacity, 0);
        // Windows servicing can use hard links. Do not impose the application
        // single-link restriction on OS files; still require the exact path.
        if (length == 0 || length >= resolved.Capacity ||
            !string.Equals(resolved.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase) ||
            !GetFileInformationByHandle(file, out var info) || info.Links == 0 ||
            (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("System module path is not canonical.");
        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}
