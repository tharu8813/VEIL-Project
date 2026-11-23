using System.IO;
using System.Text;

namespace Veil.Desktop;

/// <summary>
/// SEC-004 / ADR-0004 policy 2 mitigation. The WebView2 engine registers Edge taskbar tasks
/// ("New window", "New InPrivate window") whose command lines contain <c>--user-data-dir="&lt;profile&gt;"</c>,
/// so the protected profile location lands in %APPDATA%\Microsoft\Windows\Recent\CustomDestinations.
/// No engine switch, feature or policy stops this (docs/05). The Windows shell writes the list itself,
/// so VEIL cannot prevent the write; it removes every custom jump list that mentions this profile as soon
/// as it appears, at start and after the engine exits. Only files that contain the exact engine profile
/// path are touched, so jump lists of other applications (including Microsoft Edge) are left alone.
/// The transient write on the system disk remains and is reported by the trace tests.
/// </summary>
internal sealed class JumpListScrubber : IDisposable
{
    private const long MaxListBytes = 4 * 1024 * 1024;
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent", "CustomDestinations");

    private readonly byte[][] needles;
    private readonly FileSystemWatcher? watcher;
    private readonly Timer debounce;
    private int removed;

    internal JumpListScrubber(string engineProfile)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(engineProfile));
        // The shell stores shell-link arguments as UTF-16; search at any byte offset, ASCII case-insensitive
        // (the same byte folding is applied to file and needle), plus UTF-8 for any other representation.
        needles = [Lower(Encoding.Unicode.GetBytes(path)), Lower(Encoding.UTF8.GetBytes(path))];
        debounce = new Timer(_ => Scrub(), null, Timeout.Infinite, Timeout.Infinite);
        if (!Directory.Exists(Folder)) return;
        try
        {
            watcher = new FileSystemWatcher(Folder, "*.customDestinations-ms")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Created += (_, _) => Soon();
            watcher.Changed += (_, _) => Soon();
            watcher.Renamed += (_, _) => Soon();
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            watcher = null; // start/exit scrubs still run
        }
    }

    /// <summary>Lists removed in this run (diagnostics and tests).</summary>
    internal int Removed => Volatile.Read(ref removed);

    private void Soon() => debounce.Change(300, Timeout.Infinite);

    /// <summary>Deletes every custom jump list that mentions this profile. Returns how many were removed.</summary>
    internal int Scrub()
    {
        int count = 0;
        if (!Directory.Exists(Folder)) return 0;
        string[] files;
        try { files = Directory.GetFiles(Folder, "*.customDestinations-ms"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
        foreach (string file in files)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Mentions(file)) break;
                    File.Delete(file);
                    count++;
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100); // the shell may still hold the file while committing
                }
            }
        }
        Interlocked.Add(ref removed, count);
        return count;
    }

    private bool Mentions(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length > MaxListBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        byte[] bytes = File.ReadAllBytes(file);
        byte[] lower = Lower(bytes);
        return needles.Any(needle => Contains(lower, needle));
    }

    /// <summary>ASCII-lowercases bytes in place on a copy (paths are compared case-insensitively).</summary>
    private static byte[] Lower(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        for (int i = 0; i < copy.Length; i++)
            if (copy[i] is >= (byte)'A' and <= (byte)'Z') copy[i] = (byte)(copy[i] + 32);
        return copy;
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>After the window closed: the engine may commit its list while shutting down.</summary>
    internal void ScrubAfterExit()
    {
        for (int round = 0; round < 6; round++)
        {
            Scrub();
            Thread.Sleep(500);
        }
        Scrub();
    }

    internal static string ListFolder => Folder;

    public void Dispose()
    {
        watcher?.Dispose();
        debounce.Dispose();
    }
}
