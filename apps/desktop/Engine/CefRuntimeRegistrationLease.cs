// SPDX-License-Identifier: MPL-2.0
using System.IO;
using System.Text.Json;
using Veil.Shared;

namespace Veil.Desktop;

// Read-only machine registration for a disabled CEF candidate. The
// installer must eventually publish it from verified signed evidence,
// atomically with the broker pin and package. No publication exists yet.
internal sealed class CefRuntimeRegistrationLease : IDisposable
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const string ArchiveHash = "1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a";
    private readonly FileStream registration;
    private readonly CefRuntimeFileLease runtime;
    private readonly string path;
    private bool disposed;

    private CefRuntimeRegistrationLease(FileStream registration, CefRuntimeFileLease runtime, string path)
    {
        this.registration = registration;
        this.runtime = runtime;
        this.path = path;
    }

    internal static CefRuntimeRegistrationLease OpenInstalled(bool serializedBrokerAccess = false)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VEIL", "cef-runtime-pins.json");
        AdminOnlyFile.RequireProtectedExecutable(path);
        var registration = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CefRuntimeFileLease? runtime = null;
        try
        {
            CefRuntimeFileLease.RequireCanonicalSingleLink(registration.SafeFileHandle, path);
            if (registration.Length is <= 0 or > MaxBytes)
                throw new InvalidDataException("Invalid CEF runtime registration length.");
            byte[] data = new byte[checked((int)registration.Length)];
            registration.ReadExactly(data);
            var pins = Parse(data);
            AdminOnlyFile.RequireProtectedExecutable(path);
            CefRuntimeFileLease.RequireCanonicalSingleLink(registration.SafeFileHandle, path);
            runtime = CefRuntimeFileLease.OpenInstalled(pins, serializedBrokerAccess);
            var lease = new CefRuntimeRegistrationLease(registration, runtime, path);
            lease.RequireCurrent();
            return lease;
        }
        catch
        {
            try { runtime?.Dispose(); }
            finally { registration.Dispose(); }
            throw;
        }
    }

    // Pure strict parser. No installed path, hash list, or capability comes
    // from inherited environment variables, command line or candidate folder.
    internal static IReadOnlyList<CefRuntimeFilePin> Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length is <= 0 or > MaxBytes) throw new InvalidDataException("Invalid registration length.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
        var root = document.RootElement;
        if (!ExactObject(root, "kind", "format", "architecture", "sourceCommit",
                "cefArchiveSha256", "startupEnabled", "files") ||
            !TextEquals(root.GetProperty("kind"), "cef-admin-runtime-pins") ||
            root.GetProperty("format").ValueKind != JsonValueKind.Number ||
            root.GetProperty("format").GetRawText() != "1" ||
            !TextEquals(root.GetProperty("architecture"), "win-x64") ||
            root.GetProperty("startupEnabled").ValueKind != JsonValueKind.False ||
            !Hex(root.GetProperty("sourceCommit"), 40) ||
            !TextEquals(root.GetProperty("cefArchiveSha256"), ArchiveHash) ||
            root.GetProperty("files").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unsupported CEF runtime registration.");
        var files = root.GetProperty("files");
        if (files.GetArrayLength() is < 4 or > 4096)
            throw new InvalidDataException("Invalid runtime pin count.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CefRuntimeFilePin>();
        foreach (var entry in files.EnumerateArray())
        {
            if (!ExactObject(entry, "path", "bytes", "sha256") ||
                entry.GetProperty("path").ValueKind != JsonValueKind.String ||
                !Hex(entry.GetProperty("sha256"), 64))
                throw new InvalidDataException("Invalid runtime pin fields.");
            string name = entry.GetProperty("path").GetString()!;
            var length = entry.GetProperty("bytes");
            if (!CefRuntimeFileLease.ValidRelativePath(name) || !seen.Add(name) ||
                length.ValueKind != JsonValueKind.Number ||
                !length.GetRawText().All(c => c is >= '0' and <= '9') ||
                !length.TryGetInt64(out long bytes) || bytes <= 0)
                throw new InvalidDataException("Invalid or duplicate runtime pin.");
            result.Add(new(name, bytes, entry.GetProperty("sha256").GetString()!));
        }
        foreach (string required in new[] { "veil-cef-product.exe", "veil-cef-product.dll", "libcef.dll", "icudtl.dat" })
            if (!result.Any(p => p.Path == required))
                throw new InvalidDataException("Missing required runtime pin.");
        return result.AsReadOnly();
    }

    private static bool ExactObject(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }

    private static bool TextEquals(JsonElement value, string expected) =>
        value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool Hex(JsonElement value, int size) =>
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text && text.Length == size && text.All(Uri.IsHexDigit);

    internal string ImagePath
    {
        get { RequireCurrent(); return runtime.ImagePath; }
    }

    internal void RequireObservedApplicationModules(IEnumerable<string> modulePaths)
    {
        RequireCurrent();
        runtime.RequireObservedApplicationModules(modulePaths);
    }

    internal void RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        AdminOnlyFile.RequireProtectedExecutable(path);
        CefRuntimeFileLease.RequireCanonicalSingleLink(registration.SafeFileHandle, path);
        runtime.RequireCurrent();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { runtime.Dispose(); }
        finally { registration.Dispose(); }
    }
}
