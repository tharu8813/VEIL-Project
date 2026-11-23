using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

/// <summary>Outcome of one download request handled by the vault.</summary>
internal sealed record VaultResult(string Name, string Outcome, long Bytes, string? Detail, string? ObjectPath,
    long EngineBytesBeforeCancel, long EngineBytesOnDisk);

/// <summary>
/// Phase 4-0 prototype download vault. The engine's own download is always cancelled. Supported
/// requests (HTTP(S) GET, cookie-authenticated, redirected) are fetched again by VEIL and streamed
/// straight into the Rust core (veil-vault.exe), so VEIL never writes download plaintext to disk.
/// Anything else is blocked; there is no plaintext fallback.
///
/// Prototype limits: one keyring per session with an in-memory random passphrase (objects are not
/// readable after VEIL exits), metadata kept in memory, no export or UI yet.
/// </summary>
internal sealed class DownloadVault : IDisposable
{
    internal const string Stored = "보관됨";
    internal const string Blocked = "차단됨";
    internal const string Failed = "실패";
    private const int FrameSize = 64 * 1024;
    private const int MaxRedirects = 10;

    private readonly string directory;
    private readonly string? tool;
    private readonly string passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string keyring;
    private readonly HttpClient http;
    private readonly ConcurrentDictionary<string, string> methods = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim initLock = new(1, 1);
    private bool initialized;

    internal DownloadVault(string profileRoot)
    {
        directory = Path.Combine(profileRoot, "vault", DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        keyring = Path.Combine(directory, "session-keyring.age");
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
    internal string Directory => directory;
    internal List<VaultResult> Results { get; } = [];

    /// <summary>Remembers the method of each top-level navigation so a POST download is never replayed as GET.</summary>
    internal void NoteRequest(string method, string uri)
    {
        if (methods.Count > 256) methods.Clear();
        methods[uri] = method;
    }

    /// <summary>Decides support synchronously (the engine download is cancelled by the caller first).</summary>
    internal string? Unsupported(Uri uri)
    {
        if (!Available) return "금고 사용 불가";
        if (uri.Scheme is not ("http" or "https")) return $"미지원 방식({uri.Scheme})";
        if (methods.TryGetValue(uri.AbsoluteUri, out var method) && !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return $"미지원 방식({method})";
        return null;
    }

    /// <summary>Fetches the URL with the page's cookies and seals the body into a new vault object.</summary>
    internal async Task<(long Bytes, string Path)> CaptureAsync(Uri uri, CoreWebView2 web, IProgress<long>? progress, CancellationToken cancel)
    {
        await EnsureKeyringAsync();
        string objectPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".veil");
        using var response = await GetFollowingRedirectsAsync(uri, web, cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        long? declared = response.Content.Headers.ContentLength;

        using var process = Start("seal", keyring, objectPath);
        var stdin = process.StandardInput.BaseStream;
        long total = 0;
        bool complete = false;
        try
        {
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(passphrase + "\n"), cancel);
            await using var body = await response.Content.ReadAsStreamAsync(cancel);
            var buffer = new byte[FrameSize];
            for (int read; (read = await body.ReadAsync(buffer, cancel)) > 0;)
            {
                await stdin.WriteAsync(BitConverter.GetBytes(read), cancel);
                await stdin.WriteAsync(buffer.AsMemory(0, read), cancel);
                total += read;
                progress?.Report(total);
            }
            CryptographicOperations.ZeroMemory(buffer);
            if (declared is long expected && expected != total)
                throw new IOException($"Download ended at {total} of {expected} bytes.");
            await stdin.WriteAsync(BitConverter.GetBytes(0), cancel);
            await stdin.WriteAsync(BitConverter.GetBytes(total), cancel);
            complete = true;
        }
        finally
        {
            // Without the terminator the core refuses to publish and removes its staging file.
            try { stdin.Close(); } catch (IOException) { }
            if (!complete && !process.WaitForExit(10_000)) process.Kill();
        }
        string output = await process.StandardOutput.ReadToEndAsync(cancel);
        await process.WaitForExitAsync(cancel);
        if (process.ExitCode != 0 || !File.Exists(objectPath))
            throw new IOException("The vault did not accept the download.");
        long sealedBytes = JsonDocument.Parse(output).RootElement.GetProperty("bytes").GetInt64();
        if (sealedBytes != total) throw new IOException("Vault length mismatch.");
        return (total, objectPath);
    }

    /// <summary>Authenticates a stored object end to end and returns its plaintext length.</summary>
    internal async Task<long> VerifyAsync(string objectPath)
    {
        using var process = Start("verify", keyring, objectPath);
        await process.StandardInput.WriteAsync(passphrase + "\n");
        process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Vault object failed authentication.");
        return JsonDocument.Parse(output).RootElement.GetProperty("bytes").GetInt64();
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

    private async Task EnsureKeyringAsync()
    {
        await initLock.WaitAsync();
        try
        {
            if (initialized) return;
            System.IO.Directory.CreateDirectory(directory);
            using var process = Start("init", keyring);
            await process.StandardInput.WriteAsync(passphrase + "\n");
            process.StandardInput.Close();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("Could not create the vault keyring.");
            initialized = true;
        }
        finally { initLock.Release(); }
    }

    private Process Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(tool ?? throw new InvalidOperationException("veil-vault.exe missing"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Could not start veil-vault.exe");
    }

    public void Dispose() => http.Dispose();
}
