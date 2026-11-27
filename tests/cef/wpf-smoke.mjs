// SPDX-License-Identifier: MPL-2.0
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { watch } from 'node:fs';
import { mkdtemp, readFile, readdir } from 'node:fs/promises';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

assert.equal(process.platform, 'win32');
assert.equal(process.argv.length, 4, 'Pass the built veil-cef-wpf.exe and veil-vault.exe paths');
const executable = resolve(process.argv[2]);
const vault = resolve(process.argv[3]);
const requests = [];
const pageAttachmentRequests = [];
const anonymousCredentialObservations = [];
// Synthetic fixture cookie only.
const sessionCookie = 'veil_fixture_session=synthetic-session';
const hasSessionCookie = request => (request.headers.cookie ?? '').split(';').some(part => part.trim() === sessionCookie);
let parallelCancelClosed = false;
let parallelKeepFinished = false;
const deniedFile = 'veil-cef-denied-fixture.txt';
const deniedBlob = 'veil-cef-denied-blob.txt';
const body = Buffer.from(Array.from({ length: 96 * 1024 }, (_, index) => index % 251));
const chunkSize = 16 * 1024;
const streamedBody = Buffer.from(Array.from({ length: 768 * 1024 }, (_, index) => index % 251));
const isDownloadFile = file => /\.crdownload$/i.test(file) ||
  file.toLowerCase().endsWith(deniedFile) || file.toLowerCase().endsWith(deniedBlob);
const server = http.createServer((request, response) => {
  requests.push(request.url);
  if (request.url === '/wpf-address') {
    response.writeHead(302, { Location: '/wpf-first' }).end();
    return;
  }
  if (request.url === '/wpf-policy') {
    response.writeHead(200, { 'Content-Type': 'text/html',
      'Set-Cookie': sessionCookie + '; Path=/; HttpOnly; SameSite=Lax',
      'Cache-Control': 'no-store' });
    response.end(`<!doctype html><a id="download" href="/wpf-attachment">Download</a>
      <a id="vault-download" href="/wpf-owned-attachment">Save to fixture vault</a>
      <script>
        window.open('/wpf-popup-target', '_blank');
        const locationDenied = new Promise(resolve => navigator.geolocation.getCurrentPosition(
          () => resolve(false), error => resolve(error.code === 1)));
        const mediaDenied = navigator.mediaDevices.getUserMedia({ video: true }).then(
          stream => { stream.getTracks().forEach(track => track.stop()); return false; },
          error => error.name === 'NotAllowedError');
        const responseMatches = fetch('/wpf-response').then(response => response.arrayBuffer()).then(buffer => {
          const bytes = new Uint8Array(buffer);
          return bytes.length === ${body.length} && bytes.every((value, index) => value === index % 251);
        });
        Promise.all([locationDenied, mediaDenied, responseMatches]).then(results => {
          if (!results.every(Boolean)) return;
          document.title = 'VEIL_CEF_RESPONSE_AND_' + 'PERMISSIONS_OK';
          const blob = document.createElement('a');
          blob.href = URL.createObjectURL(new Blob(['Synthetic CEF Blob fixture']));
          blob.download = '${deniedBlob}';
          document.body.append(blob);
          blob.click();
          setTimeout(() => document.getElementById('download').click(), 100);
        });
      </script>`);
    return;
  }
  if (request.url === '/wpf-response') {
    response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.write(body.subarray(0, 32 * 1024));
    setTimeout(() => response.end(body.subarray(32 * 1024)), 25);
    return;
  }
  if (request.url === '/wpf-attachment') {
    response.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Disposition': `attachment; filename="${deniedFile}"`,
      'Cache-Control': 'no-store',
      'Content-Length': body.length,
    });
    response.write(body.subarray(0, 16 * 1024));
    setTimeout(() => response.end(body.subarray(16 * 1024)), 250);
    return;
  }
  if (['/wpf-owned-parallel-keep', '/wpf-owned-parallel-cancel'].includes(request.url)) {
    response.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Disposition': `attachment; filename="${deniedFile}"`,
      'Content-Length': streamedBody.length,
      'Cache-Control': 'no-store',
      Connection: 'close',
    });
    response.write(streamedBody.subarray(0, chunkSize));
    if (request.url === '/wpf-owned-parallel-cancel') {
      response.once('close', () => { parallelCancelClosed = !response.writableFinished; });
      return;
    }
    let offset = chunkSize;
    function resumeAfterCancel() {
      if (response.destroyed) return;
      if (!parallelCancelClosed) { setTimeout(resumeAfterCancel, 15); return; }
      const next = offset + chunkSize;
      response.write(streamedBody.subarray(offset, next));
      offset = next;
      if (offset === streamedBody.length) {
        response.once('finish', () => { parallelKeepFinished = true; });
        response.end();
      } else setTimeout(resumeAfterCancel, 15);
    }
    resumeAfterCancel();
    return;
  }
  if (request.url === '/wpf-owned-redirect') {
    anonymousCredentialObservations.push([request.url, hasSessionCookie(request)]);
    response.writeHead(302, { Location: '/wpf-owned-redirect-target',
      'Content-Length': 0, 'Cache-Control': 'no-store' }).end();
    return;
  }
  if (['/wpf-owned-attachment', '/wpf-owned-truncated', '/wpf-owned-saturated', '/wpf-owned-redirect-target'].includes(request.url)) {
    if (request.url === '/wpf-owned-attachment') {
      const authenticated = hasSessionCookie(request);
      pageAttachmentRequests.push([request.method, request.headers['x-veil-fixture-handoff'], authenticated]);
      if (!authenticated) {
        response.writeHead(401, { 'Content-Length': 0, 'Cache-Control': 'no-store' }).end();
        return;
      }
    } else {
      anonymousCredentialObservations.push([request.url, hasSessionCookie(request)]);
    }
    response.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Disposition': `attachment; filename="${deniedFile}"`,
      'Content-Length': streamedBody.length,
      'Cache-Control': 'no-store',
      Connection: 'close',
    });
    if (request.url === '/wpf-owned-truncated') {
      response.write(streamedBody.subarray(0, chunkSize));
      setTimeout(() => response.end(), 50);
      return;
    }
    let offset = 0;
    function writeChunk() {
      if (response.destroyed) return;
      const nextOffset = offset + chunkSize;
      response.write(streamedBody.subarray(offset, nextOffset));
      offset = nextOffset;
      if (offset === streamedBody.length) response.end();
      else setTimeout(writeChunk, 15);
    }
    writeChunk();
    return;
  }
  if (request.url === '/wpf-isolation' || request.url === '/wpf-isolation-reopened') {
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    const title = request.url === '/wpf-isolation' ? 'ISOLATION' : 'REOPENED';
    response.end(`<!doctype html><title>VEIL_CEF_WPF_${title}</title>`);
    return;
  }
  if (request.url !== '/wpf-first' && request.url !== '/wpf-second') {
    response.writeHead(404).end();
    return;
  }
  response.writeHead(200, { 'Content-Type': 'text/html' });
  const title = request.url === '/wpf-first' ? 'WPF_OK' : 'WPF_SECOND';
  response.end(`<!doctype html><script>document.title="VEIL_CEF_"+"${title}"</script>`);
});

server.listen(0, '127.0.0.1');
await once(server, 'listening');
const url = `http://127.0.0.1:${server.address().port}/wpf-address`;
const fixtureTemp = await mkdtemp(join(tmpdir(), 'veil-cef-wpf-run-'));
const observedDownloads = [];
let watcherError;
const watcher = watch(fixtureTemp, { recursive: true }, (_, filename) => {
  if (filename && isDownloadFile(filename)) observedDownloads.push(filename);
});
watcher.on('error', error => { watcherError = error; });
const child = spawn(executable, [], {
  cwd: dirname(executable),
  env: { ...Object.fromEntries(Object.entries(process.env).filter(([key]) =>
    !['TEMP', 'TMP', 'VEIL_CEF_WPF_TABS', 'VEIL_CEF_WPF_TABS_FRESH_PROBE', 'VEIL_CEF_WPF_TEST_URL', 'VEIL_CEF_WPF_VAULT_EXE'].includes(key.toUpperCase()))),
    TEMP: fixtureTemp, TMP: fixtureTemp, VEIL_CEF_WPF_TABS: '0', VEIL_CEF_WPF_TABS_FRESH_PROBE: '0',
    VEIL_CEF_WPF_TEST_URL: url, VEIL_CEF_WPF_VAULT_EXE: vault },
  stdio: 'ignore',
  windowsHide: true,
});
let timedOut = false;
const timer = setTimeout(() => {
  timedOut = true;
  if (child.pid && child.exitCode === null) {
    const result = spawnSync('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
    if (result.status !== 0) child.kill();
  }
}, 25000);

try {
  const [exitCode] = await once(child, 'exit');
  const log = join(fixtureTemp, `veil-cef-wpf-profile-${child.pid}`, 'smoke.log');
  const evidence = await readFile(log, 'utf8').catch(() => 'No WPF smoke log was written.');
  assert.equal(timedOut, false, 'WPF browser timed out');
  assert.equal(exitCode, 0, `WPF browser exited unsuccessfully\n${evidence}`);
  assert.ok(requests.includes('/wpf-address'), 'The address bar did not navigate');
  assert.ok(requests.includes('/wpf-first'), 'The redirect did not navigate');
  assert.ok(!requests.includes('/wpf-denied'), 'Canceled navigation contacted the server');
  assert.ok(requests.includes('/wpf-second'), 'The second address did not navigate');
  assert.equal(requests.filter(request => request === '/wpf-isolation').length, 1, 'Secondary view must navigate once');
  assert.equal(requests.filter(request => request === '/wpf-isolation-reopened').length, 1, 'Reopened view must navigate once');
  assert.ok(!requests.includes('/wpf-stale-handle'), 'Retired identity must not navigate');
  for (const route of ['/wpf-owned-parallel-keep', '/wpf-owned-parallel-cancel'])
    assert.equal(requests.filter(request => request === route).length, 1, 'Parallel request must not replay');
  assert.ok(parallelCancelClosed && parallelKeepFinished, 'Cancel must close only the canceled HTTP response');
  assert.match(evidence, /Observed both CEF vault streams receiving data concurrently[\s\S]*Canceled parallel vault published no object; surviving vault restored all 786432 bytes/);
  assert.match(evidence, /CEF view slot reused with a fresh identity and clean state[\s\S]*Repeated secondary view close preserved primary view/);
  assert.match(evidence, /Observed two independent CEF views with distinct source, title, history and mute state[\s\S]*Secondary CEF view closed; stale handle rejected; primary remains live[\s\S]*Observed matched redirect, completion, and back and forward navigation/);
  assert.ok(requests.includes('/wpf-error'), 'The failed address did not navigate');
  assert.ok(requests.includes('/wpf-policy'), 'The permission fixture did not navigate');
  assert.equal(requests.filter(request => request === '/wpf-attachment').length, 1, 'The attachment must not be replayed');
  assert.equal(requests.filter(request => request === '/wpf-response').length, 1, 'The body must come from the original request');
  assert.ok(!requests.includes('/wpf-owned-redirect-target'), 'Owned attachment followed a redirect');
  for (const route of ['/wpf-owned-truncated', '/wpf-owned-redirect', '/wpf-owned-saturated', '/wpf-owned-attachment']) {
    assert.equal(requests.filter(request => request === route).length, 1, `${route} must not be replayed`);
  }
  assert.match(evidence, /Development adapter rejected unsupported profile, network and navigation inputs[\s\S]*Navigation request canceled before HTTP[\s\S]*Observed JavaScript title[\s\S]*Observed matched redirect, completion, and back and forward navigation[\s\S]*Observed HTTP failure completion[\s\S]*Observed rejected HTTP and Blob downloads, geolocation and media permissions[\s\S]*WPF window closed[\s\S]*CEF browser closed[\s\S]*WPF dispatcher stopped/);
  assert.match(evidence, /Observed shared mute state round trip; playback and favicon unavailable/);
  assert.match(evidence, /Observed shared engine source, title and history events/);
  assert.match(evidence, /Normal fixture shutdown emitted no unexpected process failure/);
  assert.match(evidence, /CEF view registry drained and vault task settled/);
  assert.match(evidence, /Observed mandatory native popup denial/);
  assert.ok(!requests.includes('/wpf-popup-target'), 'Denied popup must not reach HTTP');
  assert.match(evidence, /Intercepted the page attachment before browser navigation reached HTTP/);
  assert.deepEqual(pageAttachmentRequests, [['GET', '1', true]],
    'Only the native same-origin handoff may send the authenticated attachment request');
  assert.deepEqual(anonymousCredentialObservations, [
    ['/wpf-owned-truncated', false], ['/wpf-owned-redirect', false], ['/wpf-owned-saturated', false],
  ], 'Anonymous fixture requests must not receive the session cookie');
  assert.equal(watcherError, undefined, 'The temporary directory watcher failed');
  assert.deepEqual(observedDownloads, [], 'Observed a download artifact during the run');
  const files = await readdir(fixtureTemp, { recursive: true });
  assert.ok(!files.some(isDownloadFile), 'A download artifact remained after shutdown');
  const sealed = join(`veil-cef-wpf-profile-${child.pid}`, 'fixture-vault', 'fixture.veil');
  const parallelSealed = join(`veil-cef-wpf-profile-${child.pid}`, 'fixture-vault-parallel-keep', 'fixture.veil');
  assert.deepEqual(files.filter(file => file.endsWith('.veil')).sort(), [sealed, parallelSealed].sort());
  for (const object of [sealed, parallelSealed]) {
    const ciphertext = await readFile(join(fixtureTemp, object));
    assert.ok(!ciphertext.includes(body.subarray(0, 512)), 'The stored object contains a plaintext fixture segment');
  }
  assert.match(evidence, /Rust vault aborted the truncated stream without publishing an object[\s\S]*CEF refused the redirect and Rust vault published no object or response bytes[\s\S]*CEF canceled the saturated stream and Rust vault published no object[\s\S]*Rust vault streamed and restored all 786432 bytes/);
  const observed = /Verified 98304 response bytes in memory\. Blocked attachment passed (\d+) bytes to the filter\./.exec(evidence);
  assert.ok(observed);
  assert.ok(Number(observed[1]) < body.length, 'The delayed attachment should be canceled before the full body arrives');
  console.log(`PASS: CEF vault round trip and download policy. Blocked attachment filter bytes=${observed[1]} (${log})`);
} finally {
  clearTimeout(timer);
  watcher.close();
  server.close();
}
