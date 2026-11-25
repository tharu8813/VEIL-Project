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
    assert.match(policy, /BypassProxyOnLocal = false, BypassList = \[\]/);
});
