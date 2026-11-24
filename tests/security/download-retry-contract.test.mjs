// SPDX-License-Identifier: MPL-2.0
// Static contract checks only; the interactive HTTPS smoke verifies actual server requests.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
const browser = await readFile('apps/desktop/BrowserWindow.xaml.cs', 'utf8');
const toolbar = await readFile('apps/desktop/BrowserWindow.Toolbar.cs', 'utf8');
const vault = await readFile('apps/desktop/DownloadVault.cs', 'utf8');
test('retry consumes its candidate and rechecks before capture', () => {
    assert.match(toolbar, /retryDownload = null;[\s\S]*?UserPrompts\.Confirm[\s\S]*?!CanRetry\(item\)[\s\S]*?CaptureAsync\(/);
    assert.match(browser, /retryReady: retry \? \(\) => candidate is not null && CanRetry\(candidate\) : null/);
});
test('retry context required after slot, threat check and cookie lookup, before HTTP', () => {
    assert.match(vault, /await captureSlot\.WaitAsync\(cancel\);[\s\S]*?GetFollowingRedirectsAsync\(uri, web, cancel, retry, retryReady\)/);
    const request = vault.slice(vault.indexOf('private async Task<HttpResponseMessage> GetFollowingRedirectsAsync'));
    assert.match(request, /retry && \(!DownloadFailurePolicy\.SafeRetryAddress\(uri\) \|\| retryReady\?\.Invoke\(\) != true\)/);
    assert.match(request, /RequireLiveRetry\(\);[\s\S]*?await RejectThreatAsync[\s\S]*?RequireLiveRetry\(\);[\s\S]*?GetCookiesAsync[\s\S]*?RequireLiveRetry\(\);[\s\S]*?http\.SendAsync/);
    assert.match(request, /if \(retry\) throw new DownloadRequestFailure\([^;]+false\);/);
});
