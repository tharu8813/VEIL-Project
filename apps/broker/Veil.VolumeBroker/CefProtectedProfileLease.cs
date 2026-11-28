// SPDX-License-Identifier: MPL-2.0
using Veil.Desktop;

namespace Veil.VolumeBroker;

// Disconnected, read-only protected profile preparation. All existing
// non-root ancestors and fixed CEF directories are retained together.
// The caller must independently authenticate the registered profile path,
// Windows system directory and generation. No browser launch authority.
internal sealed class CefProtectedProfileLease : IDisposable
{
    private readonly CefProfileLayout layout;
    private readonly IReadOnlyDictionary<string, string?> environment;
    private readonly string volume;
    private readonly Func<BackendStatus> readStatus;
    private readonly Func<bool> authorityCurrent;
    private readonly List<CefProtectedDirectoryLease> directories;
    private readonly CefProtectedDirectoryLease.RegularFilePin? log;
    private CefProtectedDirectoryLease.ExclusiveProfileLock? profileLock;
    private bool retired;
    private bool disposed;

    private CefProtectedProfileLease(CefProfileLayout layout,
        IReadOnlyDictionary<string, string?> environment, string volume,
        Func<BackendStatus> readStatus, Func<bool> authorityCurrent,
        List<CefProtectedDirectoryLease> directories, CefProtectedDirectoryLease.RegularFilePin? log)
    {
        this.layout = layout;
        this.environment = environment;
        this.volume = volume;
        this.readStatus = readStatus;
        this.authorityCurrent = authorityCurrent;
        this.directories = directories;
        this.log = log;
    }

    internal static CefProtectedProfileLease Open(string registeredProductRoot,
        string registeredVolume, string windowsDirectory, Func<BackendStatus> readStatus,
        Func<bool> authorityCurrent)
    {
        ArgumentNullException.ThrowIfNull(readStatus);
        ArgumentNullException.ThrowIfNull(authorityCurrent);
        RequireAuthority(registeredVolume, readStatus, authorityCurrent);
        var environment = CefChildEnvironmentPlan.BuildBrokerProtected(registeredProductRoot, windowsDirectory);
        var layout = new CefProfileLayout(registeredProductRoot);
        // Retain every non-root ancestor in root-to-leaf order. Each
        // handle independently checks the volume GUID and full DOS path.
        // Mounted directories, network paths and reparse targets cannot
        // substitute for the registered local drive.
        var ancestors = new Stack<string>();
        for (string? path = layout.ProductRoot; path is not null &&
            !string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);
            path = Path.GetDirectoryName(path))
        {
            if (ancestors.Count >= 64)
                throw new UnauthorizedAccessException("CEF profile path depth exceeds bound.");
            ancestors.Push(path);
        }
        var pins = new List<CefProtectedDirectoryLease>(ancestors.Count + 4);
        CefProtectedDirectoryLease.RegularFilePin? log = null;
        try
        {
            foreach (string path in ancestors.Concat(layout.Directories))
            {
                RequireAuthority(registeredVolume, readStatus, authorityCurrent);
                pins.Add(CefProtectedDirectoryLease.Open(path, registeredVolume, readStatus));
            }
            if (LogExists(layout.LogFile))
                log = CefProtectedDirectoryLease.OpenRegularFile(layout.LogFile, registeredVolume, readStatus);
            var lease = new CefProtectedProfileLease(layout, environment, registeredVolume,
                readStatus, authorityCurrent, pins, log);
            lease.RequireCurrent();
            return lease;
        }
        catch
        {
            log?.Dispose();
            for (int i = pins.Count - 1; i >= 0; --i) pins[i].Dispose();
            throw;
        }
    }

    private static void RequireAuthority(string volume, Func<BackendStatus> readStatus,
        Func<bool> authorityCurrent)
    {
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("CEF profile generation unavailable.");
        CefProtectedDirectoryLease.RequireRegisteredProtection(volume, readStatus());
        if (!authorityCurrent())
            throw new UnauthorizedAccessException("CEF profile generation changed.");
    }

    private static bool LogExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
    }

    private void RequireLog()
    {
        if (log is not null)
            log.RequireCurrent(readStatus);
        else if (LogExists(layout.LogFile))
            throw new UnauthorizedAccessException("CEF log appeared after preparation.");
    }

    // Bounded snapshot of the existing CEF subtree, not continuous
    // filesystem monitoring. Keep the current ancestor chain open during
    // traversal; never follow an unverified directory or skip hidden files.
    private void InspectExistingTree()
    {
        int visited = 0;
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        void Inspect(string path, int depth)
        {
            if (depth > 64)
                throw new UnauthorizedAccessException("CEF subtree depth exceeds bound.");
            using var directory = CefProtectedDirectoryLease.Open(path, volume, readStatus);
            foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", options))
            {
                if (++visited > 100000)
                    throw new UnauthorizedAccessException("CEF subtree entry count exceeds bound.");
                if (!authorityCurrent())
                    throw new UnauthorizedAccessException("CEF profile generation changed.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("CEF subtree contains redirected entry.");
                if ((attributes & FileAttributes.Directory) != 0)
                    Inspect(entry, depth + 1);
                else
                {
                    using var file = CefProtectedDirectoryLease.OpenRegularFile(entry, volume, readStatus);
                }
            }
            directory.RequireCurrent(readStatus);
        }

        Inspect(layout.EngineRoot, 0);
    }

    // One-shot lock acquisition only after validating all existing profile
    // directories. Existing desktop ownership is a refusal, not a takeover.
    // The future caller must arrange an authenticated handoff before this
    // can succeed. No lock file is created by the broker.
    internal void AcquireExclusiveLock()
    {
        RequireCurrent();
        if (profileLock is not null)
            throw new UnauthorizedAccessException("CEF profile lock already acquired.");
        try
        {
            profileLock = CefProtectedDirectoryLease.OpenExclusiveProfileLock(
                layout.ProductRoot, volume, readStatus);
            RequireCurrent();
        }
        catch
        {
            retired = true;
            throw;
        }
    }

    internal void RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (retired)
            throw new UnauthorizedAccessException("CEF profile lifetime retired.");
        try
        {
            RequireAuthority(volume, readStatus, authorityCurrent);
            foreach (var directory in directories) directory.RequireCurrent(readStatus);
            profileLock?.RequireCurrent(readStatus);
            RequireLog();
            InspectExistingTree();
            RequireLog();
            profileLock?.RequireCurrent(readStatus);
            RequireAuthority(volume, readStatus, authorityCurrent);
        }
        catch
        {
            retired = true;
            throw;
        }
    }

    // Read-only snapshot, not a permit. No native creation path consumes
    // this plan yet; the future caller must retain this lease through drain.
    internal IReadOnlyDictionary<string, string?> VerifiedEnvironment()
    {
        RequireCurrent();
        return environment;
    }

    // Installed preparation must not accidentally expose a launch
    // environment from a mere read-only profile inspection. Only the
    // broker's own retained exclusive lock qualifies, not a busy-file
    // observation or a desktop claim. Still not permission to resume.
    internal IReadOnlyDictionary<string, string?> VerifiedExclusiveEnvironment()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (profileLock is null)
            throw new UnauthorizedAccessException("CEF exclusive profile ownership required.");
        return VerifiedEnvironment();
    }

    public void Dispose()
    {
        if (disposed) return;
        retired = disposed = true;
        profileLock?.Dispose();
        log?.Dispose();
        for (int i = directories.Count - 1; i >= 0; --i) directories[i].Dispose();
    }
}
