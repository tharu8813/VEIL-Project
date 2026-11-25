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
    assert.match(vault, /await captureSlot\.WaitAsync\(cancel\);[\s\S]*?GetFollowingRedirectsAsync\(uri, web, cancel, retry, retryReady, session\)/);
    const request = vault.slice(vault.indexOf('private async Task<HttpResponseMessage> GetFollowingRedirectsAsync'));
    assert.match(request, /retry && \(!DownloadFailurePolicy\.SafeRetryAddress\(uri\) \|\| retryReady\?\.Invoke\(\) != true\)/);
    assert.match(request, /RequireLiveRetry\(\);[\s\S]*?await RejectThreatAsync[\s\S]*?RequireLiveRetry\(\);[\s\S]*?GetCookiesAsync[\s\S]*?RequireLiveRetry\(\);[\s\S]*?http\.SendAsync/);
    assert.match(request, /if \(retry\) throw new DownloadRequestFailure\([^;]+false\);/);
});

test('authenticated index and metadata still require valid unique records', () => {
    assert.match(vault, /if \(ValidIndex\(candidate\)\) loaded = candidate;/);
    assert.match(vault, /finally \{ CryptographicOperations\.ZeroMemory\(bytes\); \}\s*if \(loaded is not null\) break;/);
    assert.match(vault, /entries is not null && entries\.All\(ValidEntry\) &&\s*entries\.Select\(e => e\.Id\)\.Distinct\(StringComparer\.OrdinalIgnoreCase\)\.Count\(\) == entries\.Count/);
    assert.match(vault, /entry is not null && Guid\.TryParseExact\(entry\.Id, "N", out _\) &&\s*!string\.IsNullOrWhiteSpace\(entry\.Name\) && entry\.Bytes >= 0/);
    assert.match(vault, /finally \{ CryptographicOperations\.ZeroMemory\(meta\); \}[\s\S]*?entries\.Add\(ValidEntry\(entry\) && entry!\.Id == id \? entry/);
});

test('queued downloads and preflight requests cannot enter a replacement vault session', () => {
    assert.match(vault, /if \(session is null \|\| !ReferenceEquals\(serve, session\) \|\| !Unlocked\)\s*throw new DownloadRequestFailure\([^;]+false\);/);
    const capture = vault.slice(vault.indexOf('internal async Task<VaultEntry> CaptureAsync('), vault.indexOf('internal async Task<VaultEntry> CaptureInlineAsync('));
    assert.match(capture, /Process session = CaptureSession\(\);[\s\S]*?await captureSlot\.WaitAsync\(cancel\);[\s\S]*?RequireCaptureSession\(session\);[\s\S]*?GetFollowingRedirectsAsync\([^;]+session\);[\s\S]*?RequireCaptureSession\(session\);[\s\S]*?SealCaptureAsync\([^;]+session\);/);
    const inline = vault.slice(vault.indexOf('internal async Task<VaultEntry> CaptureInlineAsync('), vault.indexOf('private async Task<VaultEntry> SealCaptureAsync('));
    assert.match(inline, /Process session = CaptureSession\(\);[\s\S]*?await captureSlot\.WaitAsync\(download\.Token\);[\s\S]*?RequireCaptureSession\(session\);[\s\S]*?await RejectThreatAsync[\s\S]*?RequireCaptureSession\(session\);[\s\S]*?SealCaptureAsync\([^;]+session\);/);
    assert.match(vault, /private async Task<VaultEntry> SealCaptureAsync\([^)]*Process session\)[\s\S]*?await gate\.WaitAsync\(cancel\);[\s\S]*?RequireCaptureSession\(session\);/);
    assert.match(vault, /void RequireLiveRetry\(\)\s*\{\s*RequireCaptureSession\(session\);\s*cancel\.ThrowIfCancellationRequested\(\);/);
});

test('vault lock never injects a text command into binary frames and verifies helper exit', () => {
    const lock = vault.slice(vault.indexOf('internal void Lock()'), vault.indexOf('// ── Rotation'));
    assert.match(lock, /lockRequested = true;\s*Entries = \[\];\s*KillServe\(\);\s*serve\?\.Dispose\(\);\s*serve = null;/);
    assert.match(lock, /if \(!process\.WaitForExit\(3000\)\)\s*throw new IOException/);
    assert.doesNotMatch(lock, /WriteQuit|\.Write(?:Async)?\(|\.Flush(?:Async)?\(/);
    assert.match(vault, /internal bool Unlocked => !lockRequested && serve is \{ HasExited: false \}/);
    assert.match(vault, /private Stream PipeInput => !lockRequested && input is \{ \} stream \? stream : throw new IOException/);
    assert.match(vault, /private Stream PipeOutput => !lockRequested && output is \{ \} stream \? stream : throw new IOException/);
    assert.match(vault, /if \(canAbort && ReferenceEquals\(serve, session\) && Unlocked\)/);
    assert.match(vault, /SmokeAfterFrame\?\.Invoke\(\);\s*#endif\s*RequireCaptureSession\(session\);/);
    assert.match(vault, /await SaveIndexLockedAsync\(\);\s*RequireCaptureSession\(session\);\s*return entry;/);
    assert.doesNotMatch(vault, /private void WriteQuit\(/);
});
