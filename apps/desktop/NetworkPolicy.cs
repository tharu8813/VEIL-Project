using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Veil.Desktop;

internal enum NetworkMode { Standard, Proxy }

/// <summary>Saved network choice (ADR-0006). Applied when the engine starts, i.e. after a restart.</summary>
internal sealed record NetworkSettings(NetworkMode Mode = NetworkMode.Standard, string? ProxyScheme = null, string? ProxyHost = null, int ProxyPort = 0,
    string? DnsTemplate = null)
{
    internal static readonly NetworkSettings Standard = new();

    /// <summary>Null when valid. A proxy setting that cannot be used must never degrade to direct.</summary>
    internal string? Problem =>
        Mode is not (NetworkMode.Standard or NetworkMode.Proxy) ? "지원하지 않는 네트워크 보호 모드입니다." :
        DnsTemplate is not null && !NetworkPolicy.ValidDnsTemplate(DnsTemplate) ? "보안 DNS 주소는 https:// 로 시작하는 DoH 주소여야 합니다." :
        Mode == NetworkMode.Standard ? null :
        ProxyScheme is not ("http" or "socks5") ? "프록시 종류는 HTTP 또는 SOCKS5여야 합니다." :
        NetworkPolicy.CanonicalProxyHost(ProxyHost) is null ? "프록시 주소는 단일 호스트 이름 또는 IP 주소여야 합니다." :
        ProxyPort is < 1 or > 65535 ? "프록시 포트는 1~65535여야 합니다." : null;

    internal Uri? ProxyUri => Mode == NetworkMode.Proxy && Problem is null
        ? new Uri($"{ProxyScheme}://{NetworkPolicy.CanonicalProxyHost(ProxyHost)}:{ProxyPort}")
        : null;

    internal string Describe() => (Mode == NetworkMode.Standard ? "표준 보호" : $"{NetworkPolicy.ProxyName} ({ProxyScheme}://{ProxyHost}:{ProxyPort})") +
        (DnsTemplate is null ? "" : " · 엔진 보안 DNS 설정");
}

internal static class NetworkPolicy
{
    /// <summary>ADR-0010: proxy protection ships as experimental; every surface that names it uses this label.</summary>
    internal const string ProxyName = "프록시 보호 (실험적)";
    internal const string ProxyCaveat = "실험적 기능이며 고강도 익명성 보호가 아닙니다. OS 수준 차단(방화벽), 절전 후 복귀, 외부 IPv6, VEIL 엔진 밖 프로그램의 연결은 검증되지 않았습니다.";

    // Never pass a proxy list, credentials, URL syntax or command-line delimiters to Chromium.
    // Both the browser and .NET handler consume a single canonical authority.
    internal static string? CanonicalProxyHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253) return null;
        if (host.Contains(':') || host.Contains('[') || host.Contains(']'))
        {
            string literal = host;
            if (host.StartsWith('[') && host.EndsWith(']')) literal = host[1..^1];
            if (literal.Any(c => !char.IsAsciiHexDigit(c) && c is not (':' or '.')) ||
                !IPAddress.TryParse(literal, out var ipv6) || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
                return null;
            return $"[{ipv6}]";
        }
        if (host.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))) return null;
        if (IPAddress.TryParse(host, out var ipv4))
            return ipv4.AddressFamily == AddressFamily.InterNetwork ? ipv4.ToString() : null;
        string dns = host.EndsWith('.') ? host[..^1] : host;
        if (dns.Length == 0 || dns.Split('.').Any(label => label.Length is < 1 or > 63 ||
            label.StartsWith('-') || label.EndsWith('-'))) return null;
        return host.ToLowerInvariant();
    }

    private static void RequireValid(NetworkSettings settings)
    {
        if (settings.Problem is { } problem) throw new InvalidOperationException(problem);
    }

    /// <summary>Well-known DNS-over-HTTPS resolvers offered in the settings dialog.</summary>
    internal static readonly (string Name, string Template)[] DnsProviders =
    [
        ("Cloudflare", "https://cloudflare-dns.com/dns-query"),
        ("Quad9", "https://dns.quad9.net/dns-query"),
        ("Google", "https://dns.google/dns-query"),
    ];

    // Support a single HTTPS endpoint (POST), or the explicit RFC 8484 GET suffix.
    internal static bool ValidDnsTemplate(string template)
    {
        if (string.IsNullOrEmpty(template) || template.Length > 2048 ||
            !template.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            template.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '#'))
            return false;
        bool get = template.EndsWith("{?dns}", StringComparison.Ordinal);
        string endpoint = get ? template[..^6] : template;
        if (endpoint.Contains('{') || endpoint.Contains('}')) return false;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 &&
            uri.UserInfo.Length == 0 && uri.Port is >= 1 and <= 65535 &&
            (!get || !endpoint.Contains('?'));
    }

    /// <summary>
    /// Secure DNS (ADR-0006 DNS step): writes the engine's secure-DNS preferences into its Local State before
    /// the engine starts. "secure" mode never falls back to the system resolver. When DoH is requested and the
    /// file cannot be updated, start is refused rather than resolving names insecurely.
    /// </summary>
    internal static void ApplyDns(string userDataFolder, NetworkSettings settings)
    {
        RequireValid(settings);
        string directory = Path.Combine(userDataFolder, "EBWebView");
        string path = Path.Combine(directory, "Local State");
        JsonObject state;
        try { state = ReadDnsState(path); }
        catch (FileNotFoundException) { state = []; }
        catch (DirectoryNotFoundException) { state = []; }
        if (settings.DnsTemplate is { } template)
            state["dns_over_https"] = new JsonObject { ["mode"] = "secure", ["templates"] = template };
        else if (!state.Remove("dns_over_https"))
            return;

        Directory.CreateDirectory(directory);
        string staging = path + ".veil-new-" + Guid.NewGuid().ToString("N");
        bool owned = false;
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                System.Text.Json.JsonSerializer.Serialize(stream, state);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
            owned = false;
            // Refuse to start if the persisted settings do not match the intended state.
            if (!JsonNode.DeepEquals(ReadDnsState(path), state))
                throw new IOException("Secure DNS state did not persist as requested.");
        }
        finally
        {
            if (owned)
                try { File.Delete(staging); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static JsonObject ReadDnsState(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonNode.Parse(stream) as JsonObject ?? throw new IOException("Local State is not a JSON object.");
        }
        catch (System.Text.Json.JsonException error)
        { throw new IOException("Cannot apply secure DNS to the engine state.", error); }
    }

    /// <summary>Engine switches for the mode. Proxy mode: fixed proxy without DIRECT fallback (loopback included),
    /// WebRTC limited to proxied paths, QUIC off.</summary>
    internal static IEnumerable<string> BrowserArguments(NetworkSettings settings)
    {
        RequireValid(settings);
        if (settings.Mode != NetworkMode.Proxy) yield break;
        var proxy = settings.ProxyUri ?? throw new InvalidOperationException(settings.Problem);
        yield return $"--proxy-server={proxy.Scheme}://{CanonicalProxyHost(settings.ProxyHost)}:{settings.ProxyPort}";
        yield return "--proxy-bypass-list=<-loopback>";
        yield return "--webrtc-ip-handling-policy=disable_non_proxied_udp";
        yield return "--disable-quic";
    }

    /// <summary>HTTPS-first: plain-HTTP navigations to public hosts are opened as HTTPS, unless the user
    /// explicitly allowed HTTP for that host in this run. Local and private destinations are left alone.</summary>
    internal static Uri? HttpsUpgrade(Uri uri, IReadOnlySet<string> httpAllowed)
    {
        if (uri.Scheme != Uri.UriSchemeHttp || IsLocal(uri.Host) || httpAllowed.Contains(uri.IdnHost)) return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
    }

    // A background download cannot display a native HTTPS exception prompt.
    // An existing host exception permits HTTP only initially or along same-host HTTP redirects.
    internal static bool DownloadAddressAllowed(Uri uri, Uri? previous, IReadOnlySet<string>? httpAllowed)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (uri.Scheme != Uri.UriSchemeHttp || previous?.Scheme == Uri.UriSchemeHttps) return false;
        if (IsLocal(uri.Host)) return true;
        return httpAllowed?.Contains(uri.IdnHost) == true &&
            (previous is null || string.Equals(previous.IdnHost, uri.IdnHost, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsLocal(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" || !host.Contains('.') && !host.Contains(':')) return true; // single-label intranet names
        if (host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".test") || host.EndsWith(".internal")) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>HTTP handler for VEIL's own requests (vault re-fetch). Standard: direct, no system proxy.
    /// Proxy mode: only the configured proxy, loopback included; never the system proxy, never direct.</summary>
    internal static SocketsHttpHandler Handler(NetworkSettings settings)
    {
        RequireValid(settings);
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = settings.Mode == NetworkMode.Proxy,
            Proxy = settings.Mode == NetworkMode.Proxy
                ? new FixedProxy(settings.ProxyUri ?? throw new InvalidOperationException(settings.Problem))
                : null,
        };
        if (settings.Mode == NetworkMode.Proxy)
        {
            Uri proxy = settings.ProxyUri!;
            handler.ConnectCallback = (context, cancel) => ConnectProxyAsync(proxy, context.DnsEndPoint, cancel);
        }
        return handler;
    }

    // Defense in depth: even a transport request for a different TCP endpoint must fail.
    // Only the configured proxy host itself may be resolved locally by this path.
    internal static async ValueTask<Stream> ConnectProxyAsync(Uri proxy, DnsEndPoint endpoint, CancellationToken cancel)
    {
        if (endpoint.Port != proxy.Port ||
            !string.Equals(CanonicalProxyHost(endpoint.Host), CanonicalProxyHost(proxy.IdnHost), StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("A direct connection is forbidden in proxy mode.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Do not delegate bypass decisions to platform proxy heuristics (including loopback).
    private sealed class FixedProxy(Uri address) : IWebProxy
    {
        public ICredentials? Credentials { get => null; set { if (value is not null) throw new NotSupportedException("Proxy authentication is not supported."); } }
        public Uri GetProxy(Uri destination) => address;
        public bool IsBypassed(Uri host) => false;
    }

    // A TCP handshake does not prove the proxy protocol or absence of traffic leaks.
    internal static string ProxyStatusText(bool? reachable, DateTimeOffset? checkedAt)
    {
        if (reachable is null || checkedAt is null) return "프록시 TCP 경로 미확인";
        string time = checkedAt.Value.ToString("HH:mm:ss");
        return reachable.Value
            ? $"TCP 경로 확인됨 ({time}) · 전체 보호 미검증"
            : $"TCP 연결 실패 ({time}) · 직접 연결 폴백 없음";
    }

    /// <summary>Path check shown in the UI: can the proxy be reached at all? Blocking does not depend on it.</summary>
    internal static async Task<bool> ProxyReachableAsync(NetworkSettings settings, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (settings.ProxyUri is not { } proxy) return false;
        using var client = new TcpClient();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cancel.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(proxy.IdnHost, proxy.Port, cancel.Token);
            return true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
    }
}
