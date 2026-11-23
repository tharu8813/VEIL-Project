using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

/// <summary>Outcome of one download request handled by the vault (for status and tests).</summary>
internal sealed record VaultResult(string Name, string Outcome, long Bytes, string? Detail, string? ObjectPath,
    long EngineBytesBeforeCancel, long EngineBytesOnDisk);

/// <summary>One stored download, as kept in the encrypted index and per-item metadata.</summary>
internal sealed record VaultEntry(string Id, string Name, long Bytes, DateTimeOffset SavedAt, string? SourceHost, bool Recovered = false);

/// <summary>
/// Persistent encrypted download vault (ADR-0005). Keys live only in a long-lived
/// <c>veil-vault serve</c> process; this class never keeps the passphrase. Download bodies stream
/// from the network into that process, so VEIL never writes download plaintext to disk.
/// </summary>
internal sealed class DownloadVault : IDisposable
{
    internal const string Stored = "보관됨";
    internal const string Blocked = "차단됨";
    internal const string Failed = "실패";
    private const int FrameSize = 64 * 1024;
    private const int MaxRedirects = 10;
    private const uint AbortFrame = uint.MaxValue;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string root;
    private readonly string? tool;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> methods = new(StringComparer.Ordinal);
    private Process? serve;
    private Stream? input;
    private Stream? output;

    internal DownloadVault(string profileRoot)
    {
        root = Path.Combine(profileRoot, "vault");
        string candidate = Path.Combine(AppContext.BaseDirectory, "veil-vault.exe");
        tool = File.Exists(candidate) ? candidate : null;
        http = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false, // protected-network routing is Phase 5; never silently use the system proxy
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal bool Available => tool is not null;
    internal bool Exists => File.Exists(Path.Combine(root, "keyring.age"));
    internal bool Unlocked => serve is { HasExited: false };
    internal string Directory => root;
    internal List<VaultEntry> Entries { get; private set; } = [];
    internal List<VaultResult> Results { get; } = [];
    internal event Action? Changed;

    // ── Opening ─────────────────────────────────────────────────────────────

    /// <summary>Creates the vault. The recovery code was shown to and re-entered by the user.</summary>
    internal Task<string?> CreateAsync(string passphrase, string recoveryCode) => StartAsync($"create\t{passphrase}\t{recoveryCode}");
    internal Task<string?> UnlockAsync(string passphrase) => StartAsync($"unlock\t{passphrase}");
    internal Task<string?> RecoverAsync(string recoveryCode) => StartAsync($"recover\t{NormalizeCode(recoveryCode)}");

    /// <summary>Debug-only test hook: VEIL_DEV_VAULT_PASSPHRASE / VEIL_DEV_VAULT_RECOVERY. Release ignores them.</summary>
    internal async Task<bool> TryOpenFromDevelopmentEnvironmentAsync()
    {
#if DEBUG
        if (Unlocked) return true;
        string? passphrase = Environment.GetEnvironmentVariable("VEIL_DEV_VAULT_PASSPHRASE");
        string? recovery = Environment.GetEnvironmentVariable("VEIL_DEV_VAULT_RECOVERY");
        if (string.IsNullOrEmpty(passphrase)) return false;
        if (Exists) return await UnlockAsync(passphrase) is null;
        return !string.IsNullOrEmpty(recovery) && await CreateAsync(passphrase, NormalizeCode(recovery)) is null;
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    private async Task<string?> StartAsync(string first)
    {
        if (tool is null) return "금고 도구가 없습니다.";
        if (first.Count(c => c == '\n' || c == '\r') > 0) return "암호에 줄바꿈을 쓸 수 없습니다.";
        await gate.WaitAsync();
        try
        {
            Lock();
            foreach (string dir in new[] { root, Objects, Meta, Index }) System.IO.Directory.CreateDirectory(dir);
            CleanStaging();
            var info = new ProcessStartInfo(tool)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("serve");
            info.ArgumentList.Add(root);
            serve = Process.Start(info) ?? throw new IOException("veil-vault could not start");
            input = serve.StandardInput.BaseStream;
            output = serve.StandardOutput.BaseStream;
            string reply = await CommandLockedAsync(first);
            if (reply != "ok")
            {
                Lock();
                return first.StartsWith("create") ? "금고를 만들지 못했습니다." : "암호 또는 복구 코드가 맞지 않습니다.";
            }
        }
        catch
        {
            Lock();
            throw;
        }
        finally { gate.Release(); }
        try { await LoadIndexAsync(); }
        catch
        {
            Lock();
            throw;
        }
        return null;
    }

    /// <summary>Ends the key-holding process; the vault must be opened again.</summary>
    internal void Lock()
    {
        try { if (serve is { HasExited: false }) { WriteQuit(); serve.WaitForExit(3000); } }
        catch (IOException) { }
        try { if (serve is { HasExited: false }) serve.Kill(); } catch (InvalidOperationException) { }
        serve?.Dispose();
        serve = null;
        input = output = null;
        Entries = [];
    }

    // ── Rotation ────────────────────────────────────────────────────────────

    internal async Task<bool> ChangePassphraseAsync(string passphrase) => await CommandAsync($"rewrap\t{passphrase}") == "ok";
    internal async Task<bool> ReissueRecoveryCodeAsync(string recoveryCode) => await CommandAsync($"rewrap-recovery\t{NormalizeCode(recoveryCode)}") == "ok";

    internal async Task<bool> CompleteRecoveryAsync(string passphrase, string recoveryCode)
    {
        bool completed = false;
        try
        {
            completed = await ChangePassphraseAsync(passphrase) && await ReissueRecoveryCodeAsync(recoveryCode);
            return completed;
        }
        finally
        {
            if (!completed) Lock();
        }
    }

    /// <summary>New recovery code: 8 groups of 5 base32 characters (200 random bits).</summary>
    internal static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 symbols, no 0/O/1/I
        var bytes = RandomNumberGenerator.GetBytes(40);
        var chars = bytes.Select(b => alphabet[b % 32]).ToArray();
        CryptographicOperations.ZeroMemory(bytes);
        return string.Join("-", Enumerable.Range(0, 8).Select(i => new string(chars, i * 5, 5)));
    }

    internal static string NormalizeCode(string code) =>
        string.Join("-", new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()).Chunk(5).Select(c => new string(c)));

    // ── Capture ─────────────────────────────────────────────────────────────

    internal void NoteRequest(string method, string uri)
    {
        lock (methods)
        {
            if (methods.Count > 256) methods.Clear();
            methods[uri] = method;
        }
    }

    internal string? Unsupported(Uri uri)
    {
        if (!Available) return "금고 사용 불가";
        if (uri.Scheme is not ("http" or "https")) return $"미지원 방식({uri.Scheme})";
        lock (methods)
            if (methods.TryGetValue(uri.AbsoluteUri, out var method) && !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return $"미지원 방식({method})";
        return null;
    }

    /// <summary>Fetches the URL with the page's cookies and seals the body as a new vault item.</summary>
    internal async Task<VaultEntry> CaptureAsync(Uri uri, string name, CoreWebView2 web, IProgress<long>? progress, CancellationToken cancel)
    {
        if (!Unlocked) throw new InvalidOperationException("The vault is locked.");
        using var response = await GetFollowingRedirectsAsync(uri, web, cancel);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        long? declared = response.Content.Headers.ContentLength;
        string id = Guid.NewGuid().ToString("N");
        long total = 0;
        await gate.WaitAsync(cancel);
        try
        {
            await WriteLineAsync($"seal\t{Path.Combine(Objects, id + ".veil")}");
            bool complete = false;
            var buffer = new byte[FrameSize];
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancel);
                for (int read; (read = await body.ReadAsync(buffer, cancel)) > 0;)
                {
                    await input!.WriteAsync(BitConverter.GetBytes(read), cancel);
                    await input.WriteAsync(buffer.AsMemory(0, read), cancel);
                    total += read;
                    progress?.Report(total);
                }
                if (declared is long expected && expected != total)
                    throw new IOException($"Download ended at {total} of {expected} bytes.");
                await input!.WriteAsync(BitConverter.GetBytes(0), cancel);
                await input.WriteAsync(BitConverter.GetBytes(total), cancel);
                complete = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                if (!complete)
                {
                    // An abort frame ends this object without publishing it. Consume the core's reply so the
                    // long-lived session stays in sync for the next command.
                    await input!.WriteAsync(BitConverter.GetBytes(AbortFrame), CancellationToken.None);
                    await input.FlushAsync(CancellationToken.None);
                    await ReadLineAsync();
                }
                else await input!.FlushAsync(CancellationToken.None);
            }
            string reply = await ReadLineAsync();
            if (reply != $"ok\t{total}") throw new IOException("The vault did not accept the download.");
            var entry = new VaultEntry(id, name, total, DateTimeOffset.Now, uri.Host);
            await SealBytesLockedAsync(Path.Combine(Meta, id + ".veil"), JsonSerializer.SerializeToUtf8Bytes(entry, Json));
            Entries = [entry, .. Entries];
            await SaveIndexLockedAsync();
            return entry;
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    // ── Items ───────────────────────────────────────────────────────────────

    /// <summary>Decrypts an item to a new file; it appears only after full authentication.</summary>
    internal async Task<long> ExportAsync(VaultEntry entry, string destination)
    {
        string reply = await CommandAsync($"export\t{ObjectPath(entry)}\t{destination}");
        return reply.StartsWith("ok\t") ? long.Parse(reply[3..]) : throw new IOException("Export failed: the item did not authenticate or the file exists.");
    }

    internal async Task<long> VerifyAsync(VaultEntry entry)
    {
        string reply = await CommandAsync($"verify\t{ObjectPath(entry)}");
        return reply.StartsWith("ok\t") ? long.Parse(reply[3..]) : throw new IOException("The item failed authentication.");
    }

    internal async Task DeleteAsync(VaultEntry entry)
    {
        await gate.WaitAsync();
        try
        {
            File.Delete(ObjectPath(entry));
            File.Delete(Path.Combine(Meta, entry.Id + ".veil"));
            Entries = Entries.Where(e => e.Id != entry.Id).ToList();
            await SaveIndexLockedAsync();
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    internal string ObjectPath(VaultEntry entry) => Path.Combine(Objects, entry.Id + ".veil");

    // ── Index ───────────────────────────────────────────────────────────────

    /// <summary>Newest authenticating index generation; otherwise rebuilt from per-item metadata. Objects
    /// without metadata are listed as recovered items; metadata without an object is dropped.</summary>
    internal async Task LoadIndexAsync()
    {
        await gate.WaitAsync();
        try
        {
            List<VaultEntry>? loaded = null;
            foreach (var file in new DirectoryInfo(Index).GetFiles("*.veil").OrderByDescending(f => f.Name))
            {
                var bytes = await ReadSmallLockedAsync(file.FullName);
                if (bytes is null) continue;
                try { loaded = JsonSerializer.Deserialize<List<VaultEntry>>(bytes, Json); } catch (JsonException) { }
                if (loaded is not null) break;
            }
            var objects = new DirectoryInfo(Objects).GetFiles("*.veil")
                .Select(file => Path.GetFileNameWithoutExtension(file.Name))
                .Where(id => Guid.TryParseExact(id, "N", out _))
                .ToHashSet();
            bool rebuilt = loaded is null || loaded.Select(e => e.Id).ToHashSet().SetEquals(objects) is false;
            if (rebuilt)
            {
                var entries = new List<VaultEntry>();
                foreach (string id in objects)
                {
                    var meta = await ReadSmallLockedAsync(Path.Combine(Meta, id + ".veil"));
                    VaultEntry? entry = null;
                    if (meta is not null) try { entry = JsonSerializer.Deserialize<VaultEntry>(meta, Json); } catch (JsonException) { }
                    entries.Add(entry is not null && entry.Id == id ? entry
                        : new VaultEntry(id, $"복구된 항목 {id[..8]}", new FileInfo(Path.Combine(Objects, id + ".veil")).Length, DateTimeOffset.Now, null, Recovered: true));
                }
                loaded = entries.OrderByDescending(e => e.SavedAt).ToList();
            }
            Entries = loaded!;
            if (rebuilt) await SaveIndexLockedAsync();
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    private async Task SaveIndexLockedAsync()
    {
        string name = DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + ".veil";
        await SealBytesLockedAsync(Path.Combine(Index, name), JsonSerializer.SerializeToUtf8Bytes(Entries, Json));
        foreach (var old in new DirectoryInfo(Index).GetFiles("*.veil").OrderByDescending(f => f.Name).Skip(2))
            try { old.Delete(); } catch (IOException) { }
    }

    // ── Protocol ────────────────────────────────────────────────────────────

    private string Objects => Path.Combine(root, "objects");
    private string Meta => Path.Combine(root, "meta");
    private string Index => Path.Combine(root, "index");

    private void CleanStaging()
    {
        foreach (string dir in new[] { root, Objects, Meta, Index })
            foreach (var file in new DirectoryInfo(dir).GetFiles(".veil-encrypted-*"))
                try { file.Delete(); } catch (IOException) { }
    }

    private async Task<string> CommandAsync(string line)
    {
        await gate.WaitAsync();
        try { return Unlocked ? await CommandLockedAsync(line) : "err\tlocked"; }
        finally { gate.Release(); }
    }

    private async Task<string> CommandLockedAsync(string line)
    {
        await WriteLineAsync(line);
        await input!.FlushAsync();
        return await ReadLineAsync();
    }

    private async Task SealBytesLockedAsync(string path, byte[] data)
    {
        await WriteLineAsync($"seal\t{path}");
        await input!.WriteAsync(BitConverter.GetBytes(data.Length));
        await input.WriteAsync(data);
        await input.WriteAsync(BitConverter.GetBytes(0));
        await input.WriteAsync(BitConverter.GetBytes((long)data.Length));
        await input.FlushAsync();
        CryptographicOperations.ZeroMemory(data);
        string reply = await ReadLineAsync();
        if (reply != $"ok\t{data.Length}") throw new IOException($"The vault did not store metadata ({reply}).");
    }

    private async Task<byte[]?> ReadSmallLockedAsync(string path)
    {
        if (!File.Exists(path)) return null;
        string reply = await CommandLockedAsync($"read\t{path}");
        if (!reply.StartsWith("ok\t")) return null;
        var bytes = new byte[int.Parse(reply[3..])];
        await output!.ReadExactlyAsync(bytes);
        return bytes;
    }

    /// <summary>Command lines and frames must use the same (async) write path: mixing a buffered sync
    /// Write with WriteAsync on the pipe reorders bytes and desynchronises the protocol.</summary>
    private async Task WriteLineAsync(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await input!.WriteAsync(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private void WriteQuit()
    {
        input!.Write("quit\n"u8);
        input.Flush();
    }

    private async Task<string> ReadLineAsync()
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await output!.ReadAsync(one) == 0) throw new IOException("The vault process ended.");
            if (one[0] == (byte)'\n') break;
            bytes.Add(one[0]);
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri uri, CoreWebView2 web, CancellationToken cancel)
    {
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (uri.Scheme is not ("http" or "https")) throw new HttpRequestException("Redirect left HTTP(S).");
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", web.Settings.UserAgent);
            // Cookies are looked up per hop so each origin only receives its own cookies.
            var cookies = await web.CookieManager.GetCookiesAsync(uri.AbsoluteUri);
            if (cookies.Count > 0)
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new HttpRequestException("Too many redirects.");
    }

    public void Dispose()
    {
        Lock();
        http.Dispose();
    }
}
