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
internal sealed record VaultBackupSource(string Path, string Password, string RecoveryCode);

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
    internal const string Cancelled = "취소됨";
    private const int FrameSize = 64 * 1024;
    private const int MaxRedirects = 10;
    private const uint AbortFrame = uint.MaxValue;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string root;
    private string activeRoot;
    private readonly string? tool;
    private HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> methods = new(StringComparer.Ordinal);
    private Process? serve;
    private Stream? input;
    private Stream? output;

    internal DownloadVault(string profileRoot, bool usesProfilePassword = false)
    {
        UsesProfilePassword = usesProfilePassword;
        root = Path.Combine(profileRoot, "vault");
        activeRoot = root;
        string candidate = Path.Combine(AppContext.BaseDirectory, "veil-vault.exe");
        tool = File.Exists(candidate) ? candidate : null;
        http = new HttpClient(NetworkPolicy.Handler(NetworkSettings.Standard)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>ADR-0006: VEIL's own download requests follow the engine's network mode. In proxy mode they
    /// use only that proxy (no system proxy, no direct fallback).</summary>
    internal void ConfigureNetwork(NetworkSettings settings)
    {
        var previous = http;
        http = new HttpClient(NetworkPolicy.Handler(settings)) { Timeout = Timeout.InfiniteTimeSpan };
        previous.Dispose();
    }

    internal bool Available => tool is not null;
    internal bool UsesProfilePassword { get; }
    internal bool Exists => File.Exists(Path.Combine(root, "active-generation")) ||
        File.Exists(Path.Combine(root, "keyring.bundle")) ||
        File.Exists(Path.Combine(root, "keyring.age")) ||
        File.Exists(Path.Combine(root, "keyring.recovery.age"));
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
    internal Task<string?> RestoreAsync(VaultBackupSource backup) =>
        StartAsync($"restore\t{backup.Path}\t{backup.Password}\t{NormalizeCode(backup.RecoveryCode)}", restoring: true);

    internal async Task<long> BackupAsync(string destination, string password)
    {
        string reply = await CommandAsync($"backup\t{destination}\t{password}");
        if (reply.StartsWith("ok\t", StringComparison.Ordinal) && long.TryParse(reply.AsSpan(3), out long count))
            return count;
        throw new IOException("백업을 만들지 못했습니다. 금고 무결성, 저장 공간과 파일 이름을 확인하세요.");
    }
    internal Task<string?> OpenWithProfilePasswordAsync(ReadOnlyMemory<char> password, string? recoveryCode = null) =>
        StartAsync(recoveryCode is null ? "unlock-profile" : "create-profile", password, recoveryCode);

    internal async Task<bool> BindProfilePasswordAsync(ReadOnlyMemory<char> password, string? recoveryCode)
    {
        await gate.WaitAsync();
        try
        {
            return Unlocked && await CommandLockedAsync(recoveryCode is null ? "rewrap-profile" : "recover-profile",
                password, recoveryCode) == "ok";
        }
        finally { gate.Release(); }
    }

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

    private async Task<string?> StartAsync(string first, ReadOnlyMemory<char> profilePassword = default,
        string? recoveryCode = null, bool restoring = false)
    {
        if (tool is null) return "금고 도구가 없습니다.";
        if (first.Count(c => c == '\n' || c == '\r') > 0) return "암호에 줄바꿈을 쓸 수 없습니다.";
        await gate.WaitAsync();
        try
        {
            Lock();
            if (!restoring) System.IO.Directory.CreateDirectory(root);
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
            string reply = await CommandLockedAsync(first, profilePassword, recoveryCode);
            if (reply != "ok")
            {
                Lock();
                if (restoring) return "백업을 복원하지 못했습니다. 백업 암호와 파일을 확인하세요. 복원에는 새 금고 위치가 필요합니다.";
                return first.StartsWith("create") ? "금고를 만들지 못했습니다." : "암호 또는 복구 코드가 맞지 않습니다.";
            }
            string location = await CommandLockedAsync("location");
            if (!location.StartsWith("ok\t", StringComparison.Ordinal))
                throw new IOException("The vault did not report its active storage.");
            SetActiveRoot(location[3..]);
            CleanStaging();
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
        KillServe();
        serve?.Dispose();
        serve = null;
        input = output = null;
        activeRoot = root;
        Entries = [];
    }

    private void KillServe()
    {
        try
        {
            if (serve is not { HasExited: false }) return;
            serve.Kill();
            serve.WaitForExit(3000);
        }
        catch (InvalidOperationException) { }
    }

    // ── Rotation ────────────────────────────────────────────────────────────

    internal async Task<bool> ChangePassphraseAsync(string passphrase) => await CommandAsync($"rewrap\t{passphrase}") == "ok";
    internal async Task<bool> ReissueRecoveryCodeAsync(string recoveryCode) => await CommandAsync($"rewrap-recovery\t{NormalizeCode(recoveryCode)}") == "ok";

    internal async Task<bool> CompleteRecoveryAsync(string passphrase, string recoveryCode)
    {
        bool completed = false;
        try
        {
            completed = await CommandAsync($"rewrap-both\t{passphrase}\t{NormalizeCode(recoveryCode)}") == "ok";
            return completed;
        }
        finally
        {
            if (!completed) Lock();
        }
    }

    internal async Task<bool?> RotateContentKeysAsync(ReadOnlyMemory<char> password, string recoveryCode)
    {
        await gate.WaitAsync();
        try
        {
            if (!Unlocked) return null;
            string reply = await CommandLockedAsync("rotate-content", password, recoveryCode);
            if (reply.StartsWith("err\t", StringComparison.Ordinal)) return null;
            string[] fields = reply.Split('\t');
            if (fields is not ["ok", _, "retired" or "retained"])
                throw new IOException("The vault did not report the rotation result.");
            SetActiveRoot(fields[1]);
            return fields[2] == "retired";
        }
        catch
        {
            Lock();
            throw;
        }
        finally { gate.Release(); Changed?.Invoke(); }
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
    internal async Task<VaultEntry> CaptureAsync(Uri uri, BrowserDownload download, CoreWebView2 web)
    {
        CancellationToken cancel = download.Token;
        if (!Unlocked) throw new InvalidOperationException("The vault is locked.");
        using var response = await GetFollowingRedirectsAsync(uri, web, cancel);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        long? declared = response.Content.Headers.ContentLength;
        string id = Guid.NewGuid().ToString("N");
        long total = 0;
        await gate.WaitAsync(cancel);
        try
        {
            if (!Unlocked) throw new InvalidOperationException("The vault is locked.");
            cancel.ThrowIfCancellationRequested();
            bool canAbort = false;
            var buffer = new byte[FrameSize];
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                await WriteLineAsync($"seal\t{Path.Combine(Objects, id + ".veil")}");
                canAbort = true;
                download.Report(0, declared);
                var body = await response.Content.ReadAsStreamAsync(cancel);
                for (int read; (read = await body.ReadAsync(buffer, cancel)) > 0;)
                {
                    cancel.ThrowIfCancellationRequested();
                    // A cancelled pipe write can leave half a frame and make a subsequent abort ambiguous.
                    canAbort = false;
                    await input!.WriteAsync(BitConverter.GetBytes(read));
                    await input.WriteAsync(buffer.AsMemory(0, read));
                    canAbort = true;
                    fileHash.AppendData(buffer, 0, read);
                    total += read;
                    download.Report(total, declared);
                }
                cancel.ThrowIfCancellationRequested();
                if (declared is long expected && expected != total)
                    throw new IOException($"Download ended at {total} of {expected} bytes.");
                // ADR-0008: a listed file is aborted before the commit frame, so it is never published.
                string sha256 = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
                await RejectThreatAsync(threats?.CheckFileAsync(sha256, TimeSpan.FromSeconds(2), cancel));
                download.Finish();
                canAbort = false;
                await input!.WriteAsync(BitConverter.GetBytes(0));
                await input.WriteAsync(BitConverter.GetBytes(total));
                await input.FlushAsync();
            }
            catch
            {
                if (canAbort)
                {
                    try
                    {
                        await input!.WriteAsync(BitConverter.GetBytes(AbortFrame));
                        await input.FlushAsync();
                        await ReadLineAsync();
                    }
                    catch (IOException) { CloseBrokenPipe(); }
                }
                else CloseBrokenPipe();
                throw;
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
            string reply = await ReadLineAsync();
            if (reply != $"ok\t{total}") throw new IOException("The vault did not accept the download.");
            var entry = new VaultEntry(id, download.Name, total, DateTimeOffset.Now, uri.Host);
            await SealBytesLockedAsync(Path.Combine(Meta, id + ".veil"), JsonSerializer.SerializeToUtf8Bytes(entry, Json));
            Entries = [entry, .. Entries];
            await SaveIndexLockedAsync();
            return entry;
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    private void CloseBrokenPipe()
    {
        KillServe();
        Lock();
    }

    // ── Items ───────────────────────────────────────────────────────────────

    /// <summary>Decrypts an item to a new file; it appears only after full authentication.</summary>
    internal async Task<long> ExportAsync(VaultEntry entry, string destination)
    {
        string reply = await ObjectCommandAsync("export", entry, destination);
        return reply.StartsWith("ok\t") ? long.Parse(reply[3..]) : throw new IOException("Export failed: the item did not authenticate or the file exists.");
    }

    internal async Task<long> VerifyAsync(VaultEntry entry)
    {
        string reply = await ObjectCommandAsync("verify", entry);
        return reply.StartsWith("ok\t") ? long.Parse(reply[3..]) : throw new IOException("The item failed authentication.");
    }

    internal async Task DeleteAsync(VaultEntry entry)
    {
        await gate.WaitAsync();
        try
        {
            if (!Unlocked) throw new IOException("The vault is locked.");
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

    private string Objects => Path.Combine(activeRoot, "objects");
    private string Meta => Path.Combine(activeRoot, "meta");
    private string Index => Path.Combine(activeRoot, "index");

    private void SetActiveRoot(string generation)
    {
        if (generation == "root") activeRoot = root;
        else if (generation.Length == 20 && generation.StartsWith("gen-", StringComparison.Ordinal) &&
            generation[4..].All(char.IsAsciiLetterOrDigit))
            activeRoot = Path.Combine(root, generation);
        else throw new IOException("The vault reported an invalid generation name.");

        foreach (string directory in new[] { activeRoot, Objects, Meta, Index })
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists && generation == "root") info.Create();
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The vault generation is missing or redirected.");
        }
    }

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

    private async Task<string> ObjectCommandAsync(string command, VaultEntry entry, string? destination = null)
    {
        await gate.WaitAsync();
        try
        {
            if (!Unlocked) return "err\tlocked";
            string line = $"{command}\t{ObjectPath(entry)}";
            if (destination is not null) line += "\t" + destination;
            return await CommandLockedAsync(line);
        }
        finally { gate.Release(); }
    }

    private async Task<string> CommandLockedAsync(string line, ReadOnlyMemory<char> profilePassword = default, string? recoveryCode = null)
    {
        if (profilePassword.IsEmpty) await WriteLineAsync(line);
        else await WriteProfileCommandAsync(line, profilePassword, recoveryCode);
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
        try { await input!.WriteAsync(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task WriteProfileCommandAsync(string command, ReadOnlyMemory<char> password, string? recoveryCode)
    {
        byte[] utf8 = new byte[Encoding.UTF8.GetByteCount(password.Span)];
        string suffix = recoveryCode is null ? "\n" : $"\t{NormalizeCode(recoveryCode)}\n";
        byte[] packet = new byte[command.Length + 1 + utf8.Length * 2 + Encoding.UTF8.GetByteCount(suffix)];
        try
        {
            Encoding.UTF8.GetBytes(password.Span, utf8);
            int offset = Encoding.ASCII.GetBytes(command, packet);
            packet[offset++] = (byte)'\t';
            foreach (byte value in utf8)
            {
                packet[offset++] = "0123456789abcdef"u8[value >> 4];
                packet[offset++] = "0123456789abcdef"u8[value & 15];
            }
            Encoding.UTF8.GetBytes(suffix, packet.AsSpan(offset));
            await input!.WriteAsync(packet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
            CryptographicOperations.ZeroMemory(packet);
        }
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

    private ThreatProtection? threats;

    /// <summary>ADR-0008: VEIL threat engines for download sources and file hashes (null: no checks).</summary>
    internal void ConfigureThreats(ThreatProtection protection) => threats = protection;

    /// <summary>Blocks a Malicious verdict. Unknown and Error pass (fail-open policy) and are not stored.</summary>
    private async Task RejectThreatAsync(Task<ThreatResult>? check)
    {
        if (check is null) return;
        var result = await check;
        if (result.Verdict == ThreatVerdict.Malicious) throw new ThreatBlockedException(result.Reason);
    }

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri uri, CoreWebView2 web, CancellationToken cancel)
    {
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (uri.Scheme is not ("http" or "https")) throw new HttpRequestException("Redirect left HTTP(S).");
            // Every hop is checked before VEIL requests it, so a redirect cannot route around the list.
            await RejectThreatAsync(threats?.CheckAsync(uri, TimeSpan.FromSeconds(2), cancel));
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
        http.Dispose();
        // At application exit a suspended download may still hold the pipe with an incomplete frame.
        if (!gate.Wait(0)) KillServe();
        else gate.Release();
        Lock();
    }
}
