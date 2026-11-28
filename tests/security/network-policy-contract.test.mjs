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
    assert.match(connect, /string\? expectedHost = proxy\.IsAbsoluteUri \? CanonicalProxyHost\(proxy\.IdnHost\) : null;[\s\S]*?if \(expectedHost is null \|\| endpoint\.Port != proxy\.Port \|\|[\s\S]*?CanonicalProxyHost\(endpoint\.Host\), expectedHost[\s\S]*?throw new HttpRequestException[\s\S]*?await socket\.ConnectAsync\(endpoint, cancel\);/);
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


const manager = await readFile('apps/desktop/SecurityManager.cs', 'utf8');
const list = await readFile('apps/desktop/LocalThreatList.cs', 'utf8');
test('expired or malformed local threat lists never return a usable verdict', () => {
    assert.match(list, /expiry <= clock\.GetUtcNow\(\) \? "만료된 목록"/);
    assert.match(list, /CheckFileAsync[\s\S]*?CurrentProblem is \{ \} invalid \? new ThreatResult\(ThreatVerdict\.Error/);
    assert.match(list, /private ThreatResult Check\(Uri address\)\s*\{\s*if \(CurrentProblem is \{ \} invalid\) return new ThreatResult\(ThreatVerdict\.Error/);
    assert.match(list, /issued >= expires/);
    assert.match(list, /value is null \|\| normalize\(value\) is not \{ \} normalized\)\s*throw new JsonException/);
    assert.match(list, /using var stream = File\.OpenRead\(path\);[\s\S]*?stream\.Length > MaxListBytes[\s\S]*?stream\.ReadExactly\(bytes\);/);
    assert.match(manager, /threatList is \{ Valid: true, Expires: \{ \} expiry \} && expiry <= DateTimeOffset\.UtcNow/);
});
test('duplicate SmartScreen setting uses safe default and empty view set remains pending', () => {
    assert.match(manager, /if \(seen\) damaged = true;\s*seen = true;/);
    assert.match(manager, /new SecuritySettings\(damaged \|\| smartScreen\)/);
    assert.match(manager, /State = current\.Count == 0 \? SmartScreenState\.Pending/);
});


test('old or future TCP probe timestamps cannot claim current protection', () => {
    assert.match(policy, /TimeSpan age = \(now \?\? DateTimeOffset\.Now\) - checkedAt\.Value;/);
    assert.match(policy, /age < TimeSpan\.Zero \|\| age > TimeSpan\.FromSeconds\(30\)/);
});


const networkDataSource = await readFile('apps/desktop/BrowserData.cs', 'utf8');
const networkUi = await readFile('apps/desktop/BrowserWindow.Network.cs', 'utf8');
test('network choice uses session-owned persistence without changing the applied transport', () => {
    assert.match(networkDataSource, /if \(chosen.Problem is not null \|\| !authorized\(\)\) return false;\s*var previous = Network;[\s\S]*?Network = chosen == NetworkSettings.Standard \? null : chosen;\s*committed = authorized\(\) && save\(\);[\s\S]*?if \(!committed\) Network = previous;/);
    const commit = sessionSource.slice(sessionSource.indexOf('internal bool TryCommitNetworkSettings('), sessionSource.indexOf('internal Stack<ClosedTab>'));
    assert.match(commit, /libraryDispatcher.VerifyAccess\(\);\s*bool Authorized\(\) => !IsPrivate && !IsEnding && !EngineLost && EngineHost.IsReady &&\s*persistentStatePath is not null && authorized\(\);[\s\S]*?return Data.TryCommitNetworkSettings\(chosen, Authorized,\s*\(\) => TrySaveLibrary\(Authorized\)\);/);
    assert.doesNotMatch(commit, /\bNetwork\s*=|\bConfigureNetwork\(|\bApplyDns\(|\bInitializeAsync\(/);
    assert.match(networkUi, /bool Authorized\(\) => SettingsReady\(\) && IsActive && session.SettingsUiOpen && session.PermissionUiOpen;\s*if \(!session.TryCommitNetworkSettings\(chosen, Authorized\)\)[\s\S]*?return;\s*\}\s*foreach \(var window in session.Windows.ToArray\(\)\)/);
    assert.doesNotMatch(networkUi, /\bdata\.Save\(|\bdata\.Network\s*=(?!=)/);
    assert.match(sessionSource, /internal NetworkSettings Network \{ get; \}/);
});

