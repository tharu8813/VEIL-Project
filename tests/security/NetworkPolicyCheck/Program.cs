using System.Net;
using System.Net.Sockets;
using System.Text;
using Veil.Desktop;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var origin = new TcpListener(IPAddress.Loopback, 0);
using var proxy = new TcpListener(IPAddress.Loopback, 0);
origin.Start();
proxy.Start();
int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
var target = new Uri($"http://127.0.0.1:{originPort}/synthetic");
var settings = new NetworkSettings(NetworkMode.Proxy, "http", "127.0.0.1", proxyPort);
using var handler = NetworkPolicy.Handler(settings);
foreach (var destination in new[] { target, new Uri("http://localhost/"), new Uri("http://[::1]/"), new Uri("https://example.invalid/") })
{
    Check(handler.Proxy?.IsBypassed(destination) == false, "proxy bypass");
    Check(handler.Proxy?.GetProxy(destination) == settings.ProxyUri, "wrong proxy");
}
using var http = new HttpClient(handler);
var pending = http.GetStringAsync(target, deadline.Token);
using (var peer = await proxy.AcceptTcpClientAsync(deadline.Token))
{
    using var stream = peer.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    string? request = await reader.ReadLineAsync(deadline.Token);
    Check(request == $"GET {target.AbsoluteUri} HTTP/1.1", "request not routed to configured proxy");
    while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nproxy"u8.ToArray(), deadline.Token);
    await stream.FlushAsync(deadline.Token);
}
Check(await pending == "proxy", "unexpected proxy response");
Check(!origin.Pending(), "direct connection reached origin");

// Keep the origin listening; a fallback would be visible even if it times out.
proxy.Stop();
using var failing = new HttpClient(NetworkPolicy.Handler(settings));
bool refused = false;
try { _ = await failing.GetStringAsync(target, deadline.Token); }
catch (HttpRequestException) { refused = true; }
Check(refused, "unavailable proxy did not fail");
Check(!origin.Pending(), "direct fallback reached origin");
Console.WriteLine("PASS: fixed HTTP proxy routes loopback and refuses fallback");


foreach (string template in new[] {
    "https://resolver.example/dns-query", "https://resolver.example/dns-query{?dns}",
    "https://127.0.0.1:8443/dns-query", "https://[::1]:8443/dns-query",
    "https://resolver.example/dns-query?profile=synthetic" })
    Check(NetworkPolicy.ValidDnsTemplate(template), "valid DoH endpoint refused");
foreach (string template in new[] {
    "", "http://resolver.example/dns-query", "https://user:pass@resolver.example/dns-query",
    "https://resolver.example/dns-query#fragment", "https://resolver.example/dns-query{?dns}extra",
    "https://resolver.example/dns-query{?dns}{?dns}", "https://resolver.example/{other}",
    "https://resolver.example/dns-query?x=1{?dns}", "https://resolver.example\\dns-query",
    "https://resolver.example/dns-query\n", "https://resolver.example:0/dns-query",
    "https://resolver.example:65536/dns-query", "https://one.example https://two.example" })
    Check(!NetworkPolicy.ValidDnsTemplate(template), "invalid DoH endpoint accepted");

string lab = Path.Combine(Path.GetTempPath(), "veil-dns-policy-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(lab);
try
{
    string engine = Path.Combine(lab, "EBWebView");
    Directory.CreateDirectory(engine);
    string state = Path.Combine(engine, "Local State");
    string stale = state + ".veil-new";
    File.WriteAllText(stale, "unowned");
    File.WriteAllText(state, "{\"unrelated\":{\"value\":17},\"dns_over_https\":{\"mode\":\"automatic\"}}");
    var secure = new NetworkSettings(DnsTemplate: "https://resolver.example/dns-query{?dns}");
    NetworkPolicy.ApplyDns(lab, secure);
    var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(state))!;
    Check(json["unrelated"]!["value"]!.GetValue<int>() == 17, "unrelated engine state changed");
    Check(json["dns_over_https"]!["mode"]!.GetValue<string>() == "secure" &&
        json["dns_over_https"]!["templates"]!.GetValue<string>() == secure.DnsTemplate, "secure DNS state mismatch");
    string original = File.ReadAllText(state);
    if (OperatingSystem.IsWindows())
    {
        using (File.Open(state, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool blocked = false;
            try { NetworkPolicy.ApplyDns(lab, NetworkSettings.Standard); }
            catch (IOException) { blocked = true; }
            catch (UnauthorizedAccessException) { blocked = true; }
            Check(blocked, "locked engine state was replaced");
        }
        Check(File.ReadAllText(state) == original, "failed replacement changed engine state");
    }
    Check(!Directory.EnumerateFiles(engine, "Local State.veil-new-*").Any(), "owned staging not cleaned");
    NetworkPolicy.ApplyDns(lab, NetworkSettings.Standard);
    json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(state))!;
    Check(json["dns_over_https"] is null && json["unrelated"]!["value"]!.GetValue<int>() == 17, "system DNS reset lost unrelated settings");
    Check(File.ReadAllText(stale) == "unowned", "unowned staging was overwritten");
    File.WriteAllText(state, "{broken");
    foreach (var mode in new[] { secure, NetworkSettings.Standard })
    {
        bool refusedState = false;
        try { NetworkPolicy.ApplyDns(lab, mode); }
        catch (IOException) { refusedState = true; }
        Check(refusedState && File.ReadAllText(state) == "{broken", "malformed Local State was ignored or overwritten");
    }
}
finally { Directory.Delete(lab, recursive: true); }
Console.WriteLine("PASS: DoH template validation and engine state publication");


// The socket guard must fail before dialing any endpoint other than the configured proxy.
using (var guardDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    bool directRefused = false;
    try
    {
        using var stream = await NetworkPolicy.ConnectProxyAsync(settings.ProxyUri!,
            new DnsEndPoint("127.0.0.1", originPort), guardDeadline.Token);
    }
    catch (HttpRequestException) { directRefused = true; }
    Check(directRefused && !origin.Pending(), "TCP guard did not refuse a direct destination");
}

// A real SOCKS5 negotiation on a synthetic loopback proxy. No public origin is contacted.
using var socksDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var socks = new TcpListener(IPAddress.Loopback, 0);
socks.Start();
int socksPort = ((IPEndPoint)socks.LocalEndpoint).Port;
var socksSettings = new NetworkSettings(NetworkMode.Proxy, "socks5", "127.0.0.1", socksPort);
using var socksHttp = new HttpClient(NetworkPolicy.Handler(socksSettings));
const string syntheticHost = "veil-socks-dns.invalid";
var socksTarget = new Uri($"http://{syntheticHost}:{originPort}/synthetic");
var socksPending = socksHttp.GetStringAsync(socksTarget, socksDeadline.Token);
using (var peer = await socks.AcceptTcpClientAsync(socksDeadline.Token))
{
    using var stream = peer.GetStream();
    byte[] hello = new byte[2];
    await stream.ReadExactlyAsync(hello, socksDeadline.Token);
    Check(hello[0] == 5 && hello[1] > 0, "invalid SOCKS greeting");
    byte[] methods = new byte[hello[1]];
    await stream.ReadExactlyAsync(methods, socksDeadline.Token);
    Check(methods.SequenceEqual(new byte[] { 0 }), "unexpected SOCKS authentication method");
    await stream.WriteAsync(new byte[] { 5, 0 }, socksDeadline.Token);
    byte[] request = new byte[5];
    await stream.ReadExactlyAsync(request, socksDeadline.Token);
    Check(request[0] == 5 && request[1] == 1 && request[2] == 0 && request[3] == 3 && request[4] > 0,
        "SOCKS destination was not forwarded as a domain name");
    byte[] name = new byte[request[4]];
    byte[] port = new byte[2];
    await stream.ReadExactlyAsync(name, socksDeadline.Token);
    await stream.ReadExactlyAsync(port, socksDeadline.Token);
    Check(Encoding.ASCII.GetString(name) == syntheticHost && (port[0] << 8 | port[1]) == originPort,
        "SOCKS destination authority changed");
    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, socksDeadline.Token);
    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    Check(await reader.ReadLineAsync(socksDeadline.Token) == "GET /synthetic HTTP/1.1", "invalid tunneled HTTP request");
    while (await reader.ReadLineAsync(socksDeadline.Token) is { Length: > 0 }) { }
    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nsocks"u8.ToArray(), socksDeadline.Token);
    await stream.FlushAsync(socksDeadline.Token);
}
Check(await socksPending == "socks" && !origin.Pending(), "SOCKS request did not remain on proxy");
socks.Stop();
using var socksFailing = new HttpClient(NetworkPolicy.Handler(socksSettings));
bool socksRefused = false;
try { _ = await socksFailing.GetStringAsync(target, socksDeadline.Token); }
catch (HttpRequestException) { socksRefused = true; }
Check(socksRefused && !origin.Pending(), "SOCKS proxy outage allowed direct fallback");
Console.WriteLine("PASS: proxy socket boundary and SOCKS5 remote hostname forwarding");


// Exercise IPv6 loopback independently of the IPv4 proxy test; no external route is needed.
if (Socket.OSSupportsIPv6)
{
    using var ipv6Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var ipv6Origin = new TcpListener(IPAddress.IPv6Loopback, 0);
    using var ipv6Proxy = new TcpListener(IPAddress.IPv6Loopback, 0);
    ipv6Origin.Start();
    ipv6Proxy.Start();
    int destinationPort = ((IPEndPoint)ipv6Origin.LocalEndpoint).Port;
    int proxy6Port = ((IPEndPoint)ipv6Proxy.LocalEndpoint).Port;
    var target6 = new Uri($"http://[::1]:{destinationPort}/synthetic-v6");
    var settings6 = new NetworkSettings(NetworkMode.Proxy, "http", "::1", proxy6Port);
    using var http6 = new HttpClient(NetworkPolicy.Handler(settings6));
    var pending6 = http6.GetStringAsync(target6, ipv6Deadline.Token);
    using (var peer = await ipv6Proxy.AcceptTcpClientAsync(ipv6Deadline.Token))
    {
        using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Check(await reader.ReadLineAsync(ipv6Deadline.Token) == $"GET {target6.AbsoluteUri} HTTP/1.1",
            "IPv6 request did not use configured proxy");
        while (await reader.ReadLineAsync(ipv6Deadline.Token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nipv6!"u8.ToArray(), ipv6Deadline.Token);
    }
    Check(await pending6 == "ipv6!" && !ipv6Origin.Pending(), "IPv6 proxy response or direct route mismatch");
    ipv6Proxy.Stop();
    using var failing6 = new HttpClient(NetworkPolicy.Handler(settings6));
    bool refused6 = false;
    try { _ = await failing6.GetStringAsync(target6, ipv6Deadline.Token); }
    catch (HttpRequestException) { refused6 = true; }
    Check(refused6 && !ipv6Origin.Pending(), "IPv6 proxy failure allowed direct fallback");
    Console.WriteLine("PASS: IPv6 loopback proxy routing and outage refusal");
}
else
{
    Console.WriteLine("NOT RUN: IPv6 unavailable on this host");
}


// Vault re-fetches cannot silently downgrade HTTPS or widen a user's host exception.
var approvedHttp = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com", "other.example.com" };
var publicHttp = new Uri("http://example.com/file");
var publicHttps = new Uri("https://example.com/file");
var localHttp = new Uri("http://127.0.0.1/file");
Check(!NetworkPolicy.DownloadAddressAllowed(publicHttp, null, null), "public HTTP requires explicit exception");
Check(NetworkPolicy.DownloadAddressAllowed(publicHttp, null, approvedHttp), "explicit HTTP exception ignored");
Check(NetworkPolicy.DownloadAddressAllowed(publicHttp, new Uri("http://example.com/start"), approvedHttp), "same-host HTTP redirect refused");
Check(!NetworkPolicy.DownloadAddressAllowed(new Uri("http://other.example.com/file"), publicHttp, approvedHttp), "HTTP exception widened on redirect");
Check(!NetworkPolicy.DownloadAddressAllowed(publicHttp, publicHttps, approvedHttp), "public HTTPS downgrade accepted");
Check(!NetworkPolicy.DownloadAddressAllowed(localHttp, publicHttps, approvedHttp), "HTTPS downgrade to local HTTP accepted");
Check(NetworkPolicy.DownloadAddressAllowed(localHttp, null, null), "local HTTP fixture refused");
Check(NetworkPolicy.DownloadAddressAllowed(publicHttps, publicHttp, null), "HTTP to HTTPS upgrade refused");
Check(!NetworkPolicy.DownloadAddressAllowed(new Uri("https://user:secret@example.com/file"), null, null), "URL credentials accepted");
Check(!NetworkPolicy.DownloadAddressAllowed(new Uri("file:///synthetic"), null, null), "non-HTTP address accepted");
Console.WriteLine("PASS: vault HTTP exceptions and downgrade refusal");


// A TCP-only status check is observational, independent of actual proxy routing.
using var statusListener = new TcpListener(IPAddress.Loopback, 0);
statusListener.Start();
var statusSettings = new NetworkSettings(NetworkMode.Proxy, "http", "127.0.0.1",
    ((IPEndPoint)statusListener.LocalEndpoint).Port);
Check(await NetworkPolicy.ProxyReachableAsync(statusSettings, TimeSpan.FromSeconds(3)), "TCP status did not detect live listener");
statusListener.Stop();
Check(!await NetworkPolicy.ProxyReachableAsync(statusSettings, TimeSpan.FromSeconds(3)), "TCP status did not detect stopped listener");
statusListener.Start();
statusSettings = statusSettings with { ProxyPort = ((IPEndPoint)statusListener.LocalEndpoint).Port };
Check(await NetworkPolicy.ProxyReachableAsync(statusSettings, TimeSpan.FromSeconds(3)), "TCP status did not detect restarted listener");
using var stoppedProbe = new CancellationTokenSource();
stoppedProbe.Cancel();
Check(!await NetworkPolicy.ProxyReachableAsync(statusSettings, TimeSpan.FromSeconds(3), stoppedProbe.Token), "cancelled TCP probe succeeded");
var statusTime = new DateTimeOffset(2026, 11, 25, 12, 34, 56, TimeSpan.Zero);
Check(NetworkPolicy.ProxyStatusText(null, null).Contains("미확인"), "unmeasured status claimed a result");
Check(NetworkPolicy.ProxyStatusText(true, statusTime).Contains("12:34:56") &&
    NetworkPolicy.ProxyStatusText(true, statusTime).Contains("전체 보호 미검증"), "TCP result overclaims protection");
Check(NetworkPolicy.ProxyStatusText(false, statusTime).Contains("TCP 연결 실패"), "failed status mislabeled");
Console.WriteLine("PASS: TCP status probe and honest status presentation");
