using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Veil.Desktop;

/// <summary>A download or source a VEIL threat engine marked Malicious; nothing was published.</summary>
internal sealed class ThreatBlockedException(string reason) : IOException(reason);

/// <summary>State of the signed local threat list for the status UI.</summary>
internal sealed record ThreatListStatus(bool Present, bool Valid, string Detail, int Entries = 0, DateTimeOffset? Expires = null)
{
    internal static readonly ThreatListStatus None = new(false, false, "없음");
}

/// <summary>
/// VEIL threat engine 1.1 (ADR-0008): a signed local list of blocked hosts, exact URLs and file hashes.
/// Every lookup is in memory; nothing about the visited address leaves the machine. The list is
/// <c>security/threat-list.json</c> in the profile with a detached ECDSA P-256 / SHA-256 signature
/// (<c>threat-list.json.sig</c>, base64 DER). Only lists signed by a trusted key are used; a damaged,
/// unsigned or expired list makes every check return Error (fail-open with a visible status).
/// </summary>
internal sealed class LocalThreatList : IThreatEngine
{
    internal const string FileName = "threat-list.json";
    private const long MaxListBytes = 64L * 1024 * 1024;

    /// <summary>Public keys (SPKI, base64) whose lists VEIL accepts. Empty until the release signing key is
    /// created; a production build then uses no list (every check Unknown).</summary>
    internal static readonly string[] ProductionKeys = [];

    private readonly HashSet<string> hosts;
    private readonly HashSet<string> urls;
    private readonly HashSet<string> files;
    private readonly string? problem;
    private readonly DateTimeOffset? expires;
    private readonly TimeProvider clock;

    private string? CurrentProblem => problem ?? (expires is { } expiry && expiry <= clock.GetUtcNow() ? "만료된 목록" : null);

    private LocalThreatList(HashSet<string> hosts, HashSet<string> urls, HashSet<string> files, string? problem,
        DateTimeOffset? expires, TimeProvider clock)
    {
        this.hosts = hosts;
        this.urls = urls;
        this.files = files;
        this.problem = problem;
        this.expires = expires;
        this.clock = clock;
    }

    public string Name => "veil-local";
    public bool ProductionReady => true;

    internal static IEnumerable<string> TrustedKeys()
    {
        foreach (string key in ProductionKeys) yield return key;
#if DEBUG
        // Test-only signing key for synthetic lists. Never present in Release builds.
        if (Environment.GetEnvironmentVariable("VEIL_TEST_THREAT_LIST_KEY") is { Length: > 0 } test) yield return test;
#endif
    }

    /// <summary>Loads the list from <paramref name="folder"/>. Returns null when there is no list.</summary>
    internal static (LocalThreatList? Engine, ThreatListStatus Status) Load(string folder, IEnumerable<string> trustedKeys, DateTimeOffset now, TimeProvider? clock = null)
    {
        string path = Path.Combine(folder, FileName);
        TimeProvider time = clock ?? TimeProvider.System;
        LocalThreatList Broken(string reason) => new([], [], [], reason, null, time);
        try
        {
            byte[] bytes;
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length > MaxListBytes) return Fail("목록이 너무 큽니다");
                bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1) return Fail("목록 크기가 변경되었습니다");
            }
            catch (FileNotFoundException) { return (null, ThreatListStatus.None); }
            catch (DirectoryNotFoundException) { return (null, ThreatListStatus.None); }
            string sigPath = path + ".sig";
            if (!File.Exists(sigPath)) return Fail("서명 없음");
            byte[] signature = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());
            if (!trustedKeys.Any(key => Verifies(key, bytes, signature))) return Fail("신뢰할 수 없는 서명");
            var document = JsonSerializer.Deserialize<ListDocument>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("empty");
            if (document.Schema != 1) return Fail("지원하지 않는 형식");
            if (document.Expires is not { } expires || expires <= now) return Fail("만료된 목록");
            if (document.Issued is not { } issued || issued > now.AddDays(1) || issued >= expires) return Fail("발급일이 올바르지 않음");
            var hostSet = Normalize(document.Hosts, NormalizeHost);
            var urlSet = Normalize(document.Urls, Hex);
            var fileSet = Normalize(document.Files, Hex);
            int count = hostSet.Count + urlSet.Count + fileSet.Count;
            return (new LocalThreatList(hostSet, urlSet, fileSet, null, expires, time), new ThreatListStatus(true, true, $"{count}개 항목", count, expires));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException)
        {
            return Fail("읽을 수 없음");
        }

        (LocalThreatList?, ThreatListStatus) Fail(string reason) => (Broken(reason), new ThreatListStatus(true, false, reason));
    }

    private static bool Verifies(string spki, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception error) when (error is CryptographicException or FormatException) { return false; }
    }

    private static HashSet<string> Normalize(string[]? values, Func<string, string?> normalize)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? value in values ?? [])
        {
            if (value is null || normalize(value) is not { } normalized)
                throw new JsonException("Invalid list entry.");
            result.Add(normalized);
        }
        return result;
    }

    private static string? NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        return host.Length is > 0 and < 254 && !host.Contains('/') && !host.Contains(' ') ? host : null;
    }

    private static string? Hex(string value)
    {
        value = value.Trim().ToLowerInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    /// <summary>SHA-256 of the address without fragment, as stored in the list's <c>urls</c>.</summary>
    internal static string UrlKey(Uri uri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)))).ToLowerInvariant();

    public ValueTask<ThreatResult> CheckAsync(Uri address, CancellationToken cancel) => new(Check(address));

    public ValueTask<ThreatResult> CheckFileAsync(string sha256, CancellationToken cancel) => new(
        CurrentProblem is { } invalid ? new ThreatResult(ThreatVerdict.Error, Name, invalid)
        : files.Contains(sha256.ToLowerInvariant()) ? new ThreatResult(ThreatVerdict.Malicious, Name, "파일이 VEIL 위협 목록에 있습니다")
        : new ThreatResult(ThreatVerdict.Unknown, Name, "목록에 없음"));

    private ThreatResult Check(Uri address)
    {
        if (CurrentProblem is { } invalid) return new ThreatResult(ThreatVerdict.Error, Name, invalid);
        if (address.Scheme is not ("http" or "https")) return new ThreatResult(ThreatVerdict.Unknown, Name, "웹 주소 아님");
        string host = address.IdnHost.TrimEnd('.').ToLowerInvariant();
        // The host itself or any parent domain listed blocks it (bad.example also covers a.bad.example).
        for (string? candidate = host; candidate is not null; candidate = Parent(candidate))
            if (hosts.Contains(candidate)) return new ThreatResult(ThreatVerdict.Malicious, Name, "사이트가 VEIL 위협 목록에 있습니다");
        if (urls.Contains(UrlKey(address))) return new ThreatResult(ThreatVerdict.Malicious, Name, "주소가 VEIL 위협 목록에 있습니다");
        // A local list only knows what it lists: "not listed" is Unknown, never a statement that the site is safe.
        return new ThreatResult(ThreatVerdict.Unknown, Name, "목록에 없음");
    }

    private static string? Parent(string host)
    {
        int dot = host.IndexOf('.');
        return dot < 0 || host.IndexOf('.', dot + 1) < 0 ? null : host[(dot + 1)..];
    }

    private sealed record ListDocument(int Schema, DateTimeOffset? Issued, DateTimeOffset? Expires, string[]? Hosts, string[]? Urls, string[]? Files);
}
