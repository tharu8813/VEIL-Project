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
