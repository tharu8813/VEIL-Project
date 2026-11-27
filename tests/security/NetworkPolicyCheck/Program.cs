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
using var recoveredListener = new TcpListener(IPAddress.Loopback, statusSettings.ProxyPort);
recoveredListener.Start();
Check(await NetworkPolicy.ProxyReachableAsync(statusSettings, TimeSpan.FromSeconds(3)), "TCP status did not detect recovery at the same endpoint");
using var stoppedProbe = new CancellationTokenSource();
stoppedProbe.Cancel();
Check(!await NetworkPolicy.ProxyReachableAsync(statusSettings, TimeSpan.FromSeconds(3), stoppedProbe.Token), "cancelled TCP probe succeeded");
var statusTime = new DateTimeOffset(2026, 11, 25, 12, 34, 56, TimeSpan.Zero);
Check(NetworkPolicy.ProxyStatusText(null, null).Contains("미확인"), "unmeasured status claimed a result");
Check(NetworkPolicy.ProxyStatusText(true, statusTime, statusTime.AddSeconds(10)).Contains("12:34:56") &&
    NetworkPolicy.ProxyStatusText(true, statusTime, statusTime.AddSeconds(10)).Contains("전체 보호 미검증"), "TCP result overclaims protection");
Check(NetworkPolicy.ProxyStatusText(false, statusTime, statusTime).Contains("TCP 연결 실패"), "failed status mislabeled");
Check(NetworkPolicy.ProxyStatusText(true, statusTime, statusTime.AddSeconds(31)).Contains("미확인"), "stale successful probe still advertised");
Check(NetworkPolicy.ProxyStatusText(false, statusTime, statusTime.AddSeconds(31)).Contains("미확인"), "stale failure still advertised");
Check(NetworkPolicy.ProxyStatusText(true, statusTime, statusTime.AddSeconds(-1)).Contains("미확인"), "clock rollback accepted old probe");
Console.WriteLine("PASS: TCP status probe and honest status presentation");


// Even if an invalid URI reaches the TCP guard, two invalid canonical names cannot compare equal.
bool invalidProxyRefused = false;
try
{
    using var invalidStream = await NetworkPolicy.ConnectProxyAsync(new Uri("relative", UriKind.Relative),
        new DnsEndPoint("127.0.0.1", originPort), CancellationToken.None);
}
catch (HttpRequestException) { invalidProxyRefused = true; }
Check(invalidProxyRefused && !origin.Pending(), "invalid proxy identity did not fail closed");


// Address-like mistakes are rejected rather than sent to the default search provider.
foreach (string rejected in new[] {
    "https://user:secret@example.test/", "user@example.test", "example.test:invalid",
    "http:/example.test", "https://", "https://example.test/a b", "file:///C:/private",
    @"C:\private notes\file.txt", "/home/user/private notes", "//example.test",
    "mailto:user@example.test notes", "https://example.test/\nsecret", "example.test\u202E",
})
    Check(AddressInputPolicy.TryResolve(rejected, out var address) && address is null, $"address was not rejected: {rejected}");
foreach (var (input, expected) in new[] {
    ("example.test", "https://example.test/"),
    ("https://example.test/a?b=1", "https://example.test/a?b=1"),
    ("LOCALHOST:8765", "http://localhost:8765/"),
    ("127.0.0.1:8765", "http://127.0.0.1:8765/"),
    ("[::1]:8765", "http://[::1]:8765/"),
})
    Check(AddressInputPolicy.TryResolve(input, out var address) && address?.AbsoluteUri == expected, $"valid address changed: {input}");
foreach (string query in new[] { "한글 브라우저", "reference guide", "why: test", "example.test guide", "browser" })
    Check(!AddressInputPolicy.TryResolve(query, out _), $"ordinary search refused: {query}");
Console.WriteLine("PASS: address routing and rejected URL isolation");


using var progressDownload = new BrowserDownload("synthetic");
int updates = 0;
progressDownload.PropertyChanged += (_, _) => updates++;
Check(progressDownload.IsIndeterminate, "queued transfer must be indeterminate");
progressDownload.WaitForTransfer();
progressDownload.Report(50, 100);
Check(!progressDownload.IsIndeterminate && progressDownload.Percent == 50, "known body progress incorrect");
int beforeComplete = updates;
progressDownload.Report(100, 100);
Check(progressDownload.IsIndeterminate && updates > beforeComplete && progressDownload.StatusText.Contains("수신 완료"),
    "body completion did not immediately switch to authentication state");
progressDownload.Finish();
Check(progressDownload.IsIndeterminate && !progressDownload.CanCancel && progressDownload.Phase == DownloadPhase.Publishing,
    "publishing should not advertise complete file or allow cancellation");
progressDownload.Report(10, 100);
Check(progressDownload.Bytes == 100 && progressDownload.Phase == DownloadPhase.Publishing, "late progress changed publication state");
using var cancelledDownload = new BrowserDownload("synthetic-cancel");
cancelledDownload.Report(1, 10);
cancelledDownload.Cancel();
cancelledDownload.Report(9, 10);
cancelledDownload.WaitForVault();
Check(cancelledDownload.IsCancelling && cancelledDownload.Bytes == 1 && cancelledDownload.StatusText == "취소하는 중" &&
    cancelledDownload.IsIndeterminate, "late report changed cancellation state");
bool cancelledFinish = false;
try { cancelledDownload.Finish(); }
catch (OperationCanceledException) { cancelledFinish = true; }
Check(cancelledFinish, "cancelled transfer crossed publication boundary");
Console.WriteLine("PASS: download progress, authentication, publication and cancellation states");


foreach (var selection in new SiteDataSelection[] {
    new("https://example.test", null, false, false, false),
    new("https://example.test:8443", null, false, false, false, true),
    new(null, ".example.test", false, false, false),
    new(null, null, true, true, false),
})
    Check(SiteDataPolicy.ValidSelection(selection), "valid site-data scope refused");
foreach (var selection in new SiteDataSelection[] {
    new("https://example.test", ".example.test", false, false, false),
    new("https://example.test", null, true, false, false),
    new(null, ".example.test", false, true, false),
    new(null, ".example.test", false, false, false, true),
    new(null, null, false, false, false),
    new(null, null, true, false, false, true),
    new("https://example.test/path", null, false, false, false),
    new(null, "bad/domain", false, false, false),
})
    Check(!SiteDataPolicy.ValidSelection(selection), "ambiguous site-data scope accepted");
Console.WriteLine("PASS: exclusive site-data deletion and inspection scopes");

var productSupport = SiteDataCapabilities.StandardWebView2;
var fixtureSupport = SiteDataCapabilities.CefTabFixture;
var absentSupport = SiteDataCapabilities.Unavailable;
Check(productSupport.Scope == SiteDataScope.Product &&
    fixtureSupport.Scope == SiteDataScope.SyntheticFixture, "fixture advertised as product");
for (int flags = 0; flags <= 15; flags++)
{
    var kinds = (EngineProfileDataKinds)flags;
    Check(productSupport.SupportsProfile(kinds) == (flags >= 1 && flags <= 7),
        "WebView2 profile flags mismatch");
    Check(fixtureSupport.SupportsProfile(kinds) == (flags is 1 or 4),
        "CEF fixture accepted unsupported or mixed flags");
    Check(!absentSupport.SupportsProfile(kinds), "unavailable profile capability accepted");
}
Check(!productSupport.SupportsProfile((EngineProfileDataKinds)(-1)), "unknown negative flags accepted");
Check(fixtureSupport.Supports(new("http://localhost:1234", null, false, false, false)),
    "fixture origin operation not described");
Check(!fixtureSupport.Supports(new(null, null, false, true, false)),
    "fixture falsely advertised all DOM storage");
Check(!fixtureSupport.Supports(new(null, null, true, false, true)),
    "fixture accepted mixed profile scope");
Check(!absentSupport.Supports(new("https://example.test", null, false, false, false, true)),
    "unavailable engine accepted inspection");
Check(!productSupport.Supports(new("https://example.test/path", null, false, false, false)),
    "capability bypassed canonical origin");
Check(!(productSupport with { ReadCookieDomains = false }).Supports(new(null, ".example.test", false, false, false)),
    "cookie deletion accepted without metadata");
Console.WriteLine("PASS: engine site-data capabilities and supported flag combinations");


// A failed diagnostic sink must not interrupt a lock/unlock operation's caller.
var previousOutput = Console.Out;
string logRoot = Path.Combine(Path.GetTempPath(), "veil-log-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(logRoot);
try
{
    using var capture = new StringWriter();
    Console.SetOut(capture);
    new Veil.VolumeBroker.BrokerLog(null, console: false).Write("service-test");
    Check(capture.ToString().Length == 0, "production service unexpectedly wrote diagnostics");
    new Veil.VolumeBroker.BrokerLog(Path.Combine(logRoot, "missing", "log.jsonl")).Write("file-error", new { code = "synthetic" });
    Check(capture.ToString().Contains("file-error"), "file sink failure blocked console diagnostics");

    using var closedOutput = new StringWriter();
    closedOutput.Dispose();
    Console.SetOut(closedOutput);
    string file = Path.Combine(logRoot, "events.jsonl");
    new Veil.VolumeBroker.BrokerLog(file).Write("console-error", new { code = "synthetic" });
    Check(File.ReadAllText(file).Contains("console-error"), "console failure blocked file diagnostics");
    new Veil.VolumeBroker.BrokerLog(Path.Combine(logRoot, "missing", "log.jsonl")).Write("both-error");
}
finally
{
    Console.SetOut(previousOutput);
    Directory.Delete(logRoot, recursive: true);
}
Console.WriteLine("PASS: broker diagnostic sink failures do not escape and service output is disabled");

// Metadata admission only. Do not dispatch operations or expose a real account.
var loginCapability = new SyntheticLoginCapability();
foreach (var scope in new[] { LoginFormScope.Unavailable, LoginFormScope.SyntheticFixture,
    LoginFormScope.Product, (LoginFormScope)99 })
{
    loginCapability.LoginScope = scope;
    Check(ReferenceEquals(LoginFormAccess.ProductForms(loginCapability, false), loginCapability) ==
        (scope == LoginFormScope.Product), "login capability scope admitted incorrectly");
    Check(LoginFormAccess.ProductForms(loginCapability, true) is null, "private credential capability admitted");
}
loginCapability.LoginScope = LoginFormScope.Product;
Check(ReferenceEquals(LoginFormAccess.ProductForms(loginCapability, false), loginCapability), "product login capability unavailable");
loginCapability.LoginScope = LoginFormScope.Unavailable;
Check(LoginFormAccess.ProductForms(loginCapability, false) is null, "revoked capability remained admitted");
Check(LoginFormAccess.ProductForms(new object(), false) is null &&
    LoginFormAccess.ProductForms(null, false) is null, "missing login interface admitted");
Console.WriteLine("PASS: product-only login scope admission and revocation");

// Pure layout test: no profile directory or broker is touched.
string layoutRoot = Path.Combine(Path.GetTempPath(), "veil-cef-layout-" + Guid.NewGuid().ToString("N"));
var cefLayout = new CefProfileLayout(layoutRoot + Path.DirectorySeparatorChar);
Check(cefLayout.ProductRoot == layoutRoot && cefLayout.EngineRoot == Path.Combine(layoutRoot, "cef"),
    "CEF layout failed to normalize root");
Check(cefLayout.Cache == Path.Combine(layoutRoot, "cef", "cache") &&
    cefLayout.Scratch == Path.Combine(layoutRoot, "cef", "temp") &&
    cefLayout.Logs == Path.Combine(layoutRoot, "cef", "logs") &&
    cefLayout.LogFile == Path.Combine(cefLayout.Logs, "cef.log"), "unexpected CEF layout");
Check(cefLayout.Directories.Distinct().Count() == 4 &&
    cefLayout.Directories.All(path => path.StartsWith(cefLayout.EngineRoot, StringComparison.Ordinal)),
    "CEF directories overlap or escape engine root");
var steps = cefLayout.PreparationSteps.ToArray();
Check(steps.SequenceEqual(new (string Parent, string Child)[] {
    (cefLayout.ProductRoot, cefLayout.EngineRoot),
    (cefLayout.EngineRoot, cefLayout.Cache),
    (cefLayout.EngineRoot, cefLayout.Scratch),
    (cefLayout.EngineRoot, cefLayout.Logs)
}), "CEF preparation order or immediate parent mismatch");
Check(steps.Select(step => step.Child).SequenceEqual(cefLayout.Directories),
    "CEF preparation steps do not cover exactly the expected directories");
Check(!Directory.Exists(layoutRoot), "layout planning created a profile");
bool relativeRejected = false;
try { _ = new CefProfileLayout("relative-profile"); }
catch (ArgumentException) { relativeRejected = true; }
Check(relativeRejected, "relative CEF root accepted");
Console.WriteLine("PASS: separate CEF profile layout without filesystem side effects");

// Child environment planning is pure; no process or real profile is started.
var inheritedCefEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal) {
    ["TEMP"] = "old-temp", ["temp"] = "old-lower-temp", ["TmP"] = "old-tmp",
    ["veil_cef_wpf_test_url"] = "fixture-url", ["VEIL_CEF_WPF_TABS"] = "1",
    ["VEIL_CEF_PROTECTED_ROOT"] = "old-root", ["Veil_Cef_Protected_Log_File"] = "old-log",
    ["chrome_log_file"] = "outside-log", ["SystemRoot"] = "synthetic-system-root"
};
var childCefEnvironment = CefChildEnvironmentPlan.Build(cefLayout, inheritedCefEnvironment);
Check(childCefEnvironment.Count == 6 &&
    childCefEnvironment["temp"] == cefLayout.Scratch &&
    childCefEnvironment["TMP"] == cefLayout.Scratch &&
    childCefEnvironment[CefChildEnvironmentPlan.RootKey] == cefLayout.EngineRoot &&
    childCefEnvironment[CefChildEnvironmentPlan.CacheKey] == cefLayout.Cache &&
    childCefEnvironment[CefChildEnvironmentPlan.LogKey] == cefLayout.LogFile &&
    childCefEnvironment["SystemRoot"] == "synthetic-system-root",
    "CEF child environment did not replace controlled paths");
Check(!childCefEnvironment.ContainsKey("VEIL_CEF_WPF_TEST_URL") &&
    !childCefEnvironment.ContainsKey("VEIL_CEF_WPF_TABS") &&
    !childCefEnvironment.ContainsKey("CHROME_LOG_FILE"), "CEF fixture/log override inherited");
Check(inheritedCefEnvironment.Count == 9 && inheritedCefEnvironment["TEMP"] == "old-temp" &&
    inheritedCefEnvironment["VEIL_CEF_WPF_TABS"] == "1", "Parent environment input was modified");
bool ambiguousEnvironmentRejected = false;
try {
    _ = CefChildEnvironmentPlan.Build(cefLayout, new Dictionary<string, string?> {
        ["PATH"] = "first", ["Path"] = "second"
    });
}
catch (ArgumentException) { ambiguousEnvironmentRejected = true; }
Check(ambiguousEnvironmentRejected, "Ambiguous unrelated environment accepted");
Check(!Directory.Exists(layoutRoot), "Child environment plan created a profile");
Console.WriteLine("PASS: isolated CEF child environment planning without launch or parent mutation");

// Pure child admission model; no OS process, credential or volume access.
var ownerIdentity = new Veil.VolumeBroker.ClientIdentity(100, 1, "S-1-synthetic", "owner.exe", 1000);
var childIdentity = new Veil.VolumeBroker.ClientIdentity(101, 1, "S-1-synthetic", "child.exe", 2000);
var unlockGeneration = new object();
var admissionClock = new SyntheticAdmissionClock();
Veil.VolumeBroker.CefChildAdmission Grant() => new(ownerIdentity, childIdentity, unlockGeneration, admissionClock);
bool Redeem(Veil.VolumeBroker.CefChildAdmission grant,
    Veil.VolumeBroker.ClientIdentity? observedOwner = null,
    Veil.VolumeBroker.ClientIdentity? observedChild = null,
    object? generation = null, bool ownerAlive = true, bool childAlive = true,
    bool imageVerified = true, bool volumeProtected = true) =>
    grant.TryRedeem(observedOwner ?? ownerIdentity, observedChild ?? childIdentity,
        generation ?? unlockGeneration, ownerAlive, childAlive, imageVerified, volumeProtected);
var admittedGrant = Grant();
Check(Redeem(admittedGrant) && !Redeem(admittedGrant), "CEF admission replay accepted");
foreach (var wrongChild in new[] {
    childIdentity with { ProcessId = 102 }, childIdentity with { CreatedFileTime = 3000 },
    childIdentity with { SessionId = 2 }, childIdentity with { UserSid = "other" },
    childIdentity with { ImagePath = "other.exe" }
})
{
    var denied = Grant();
    Check(!Redeem(denied, observedChild: wrongChild) && !Redeem(denied),
        "wrong CEF child accepted or failed grant reusable");
}
Check(!Redeem(Grant(), observedOwner: ownerIdentity with { CreatedFileTime = 3000 }),
    "reused owner PID admitted");
Check(!Redeem(Grant(), generation: new object()), "changed unlock generation admitted");
Check(!Redeem(Grant(), ownerAlive: false) && !Redeem(Grant(), childAlive: false) &&
    !Redeem(Grant(), imageVerified: false) && !Redeem(Grant(), volumeProtected: false),
    "unverified CEF child admission accepted");
var revoked = Grant();
revoked.Revoke();
Check(!Redeem(revoked), "revoked CEF grant admitted");
var expired = Grant();
admissionClock.Advance(TimeSpan.FromSeconds(15));
Check(!Redeem(expired), "expired CEF grant admitted");
var almostExpired = Grant();
admissionClock.Advance(TimeSpan.FromSeconds(14));
Check(Redeem(almostExpired), "live CEF grant unexpectedly rejected");
foreach (var invalidChild in new[] {
    childIdentity with { ProcessId = 100 }, childIdentity with { SessionId = 2 },
    childIdentity with { UserSid = "other" }, childIdentity with { CreatedFileTime = 999 }
})
{
    bool rejected = false;
    try { _ = new Veil.VolumeBroker.CefChildAdmission(ownerIdentity, invalidChild, unlockGeneration, admissionClock); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "invalid child binding accepted");
}
Console.WriteLine("PASS: CEF child one-shot admission policy, generation, expiry, revocation and identity");

// Pure digest pin tests use synthetic bytes, not real signed executables.
byte[] syntheticImage = [0, 1, 2, 3, 4, 255];
string syntheticDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(syntheticImage));
var imagePin = new Veil.VolumeBroker.CefImageDigest(syntheticDigest.ToLowerInvariant());
using (var bytes = new MemoryStream(syntheticImage))
    Check(imagePin.Matches(bytes), "matching CEF digest rejected");
using (var bytes = new MemoryStream([0, 1, 2, 3, 4, 254]))
    Check(!imagePin.Matches(bytes), "modified CEF image accepted");
using (var bytes = new MemoryStream(syntheticImage))
{
    bytes.Position = 1;
    bool rejected = false;
    try { _ = imagePin.Matches(bytes); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "partial CEF image hash accepted");
}
foreach (string invalidPin in new[] { "", "00", new string('g', 64), new string('0', 65) })
{
    bool rejected = false;
    try { _ = new Veil.VolumeBroker.CefImageDigest(invalidPin); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "invalid CEF digest pin accepted");
}
Console.WriteLine("PASS: CEF executable SHA-256 pin and changed bytes refusal");

// Resource ownership is exercised without real process or file handles.
var retainedPolicy = Grant();
var retainedProcess = new SyntheticRetainedResource();
var retainedImage = new SyntheticRetainedResource();
var retainedRuntime = new SyntheticRetainedResource();
using (var stagedChildResource = new Veil.VolumeBroker.CefPendingChild(retainedPolicy, retainedProcess, retainedImage, retainedRuntime))
{
    Check(!stagedChildResource.IsExpired, "new pending child expired immediately");
    admissionClock.Advance(TimeSpan.FromSeconds(15));
    Check(stagedChildResource.IsExpired, "pending child did not expire");
    stagedChildResource.Dispose();
    stagedChildResource.Dispose();
    Check(retainedProcess.Disposals == 1 && retainedImage.Disposals == 1 && retainedRuntime.Disposals == 1 &&
        !Redeem(retainedPolicy), "pending child disposal failed to retire resources");
}
var throwingProcess = new SyntheticRetainedResource();
var throwingImage = new SyntheticRetainedResource { Throw = true };
var throwingRuntime = new SyntheticRetainedResource { Throw = true };
var throwingPending = new Veil.VolumeBroker.CefPendingChild(Grant(), throwingProcess, throwingImage, throwingRuntime);
try { throwingPending.Dispose(); }
catch (IOException) { }
Check(throwingProcess.Disposals == 1 && throwingImage.Disposals == 1 && throwingRuntime.Disposals == 1,
    "image disposal failure leaked the process resource");
Console.WriteLine("PASS: pending CEF child resource ownership and retirement");

// The scoped slot can be redeemed only by the designated generation.
var redeemProcess = new SyntheticRetainedResource();
var redeemImage = new SyntheticRetainedResource();
using (var redemptionSlot = new Veil.VolumeBroker.CefPendingChild(Grant(), redeemProcess, redeemImage))
{
    Check(!redemptionSlot.MatchesChild(childIdentity with { CreatedFileTime = 3000 }) &&
        redemptionSlot.MatchesChild(childIdentity), "wrong child consumed or matched a pending slot");
    Check(redemptionSlot.TryRedeem(ownerIdentity, childIdentity, unlockGeneration, true, true, true, true),
        "valid pending child redemption rejected");
    Check(!redemptionSlot.MatchesChild(childIdentity) &&
        !redemptionSlot.TryRedeem(ownerIdentity, childIdentity, unlockGeneration, true, true, true, true),
        "pending child redeemed twice");
}
Check(redeemProcess.Disposals == 1 && redeemImage.Disposals == 1, "redeemed slot leaked resources");
Console.WriteLine("PASS: scoped pending child redemption and replay rejection");

file sealed class SyntheticRetainedResource : IDisposable
{
    internal int Disposals { get; private set; }
    internal bool Throw { get; init; }
    public void Dispose()
    {
        Disposals++;
        if (Throw) throw new IOException("synthetic disposal failure");
    }
}

file sealed class SyntheticAdmissionClock : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
}

file sealed class SyntheticLoginCapability : IEngineLoginForms
{
    public LoginFormScope LoginScope { get; set; }
    public Task<bool> ProbeLoginFormAsync(string origin, string page, Func<bool> stillAuthorized) =>
        throw new InvalidOperationException("Admission must not dispatch");
    public Task<SavedLogin?> ReadLoginFormAsync(string origin, string page, Func<bool> stillAuthorized) =>
        throw new InvalidOperationException("Admission must not read");
    public Task<bool> FillLoginFormAsync(SavedLogin login, string page, Func<bool> stillAuthorized) =>
        throw new InvalidOperationException("Admission must not fill");
}
