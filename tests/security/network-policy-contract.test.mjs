// SPDX-License-Identifier: MPL-2.0
// Static contract only. Interactive WebView2 and real packet traces are separate tests.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
const policy = await readFile('apps/desktop/NetworkPolicy.cs', 'utf8');
test('unknown network mode and ambiguous host cannot select a direct transport', () => {
    assert.match(policy, /Mode is not \(NetworkMode\.Standard or NetworkMode\.Proxy\) \?/);
    assert.match(policy, /NetworkPolicy\.CanonicalProxyHost\(ProxyHost\) is null \?/);
    assert.match(policy, /if \(host\.Any\(c => !char\.IsAsciiLetterOrDigit\(c\) && c is not \('\.' or '-'\)\)\) return null;/);
    assert.match(policy, /ipv6\.AddressFamily != AddressFamily\.InterNetworkV6/);
    assert.match(policy, /BrowserArguments\(NetworkSettings settings\)\s*\{\s*RequireValid\(settings\);\s*if \(settings\.Mode != NetworkMode\.Proxy\) yield break;/);
    assert.match(policy, /Handler\(NetworkSettings settings\)\s*\{\s*RequireValid\(settings\);/);
    assert.match(policy, /ApplyDns\(string userDataFolder, NetworkSettings settings\)\s*\{\s*RequireValid\(settings\);/);
    assert.match(policy, /--proxy-server=\{proxy\.Scheme\}:\/\/\{CanonicalProxyHost\(settings\.ProxyHost\)\}:\{settings\.ProxyPort\}/);
    assert.match(policy, /new FixedProxy\(settings\.ProxyUri \?\? throw new InvalidOperationException\(settings\.Problem\)\)/);
    assert.match(policy, /private sealed class FixedProxy\(Uri address\) : IWebProxy[\s\S]*?public Uri GetProxy\(Uri destination\) => address;\s*public bool IsBypassed\(Uri host\) => false;/);
    assert.doesNotMatch(policy, /new WebProxy\(/);
});


test('proxy TCP connections are restricted to the configured endpoint', () => {
    assert.match(policy, /handler\.ConnectCallback = \(context, cancel\) => ConnectProxyAsync\(proxy, context\.DnsEndPoint, cancel\);/);
    const connect = policy.slice(policy.indexOf('internal static async ValueTask<Stream> ConnectProxyAsync'), policy.indexOf('private sealed class FixedProxy'));
    assert.match(connect, /if \(endpoint\.Port != proxy\.Port \|\|[\s\S]*?CanonicalProxyHost\(endpoint\.Host\)[\s\S]*?CanonicalProxyHost\(proxy\.IdnHost\)[\s\S]*?throw new HttpRequestException[\s\S]*?await socket\.ConnectAsync\(endpoint, cancel\);/);
    assert.match(connect, /catch\s*\{\s*socket\.Dispose\(\);\s*throw;/);
});


const sessionSource = await readFile('apps/desktop/BrowserSession.cs', 'utf8');
const browserSource = await readFile('apps/desktop/BrowserWindow.xaml.cs', 'utf8');
test('one shared proxy monitor checks periodically and drops cancelled or superseded results', () => {
    assert.match(sessionSource, /internal bool\? ProxyReachable => MainSession\.proxyReachable;/);
    assert.match(sessionSource, /StartProxyMonitorAsync\(\) => MainSession\.StartMainProxyMonitorAsync\(\)/);
    assert.match(sessionSource, /proxyMonitor is not null \|\| IsEnding \|\| EngineLost\) return;/);
    assert.match(sessionSource, /new DispatcherTimer \{ Interval = TimeSpan\.FromSeconds\(15\) \}/);
    assert.match(sessionSource, /proxyChecking \|\| IsEnding \|\| EngineLost\) return;/);
    assert.match(sessionSource, /await NetworkPolicy\.ProxyReachableAsync\(Network, TimeSpan\.FromSeconds\(3\), monitor\.Token\);\s*if \(!ReferenceEquals\(proxyMonitor, monitor\) \|\| IsEnding \|\| EngineLost\) return;/);
    assert.match(sessionSource, /proxyMonitor = null;\s*monitor\?\.Cancel\(\);\s*monitor\?\.Dispose\(\);/);
    assert.match(browserSource, /session\.ReleasePrivate\(\);\s*session\.StopProxyMonitorIfIdle\(\);/);
    assert.match(policy, /전체 보호 미검증/);
});
