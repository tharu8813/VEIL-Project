// SPDX-License-Identifier: MPL-2.0
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Veil.Shared;

namespace Veil.Desktop;

internal sealed record CefRuntimeFilePin(string Path, long Bytes, string Sha256);

// Retains a read-only handle to every authenticated inventory entry. The
// caller must supply pins from independently authenticated admin registration.
// Neither a candidate's own unsigned JSON nor an environment variable is trust.
internal sealed class CefRuntimeFileLease : IDisposable
{
    private readonly List<FileStream> streams = [];
    private readonly string root;
    private readonly Dictionary<string, CefRuntimeFilePin> pins;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool disposed;

    private CefRuntimeFileLease(string root, Dictionary<string, CefRuntimeFilePin> pins)
    {
        this.root = root;
        this.pins = pins;
    }

    internal static CefRuntimeFileLease OpenInstalled(IEnumerable<CefRuntimeFilePin> authenticatedPins)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VEIL", "Cef");
        return Open(root, authenticatedPins, AdminOnlyFile.RequireProtectedExecutable);
    }

    // Separate test seam for disposable synthetic files; never used by product startup.
    internal static CefRuntimeFileLease Open(string root, IEnumerable<CefRuntimeFilePin> authenticatedPins,
        Action<string> requireProtectedFile)
    {
        ArgumentNullException.ThrowIfNull(authenticatedPins);
        ArgumentNullException.ThrowIfNull(requireProtectedFile);
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected canonical local runtime root.", nameof(root));
        var pins = new Dictionary<string, CefRuntimeFilePin>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in authenticatedPins)
        {
            if (pin is null || !ValidRelativePath(pin.Path) || pin.Bytes <= 0 ||
                pin.Sha256 is not { Length: 64 } || !pin.Sha256.All(Uri.IsHexDigit) ||
                !pins.TryAdd(pin.Path, pin) || pins.Count > 4096)
                throw new InvalidDataException("Invalid or duplicate CEF runtime pin.");
        }
        foreach (string required in new[] { "veil-cef-product.exe", "veil-cef-product.dll", "libcef.dll", "icudtl.dat" })
            if (!pins.ContainsKey(required)) throw new InvalidDataException("Required CEF runtime pin missing.");
        var lease = new CefRuntimeFileLease(root, pins);
        try
        {
            lease.CheckInventory();
            foreach (var pin in pins.Values)
            {
                string path = Path.Combine(root, pin.Path.Replace('/', Path.DirectorySeparatorChar));
                requireProtectedFile(path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                lease.streams.Add(stream);
                RequireCanonicalSingleLink(stream.SafeFileHandle, path);
                if (stream.Length != pin.Bytes ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(pin.Sha256)))
                    throw new UnauthorizedAccessException("CEF runtime file length or digest mismatch.");
                stream.Position = 0;
                requireProtectedFile(path);
                RequireCanonicalSingleLink(stream.SafeFileHandle, path);
            }
            lease.CheckInventory();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal string ImagePath
    {
        get { RequireCurrent(); return Path.Combine(root, "veil-cef-product.exe"); }
    }

    internal void RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != ownerThread ||
            streams.Count != pins.Count || streams.Any(s => s.SafeFileHandle.IsClosed || s.SafeFileHandle.IsInvalid))
            throw new UnauthorizedAccessException("CEF runtime lease unavailable.");
        CheckInventory();
        int i = 0;
        foreach (var pin in pins.Values)
            RequireCanonicalSingleLink(streams[i++].SafeFileHandle,
                Path.Combine(root, pin.Path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private void CheckInventory()
    {
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if (!ancestor.Exists || ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("CEF runtime ancestor is missing or redirected.");
        var remaining = new HashSet<string>(pins.Keys, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count != 0)
        {
            foreach (var item in pending.Pop().EnumerateFileSystemInfos())
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new UnauthorizedAccessException("CEF runtime link refused.");
                if (item is DirectoryInfo directory) { pending.Push(directory); continue; }
                string name = Path.GetRelativePath(root, item.FullName).Replace('\\', '/');
                if (!remaining.Remove(name) || !pins.TryGetValue(name, out var pin) ||
                    !string.Equals(name, pin.Path, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Unexpected CEF runtime entry.");
            }
        }
        if (remaining.Count != 0) throw new UnauthorizedAccessException("CEF runtime file missing.");
    }

    internal static bool ValidRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240) return false;
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part.EndsWith('.') ||
                !part.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.'))
                return false;
            string baseName = part.Split('.')[0].ToUpperInvariant();
            if (baseName is "CON" or "PRN" or "AUX" or "NUL" ||
                (baseName.Length == 4 && (baseName.StartsWith("COM") || baseName.StartsWith("LPT")) && baseName[3] is >= '1' and <= '9'))
                return false;
            if (part.StartsWith("veil-cef-smoke", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("veil-cef-wpf", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("Veil.Cef.WpfSmoke", StringComparison.OrdinalIgnoreCase) ||
                part.Contains("-test.", StringComparison.OrdinalIgnoreCase)) return false;
        }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".exe" ? path == "veil-cef-product.exe" :
            ext is ".dll" or ".pak" or ".dat" or ".bin" or ".json" or ".txt";
    }

    private static void RequireCanonicalSingleLink(SafeFileHandle file, string path)
    {
        var resolved = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(file, resolved, (uint)resolved.Capacity, 0);
        if (length == 0 || length >= resolved.Capacity ||
            !string.Equals(resolved.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase) ||
            !GetFileInformationByHandle(file, out var info) || info.Links != 1 ||
            (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("CEF runtime file must be canonical and single-link.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var stream in streams) stream.Dispose();
        streams.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}
