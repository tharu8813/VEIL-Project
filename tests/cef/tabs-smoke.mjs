// SPDX-License-Identifier: MPL-2.0
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile } from 'node:fs/promises';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

assert.equal(process.platform, 'win32');
assert.equal(process.argv.length, 3, 'Pass the built veil-cef-wpf.exe');
const executable = resolve(process.argv[2]);
const requests = [];
const cookieChecks = new Map();
let httpCacheRequests = 0;
const titles = new Map([['/tabs-first', 'TAB_FIRST'], ['/tabs-second', 'TAB_SECOND'], ['/tabs-after-close', 'TAB_AFTER_CLOSE'], ['/tabs-preserved', 'TAB_PRESERVED'], ['/tabs-reseed', 'TAB_RESEEDED'], ['/tabs-cleared-first', 'TAB_CLEARED_FIRST'], ['/tabs-cleared-second', 'TAB_CLEARED_SECOND'], ['/tabs-storage-reseed', 'TAB_STORAGE_RESEEDED'], ['/tabs-storage-denied', 'TAB_STORAGE_DENIED'], ['/tabs-storage-deleted', 'TAB_STORAGE_DELETED'], ['/tabs-storage-kept', 'TAB_STORAGE_KEPT'], ['/tabs-http-cache-first', 'TAB_HTTP_CACHE_FIRST'], ['/tabs-http-cache-reused', 'TAB_HTTP_CACHE_REUSED'], ['/tabs-http-cache-cleared', 'TAB_HTTP_CACHE_CLEARED'], ['/tabs-http-cache-cookie', 'TAB_HTTP_CACHE_COOKIE']]);
const server = http.createServer((request, response) => {
  requests.push(request.url);
  if (request.url === '/fixture-http-cache-resource') {
    httpCacheRequests++;
    response.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'public, max-age=3600' });
    response.end('fixture-cache-' + httpCacheRequests);
    return;
  }
  if (['/tabs-after-close', '/tabs-preserved', '/tabs-cleared-first', '/tabs-cleared-second', '/tabs-storage-denied', '/tabs-storage-deleted', '/tabs-storage-kept', '/tabs-http-cache-cookie'].includes(request.url))
    cookieChecks.set(request.url, request.headers.cookie ?? '');
  if (!titles.has(request.url)) { response.writeHead(404).end(); return; }
  const headers = { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' };
  if (request.url === '/tabs-first' || request.url === '/tabs-reseed' || request.url === '/tabs-storage-reseed') headers['Set-Cookie'] = 'fixture_first=synthetic-one; Path=/; HttpOnly; SameSite=Lax';
  if (request.url === '/tabs-second') headers['Set-Cookie'] = 'fixture_second=synthetic-two; Path=/; HttpOnly; SameSite=Lax';
  response.writeHead(200, headers);
  const route = request.url;
  const title = JSON.stringify(titles.get(route));
  let script = 'document.title=' + title + ';';
  if (route === '/tabs-first' || route === '/tabs-second') {
    const marker = JSON.stringify(route === '/tabs-first' ? 'first' : 'second');
    const size = route === '/tabs-first' ? 65536 : 1048576;
    script = '(async()=>{localStorage.setItem("veil-fixture-marker",' + marker + ');' +
      'const cache=await caches.open("veil-fixture-usage");' +
      'await cache.put("/fixture-cache-entry",new Response(new Uint8Array(' + size + ')));' +
      'if(localStorage.getItem("veil-fixture-marker")===' + marker + '){' + script + '}})();';
  } else if (['/tabs-reseed', '/tabs-cleared-first', '/tabs-cleared-second'].includes(route)) {
    const marker = JSON.stringify(route === '/tabs-cleared-second' ? 'second' : 'first');
    script = 'if(localStorage.getItem("veil-fixture-marker")===' + marker + '){' + script + '}';
  }
  if (['/tabs-storage-reseed', '/tabs-storage-denied', '/tabs-storage-deleted', '/tabs-storage-kept'].includes(route)) {
    const deleted = route === '/tabs-storage-deleted';
    const marker = JSON.stringify(route === '/tabs-storage-kept' ? 'second' : 'first');
    // Check cache existence without creating it in the deleted-origin case.
    script = '(async()=>{const has=await caches.has("veil-fixture-usage");' +
      (deleted
        ? 'if(!has&&localStorage.getItem("veil-fixture-marker")===null){'
        : 'if(has&&localStorage.getItem("veil-fixture-marker")===' + marker +
          '&&(await (await caches.open("veil-fixture-usage")).match("/fixture-cache-entry"))){') +
      script + '}})();';
  }
  if (['/tabs-http-cache-first', '/tabs-http-cache-reused', '/tabs-http-cache-cleared'].includes(route)) {
    const expected = JSON.stringify(route === '/tabs-http-cache-cleared' ? 'fixture-cache-2' : 'fixture-cache-1');
    script = '(async()=>{const r=await fetch("/fixture-http-cache-resource");' +
      'if(r.ok&&(await r.text())===' + expected +
      '&&localStorage.getItem("veil-fixture-marker")==="second"&&await caches.has("veil-fixture-usage")' +
      '&&(await (await caches.open("veil-fixture-usage")).match("/fixture-cache-entry"))){' + script + '}})();';
  }
  response.end('<!doctype html><title>WAITING</title><script>' + script + '</script>');
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const fixtureTemp = await mkdtemp(join(tmpdir(), 'veil-cef-tabs-run-'));
const child = spawn(executable, [], {
  cwd: dirname(executable),
  env: { ...process.env, TEMP: fixtureTemp, TMP: fixtureTemp,
    VEIL_CEF_WPF_TABS: '1', VEIL_CEF_WPF_TEST_URL: 'http://127.0.0.1:' + server.address().port + '/' },
  stdio: 'ignore', windowsHide: true,
});
let timedOut = false;
const timer = setTimeout(() => {
  timedOut = true;
  if (child.pid && child.exitCode === null)
    spawnSync('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
}, 70000);
try {
  const [code] = await once(child, 'exit');
  const evidence = await readFile(join(fixtureTemp, 'veil-cef-wpf-profile-' + child.pid, 'tabs.log'), 'utf8').catch(() => '');
  assert.equal(timedOut, false, evidence);
  assert.equal(code, 0, evidence);
  for (const route of titles.keys()) assert.equal(requests.filter(x => x === route).length, 1, route);
  assert.ok(!requests.includes('/tabs-stale'));
  assert.equal(cookieChecks.get('/tabs-after-close'), '', 'Deleted domain must not send its cookie');
  assert.equal(cookieChecks.get('/tabs-preserved'), 'fixture_second=synthetic-two', 'Other domain must retain its synthetic cookie');
  assert.equal(cookieChecks.get('/tabs-cleared-first'), '', 'Profile cookie clear must remove first domain cookie');
  assert.equal(cookieChecks.get('/tabs-cleared-second'), '', 'Profile cookie clear must remove second domain cookie');
  assert.match(evidence, /Unsupported mixed scope and denied profile clear preserved both cookies[\s\S]*Explicit profile cookie clear returned an empty cookie scan[\s\S]*Profile cookie clear preserved both origin localStorage markers/);
  assert.match(evidence, /Revoked cookie deletion left both domains untouched[\s\S]*Exact domain deletion preserved the other domain[\s\S]*Preserved domain HTTP check completed/);
  assert.match(evidence, /Fresh fixture cookie scan returned an empty list[\s\S]*Both views returned only the two synthetic cookie domains[\s\S]*Closed view cookie scan identity rejected/);
  assert.match(evidence, /Tab selection preserved both native identities and active address[\s\S]*Closing active tab preserved surviving view and navigation[\s\S]*Tabbed host registry drained/);
  assert.match(evidence, /Storage usage rejected an unapproved origin and observed first fixture cache[\s\S]*Distinct fixture origins returned distinct finite storage usage/);
  assert.equal(cookieChecks.get('/tabs-storage-denied'), 'fixture_first=synthetic-one');
  assert.equal(cookieChecks.get('/tabs-storage-deleted'), 'fixture_first=synthetic-one', 'Origin storage clear must preserve cookies');
  assert.equal(cookieChecks.get('/tabs-storage-kept'), '', 'Other origin cookie scope remains unchanged');
  assert.match(evidence, /Unapproved origin and revoked storage clear left fixture storage untouched[\s\S]*Exact origin storage clear preserved the other origin and cookie scope/);
  assert.equal(httpCacheRequests, 2, 'HTTP cache must be reused before clear and refetched after clear');
  assert.equal(cookieChecks.get('/tabs-http-cache-cookie'), 'fixture_first=synthetic-one', 'HTTP cache clear must retain cookies');
  assert.match(evidence, /Denied and mixed cache scopes preserved cached HTTP response[\s\S]*HTTP cache clear refetched resource and preserved origin storage[\s\S]*HTTP cache clear cookie preservation probe completed/);
  console.log('PASS: same-window tabs, cookie scopes, storage usage, origin and HTTP cache clearing');
} finally {
  clearTimeout(timer);
  server.closeAllConnections();
  server.close();
}
