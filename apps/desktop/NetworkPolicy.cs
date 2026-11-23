using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Veil.Desktop;

internal enum NetworkMode { Standard, Proxy }

/// <summary>Saved network choice (ADR-0006). Applied when the engine starts, i.e. after a restart.</summary>
internal sealed record NetworkSettings(NetworkMode Mode = NetworkMode.Standard, string? ProxyScheme = null, string? ProxyHost = null, int ProxyPort = 0)
{
    internal static readonly NetworkSettings Standard = new();

    /// <summary>Null when valid. A proxy setting that cannot be used must never degrade to direct.</summary>
    internal string? Problem =>
        Mode == NetworkMode.Standard ? null :
        ProxyScheme is not ("http" or "socks5") ? "프록시 종류는 HTTP 또는 SOCKS5여야 합니다." :
        string.IsNullOrWhiteSpace(ProxyHost) || ProxyHost.Any(c => char.IsWhiteSpace(c) || c is '/' or '@' or '?' or '#') ? "프록시 주소가 올바르지 않습니다." :
        ProxyPort is < 1 or > 65535 ? "프록시 포트는 1~65535여야 합니다." : null;

    internal Uri? ProxyUri => Mode == NetworkMode.Proxy && Problem is null
        ? new Uri($"{ProxyScheme}://{(ProxyHost!.Contains(':') && !ProxyHost.StartsWith('[') ? $"[{ProxyHost}]" : ProxyHost)}:{ProxyPort}")
        : null;

    internal string Describe() => Mode == NetworkMode.Standard ? "표준 보호" : $"프록시 보호 ({ProxyScheme}://{ProxyHost}:{ProxyPort})";
}

internal static class NetworkPolicy
{
    /// <summary>Engine switches for the mode. Proxy mode: fixed proxy without DIRECT fallback (loopback included),
    /// WebRTC limited to proxied paths, QUIC off.</summary>
    internal static IEnumerable<string> BrowserArguments(NetworkSettings settings)
    {
        if (settings.Mode != NetworkMode.Proxy) yield break;
        var proxy = settings.ProxyUri ?? throw new InvalidOperationException(settings.Problem);
        yield return $"--proxy-server={proxy.Scheme}://{proxy.Authority}";
        yield return "--proxy-bypass-list=<-loopback>";
        yield return "--force-webrtc-ip-handling-policy=disable_non_proxied_udp";
        yield return "--disable-quic";
    }

    /// <summary>HTTPS-first: plain-HTTP navigations to public hosts are opened as HTTPS, unless the user
    /// explicitly allowed HTTP for that host in this run. Local and private destinations are left alone.</summary>
    internal static Uri? HttpsUpgrade(Uri uri, IReadOnlySet<string> httpAllowed)
    {
        if (uri.Scheme != Uri.UriSchemeHttp || IsLocal(uri.Host) || httpAllowed.Contains(uri.IdnHost)) return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
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
    internal static SocketsHttpHandler Handler(NetworkSettings settings) => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseProxy = settings.Mode == NetworkMode.Proxy,
        Proxy = settings.Mode == NetworkMode.Proxy
            ? new WebProxy(settings.ProxyUri ?? throw new InvalidOperationException(settings.Problem)) { BypassProxyOnLocal = false, BypassList = [] }
            : null,
    };

    /// <summary>Path check shown in the UI: can the proxy be reached at all? Blocking does not depend on it.</summary>
    internal static async Task<bool> ProxyReachableAsync(NetworkSettings settings, TimeSpan timeout)
    {
        if (settings.ProxyUri is not { } proxy) return false;
        using var client = new TcpClient();
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(proxy.IdnHost, proxy.Port, cancel.Token);
            return true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
    }
}
