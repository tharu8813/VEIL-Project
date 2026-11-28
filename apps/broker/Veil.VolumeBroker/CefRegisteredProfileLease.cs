// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Veil.Desktop;
using Veil.Shared;

namespace Veil.VolumeBroker;

// Broker-side installed registration authority. The profile path comes
// only from the fixed administrator-controlled client registration, not
// IPC, environment, command line or a nominated process. No launch permit.
internal sealed class CefRegisteredProfileLease : IDisposable
{
    private const int MaxBytes = 64 * 1024;
    private readonly FileStream registration;
    private readonly string registrationPath;
    private readonly byte[] original;
    private readonly CefProtectedProfileLease profile;
    private bool retired;
    private bool disposed;

    private CefRegisteredProfileLease(FileStream registration, string registrationPath,
        byte[] original, CefProtectedProfileLease profile)
    {
        this.registration = registration;
        this.registrationPath = registrationPath;
        this.original = original;
        this.profile = profile;
    }

    internal static CefRegisteredProfileLease OpenInstalled(string volumeId, string volumeGuid,
        string pipeName, Func<BackendStatus> readStatus, Func<bool> authorityCurrent)
    {
        ArgumentNullException.ThrowIfNull(readStatus);
        ArgumentNullException.ThrowIfNull(authorityCurrent);
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("CEF profile registration generation unavailable.");
        string expectedBroker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "VEIL", "Broker", "Veil.VolumeBroker.exe");
        if (!string.Equals(Environment.ProcessPath, expectedBroker, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF profile registration requires installed broker.");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows) ||
            !string.Equals(Path.Combine(windows, "System32"), Environment.SystemDirectory,
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CEF Windows directory unavailable.");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VEIL", "client.json");
        AdminOnlyFile.RequireProtectedExecutable(path);
        var registration = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CefProtectedProfileLease? profile = null;
        try
        {
            byte[] original = ReadPinned(registration, path);
            string root = Parse(original, volumeId, pipeName, expectedBroker, windows);
            if (!authorityCurrent())
                throw new UnauthorizedAccessException("CEF profile registration generation changed.");
            profile = CefProtectedProfileLease.OpenExclusive(root, volumeGuid, windows, readStatus, authorityCurrent);
            var lease = new CefRegisteredProfileLease(registration, path, original, profile);
            lease.RequireCurrent();
            return lease;
        }
        catch
        {
            try { profile?.Dispose(); }
            finally { registration.Dispose(); }
            throw;
        }
    }

    private static byte[] ReadPinned(FileStream file, string path)
    {
        AdminOnlyFile.RequireProtectedExecutable(path);
        Native.RequireCanonicalSingleLinkImage(file.SafeFileHandle, path);
        if (file.Length is <= 0 or > MaxBytes)
            throw new InvalidDataException("Invalid CEF client registration length.");
        file.Position = 0;
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.Length != bytes.Length)
            throw new InvalidDataException("CEF client registration length changed.");
        AdminOnlyFile.RequireProtectedExecutable(path);
        Native.RequireCanonicalSingleLinkImage(file.SafeFileHandle, path);
        return bytes;
    }

    // Strict parser compatible with the six fields written by the existing
    // preflight installer. Unknown, repeated or wrongly typed fields refuse.
    // Pure parser tests do not assert installed ACL or BitLocker evidence.
    internal static string Parse(ReadOnlyMemory<byte> bytes, string volumeId,
        string pipeName, string expectedBroker, string windowsDirectory)
    {
        if (bytes.Length is <= 0 or > MaxBytes || string.IsNullOrWhiteSpace(volumeId) ||
            volumeId.Length > 64 || pipeName != "VEIL.VolumeBroker" ||
            !Path.IsPathFullyQualified(expectedBroker))
            throw new InvalidDataException("Invalid CEF client registration context.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
        var root = document.RootElement;
        var remaining = new HashSet<string>(["volumeId", "pipeName", "brokerImage",
            "profilePath", "requireWindowsService", "development"], StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid CEF client registration object.");
        foreach (var property in root.EnumerateObject())
            if (!remaining.Remove(property.Name))
                throw new InvalidDataException("Unknown or duplicate CEF client registration field.");
        if (remaining.Count != 0 || !TextEquals(root.GetProperty("volumeId"), volumeId, StringComparison.Ordinal) ||
            !TextEquals(root.GetProperty("pipeName"), pipeName, StringComparison.Ordinal) ||
            !TextEquals(root.GetProperty("brokerImage"), expectedBroker, StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("requireWindowsService").ValueKind != JsonValueKind.True ||
            root.GetProperty("development").ValueKind != JsonValueKind.False ||
            root.GetProperty("profilePath").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("CEF client registration does not match broker.");
        string path = root.GetProperty("profilePath").GetString()!;
        _ = CefChildEnvironmentPlan.BuildBrokerProtected(path, windowsDirectory);
        return path;
    }

    private static bool TextEquals(JsonElement field, string expected, StringComparison comparison) =>
        field.ValueKind == JsonValueKind.String && string.Equals(field.GetString(), expected, comparison);

    internal void RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (retired)
            throw new UnauthorizedAccessException("CEF registered profile lifetime retired.");
        try
        {
            if (!ReadPinned(registration, registrationPath).AsSpan().SequenceEqual(original))
                throw new UnauthorizedAccessException("CEF client registration changed.");
            profile.RequireCurrent();
            if (!ReadPinned(registration, registrationPath).AsSpan().SequenceEqual(original))
                throw new UnauthorizedAccessException("CEF client registration changed.");
        }
        catch
        {
            retired = true;
            throw;
        }
    }

#if VEIL_CEF_BROKER || VEIL_CEF_PROFILE_ACCESS_TEST
    internal IReadOnlyDictionary<string, string?> GetEnvironmentSnapshot(
        CefProcessTokenObserver.LaunchTokenLease token)
    {
        ArgumentNullException.ThrowIfNull(token);
        RequireCurrent();
        try
        {
            token.RequireProfileAccess(profile);
            var environment = profile.VerifiedExclusiveEnvironment();
            RequireCurrent();
            token.RequireCurrent();
            return environment;
        }
        catch
        {
            retired = true;
            throw;
        }
    }
#endif

    public void Dispose()
    {
        if (disposed) return;
        retired = disposed = true;
        try { profile.Dispose(); }
        finally { registration.Dispose(); }
    }
}
