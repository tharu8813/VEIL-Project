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
