// SPDX-License-Identifier: MPL-2.0
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, rm, stat, lstat } from 'node:fs/promises';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

assert.equal(process.platform, 'win32');
assert.equal(process.argv.length, 3, 'Pass the built veil-cef-wpf.exe');
const executable = resolve(process.argv[2]);
const requests = [];
const cookieChecks = new Map();
let httpCacheRequests = 0;
const titles = new Map([['/tabs-first', 'TAB_FIRST'], ['/tabs-second', 'TAB_SECOND'], ['/tabs-after-close', 'TAB_AFTER_CLOSE'], ['/tabs-preserved', 'TAB_PRESERVED'], ['/tabs-reseed', 'TAB_RESEEDED'], ['/tabs-cleared-first', 'TAB_CLEARED_FIRST'], ['/tabs-cleared-second', 'TAB_CLEARED_SECOND'], ['/tabs-storage-reseed', 'TAB_STORAGE_RESEEDED'], ['/tabs-storage-denied', 'TAB_STORAGE_DENIED'], ['/tabs-storage-deleted', 'TAB_STORAGE_DELETED'], ['/tabs-storage-kept', 'TAB_STORAGE_KEPT'], ['/tabs-http-cache-first', 'TAB_HTTP_CACHE_FIRST'], ['/tabs-http-cache-reused', 'TAB_HTTP_CACHE_REUSED'], ['/tabs-http-cache-cleared', 'TAB_HTTP_CACHE_CLEARED'], ['/tabs-http-cache-cookie', 'TAB_HTTP_CACHE_COOKIE'], ['/tabs-batch-seed-first', 'TAB_BATCH_SEED_FIRST'], ['/tabs-batch-denied', 'TAB_BATCH_DENIED'], ['/tabs-batch-first-deleted', 'TAB_BATCH_FIRST_DELETED'], ['/tabs-batch-kept', 'TAB_BATCH_KEPT'], ['/tabs-batch-reseed-first', 'TAB_BATCH_RESEED_FIRST'], ['/tabs-batch-final-first', 'TAB_BATCH_FINAL_FIRST'], ['/tabs-batch-second-deleted', 'TAB_BATCH_SECOND_DELETED'], ['/tabs-isolation-seed', 'TAB_ISOLATION_SEEDED']]);
const loginFixtures = new Map([
  ['/tabs-login-valid', '<form action="/unused-login"><input autocomplete="username"><input type="password"></form>'],
  ['/tabs-login-hidden', '<form hidden><input><input type="password"></form>'],
  ['/tabs-login-ambiguous', '<form><input><input type="password"><input type="password"></form>'],
  ['/tabs-login-cross-origin', '<form action="http://example.invalid/"><input><input type="password"></form>'],
  ['/tabs-login-iframe', '<iframe id="fixture-frame" src="/tabs-login-frame-child"></iframe>'],
  ['/tabs-login-new', '<form><input><input type="password" autocomplete="new-password"></form>'],
]);
const loginValueFixtures = new Map([
  ['/tabs-login-read', 'veil-synthetic-marker'],
  ['/tabs-login-read-empty', ''],
  ['/tabs-login-read-mismatch', 'different-synthetic-marker'],
]);
const loginFillFixtures = new Map([
  ['/tabs-login-fill-empty', ['', '']],
  ['/tabs-login-fill-matching', ['veil-synthetic-user', '']],
  ['/tabs-login-fill-existing', ['veil-synthetic-user', 'existing-synthetic-marker']],
  ['/tabs-login-fill-other', ['other-synthetic-user', '']],
  ['/tabs-login-fill-denied', ['', '']],
  ['/tabs-login-fill-duplicate', ['', '']],
  ['/tabs-login-fill-switch', ['', '']],
  ['/tabs-login-fill-navigation', ['', '']],
  ['/tabs-login-fill-destination', ['', '']],
  ['/tabs-login-fill-closing', ['', '']],
  ['/tabs-login-fill-reopened', ['', '']],
  ['/tabs-login-fill-renderer-recovered', ['', '']],
]);
const server = http.createServer((request, response) => {
  requests.push(request.url);
  if (request.url === '/fixture-http-cache-resource') {
    httpCacheRequests++;
    response.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'public, max-age=3600' });
    response.end('fixture-cache-' + httpCacheRequests);
    return;
  }
  if (['/tabs-after-close', '/tabs-preserved', '/tabs-cleared-first', '/tabs-cleared-second', '/tabs-storage-denied', '/tabs-storage-deleted', '/tabs-storage-kept', '/tabs-http-cache-cookie', '/tabs-batch-denied', '/tabs-batch-first-deleted', '/tabs-batch-kept', '/tabs-batch-final-first', '/tabs-batch-second-deleted'].includes(request.url))
    cookieChecks.set(request.url, request.headers.cookie ?? '');
  if (request.url === '/tabs-fresh-probe') {
    cookieChecks.set(request.url, request.headers.cookie ?? '');
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    response.end('<!doctype html><title>WAITING</title><script>(async()=>{' +
      'if(localStorage.getItem("veil-isolation-marker")===null&&!await caches.has("veil-isolation-cache"))' +
      'document.title="TAB_FRESH_PROFILE";})();</script>');
    return;
  }
  if (request.url === '/tabs-login-frame-child') {
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    response.end('<!doctype html><form><input><input type="password"></form>');
    return;
  }
  if (request.url === '/tabs-login-renderer-failure') {
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    response.end('<!doctype html><title>TAB_LOGIN_RENDERER_FAILURE</title><form><input><input type="password"></form>');
    return;
  }
  if (loginFillFixtures.has(request.url)) {
    const index = [...loginFillFixtures.keys()].indexOf(request.url);
    const [user, pass] = loginFillFixtures.get(request.url);
    const title = request.url === '/tabs-login-fill-renderer-recovered' ? 'TAB_LOGIN_RENDERER_RECOVERED' :
      index < 5 ? 'TAB_LOGIN_FILL_' + index :
      'TAB_LOGIN_LIFETIME_' + ['DUPLICATE', 'SWITCH', 'NAVIGATION', 'DESTINATION', 'CLOSING', 'REOPENED'][index - 5];
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    response.end('<!doctype html><title>' + title + '</title>' +
      '<form action="/unused-login" data-inputs="0" data-changes="0" data-submits="0">' +
      '<input autocomplete="username" value="' + user + '">' +
      '<input type="password" value="' + pass + '"></form>' +
      '<script>const f=document.querySelector("form");' +
      'f.addEventListener("input",()=>f.dataset.inputs=String(Number(f.dataset.inputs)+1));' +
      'f.addEventListener("change",()=>f.dataset.changes=String(Number(f.dataset.changes)+1));' +
      'f.addEventListener("submit",()=>f.dataset.submits=String(Number(f.dataset.submits)+1));</script>');
    return;
  }
  if (loginValueFixtures.has(request.url)) {
    const index = [...loginValueFixtures.keys()].indexOf(request.url);
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    response.end('<!doctype html><title>TAB_LOGIN_VALUES_' + index +
      '</title><form action="/unused-login"><input autocomplete="username" value="veil-synthetic-user">' +
      '<input type="password" value="' + loginValueFixtures.get(request.url) + '"></form>');
    return;
  }
  if (loginFixtures.has(request.url)) {
    const index = [...loginFixtures.keys()].indexOf(request.url);
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    if (request.url === '/tabs-login-iframe') {
      // Require the real same-origin child form to exist before reporting ready.
      response.end('<!doctype html><title>WAITING</title><script>' +
        'addEventListener("load",()=>{const f=document.getElementById("fixture-frame");' +
        'if(f.contentDocument.querySelector(\'input[type="password"]\'))document.title="TAB_LOGIN_' + index + '";});' +
        '</script>' + loginFixtures.get(request.url));
    } else response.end('<!doctype html><title>TAB_LOGIN_' + index + '</title>' + loginFixtures.get(request.url));
    return;
  }
  if (!titles.has(request.url)) { response.writeHead(404).end(); return; }
  const headers = { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' };
  if (request.url === '/tabs-first' || request.url === '/tabs-reseed' || request.url === '/tabs-storage-reseed') headers['Set-Cookie'] = 'fixture_first=synthetic-one; Path=/; HttpOnly; SameSite=Lax';
  if (request.url === '/tabs-second') headers['Set-Cookie'] = 'fixture_second=synthetic-two; Path=/; HttpOnly; SameSite=Lax';
  if (request.url === '/tabs-isolation-seed') headers['Set-Cookie'] = 'fixture_isolation=persistent-synthetic; Path=/; Max-Age=3600; HttpOnly; SameSite=Lax';
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
      'if(localStorage.getItem("veil-fixture-marker")===' + marker + '){' +
      (route === '/tabs-first' ? 'window.open("/tabs-popup-denied-target","_blank");' : '') +
      script + '}})();';
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
  if (['/tabs-batch-seed-first', '/tabs-batch-reseed-first'].includes(route)) {
    script = '(async()=>{localStorage.setItem("veil-fixture-marker","first");' +
      'const cache=await caches.open("veil-fixture-usage");' +
      'await cache.put("/fixture-cache-entry",new Response("batch-marker"));' +
      'if(localStorage.getItem("veil-fixture-marker")==="first"){' + script + '}})();';
  } else if (route.startsWith('/tabs-batch-')) {
    const deleted = ['/tabs-batch-first-deleted', '/tabs-batch-final-first', '/tabs-batch-second-deleted'].includes(route);
    const marker = JSON.stringify(route === '/tabs-batch-kept' ? 'second' : 'first');
    script = '(async()=>{const has=await caches.has("veil-fixture-usage");' +
      (deleted
        ? 'if(!has&&localStorage.getItem("veil-fixture-marker")===null){'
        : 'if(has&&localStorage.getItem("veil-fixture-marker")===' + marker +
          '&&(await (await caches.open("veil-fixture-usage")).match("/fixture-cache-entry"))){') +
      script + '}})();';
  }
  if (route === '/tabs-isolation-seed') {
    script = '(async()=>{localStorage.setItem("veil-isolation-marker","persistent-synthetic");' +
      'const cache=await caches.open("veil-isolation-cache");' +
      'await cache.put("/isolation-marker",new Response("persistent-synthetic"));' +
      'if(localStorage.getItem("veil-isolation-marker")==="persistent-synthetic"&&await cache.match("/isolation-marker")){' +
      script + '}})();';
  }
  response.end('<!doctype html><title>WAITING</title><body>' +
    (route === '/tabs-first' ? 'VEIL-TAB-FIND-ONLY-FIRST' : '') +
    '<script>' + script + '</script></body>');
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const fixtureTemp = await mkdtemp(join(tmpdir(), 'veil-cef-tabs-run-'));
const runs = [];
let passed = false;
async function runFixture(fresh) {
  // Separate roots under the runner-owned directory; never reuse a profile.
  const runTemp = await mkdtemp(join(fixtureTemp, fresh ? 'fresh-' : 'seed-'));
  const child = spawn(executable, [], {
    cwd: dirname(executable),
    env: { ...Object.fromEntries(Object.entries(process.env).filter(([key]) =>
      !['TEMP', 'TMP', 'VEIL_CEF_WPF_TABS', 'VEIL_CEF_WPF_TABS_FRESH_PROBE', 'VEIL_CEF_WPF_TEST_URL', 'VEIL_CEF_WPF_VAULT_EXE'].includes(key.toUpperCase()))),
      TEMP: runTemp, TMP: runTemp,
      VEIL_CEF_WPF_TABS: '1', VEIL_CEF_WPF_TABS_FRESH_PROBE: fresh ? '1' : '0',
      VEIL_CEF_WPF_TEST_URL: 'http://127.0.0.1:' + server.address().port + '/' },
    stdio: 'ignore', windowsHide: true,
  });
  const run = { child, runTemp };
  runs.push(run);
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    if (child.pid && child.exitCode === null) {
      const killed = spawnSync('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, timeout: 10000 });
      if (killed.status !== 0) child.kill();
    }
  }, 120000);
  try {
    const [code] = await once(child, 'exit');
    const profile = join(runTemp, 'veil-cef-wpf-profile-' + child.pid);
    const evidence = await readFile(join(profile, 'tabs.log'), 'utf8').catch(() => '');
    return { code, timedOut, evidence, profile };
  } finally {
    clearTimeout(timer);
  }
}

// Inspect only the test executable's processes associated with these run roots
// or direct parent PIDs. Refuse cleanup if inspection fails.
function assertNoFixtureProcesses() {
  const script = [
    '$ErrorActionPreference = "Stop"',
    '$roots = $env:VEIL_TEST_RUN_ROOTS | ConvertFrom-Json',
    '$ids = $env:VEIL_TEST_RUN_PIDS | ConvertFrom-Json',
    '$remaining = @(Get-CimInstance Win32_Process | Where-Object {',
    '  $p = $_',
    '  $sameExecutable = $p.ExecutablePath -eq $env:VEIL_TEST_EXECUTABLE -or',
    '    (-not $p.ExecutablePath -and $p.Name -eq [IO.Path]::GetFileName($env:VEIL_TEST_EXECUTABLE))',
    '  $sameExecutable -and (',
    '    $ids -contains $p.ProcessId -or $ids -contains $p.ParentProcessId -or',
    '    @($roots | Where-Object { $p.CommandLine -and $p.CommandLine.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0',
    '  )',
    '})',
    'if ($remaining.Count -ne 0) { $remaining | Select-Object ProcessId, ParentProcessId | Out-String | Write-Output; exit 1 }',
  ].join('\n');
  const checked = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    env: { ...process.env, VEIL_TEST_EXECUTABLE: executable,
      VEIL_TEST_RUN_ROOTS: JSON.stringify(runs.map(r => r.runTemp)),
      VEIL_TEST_RUN_PIDS: JSON.stringify(runs.map(r => r.child.pid)) },
    encoding: 'utf8', windowsHide: true, timeout: 15000,
  });
  assert.equal(checked.status, 0, 'Fixture process inspection failed or processes remain: ' + (checked.stdout ?? '') + (checked.stderr ?? ''));
}
try {
  const { code, timedOut, evidence, profile } = await runFixture(false);
  assert.equal(timedOut, false, evidence);
  assert.equal(code, 0, evidence);
  for (const route of titles.keys()) assert.equal(requests.filter(x => x === route).length, 1, route);
  assert.ok(!requests.includes('/tabs-stale'));
  assert.ok(!requests.includes('/tabs-popup-denied-target'), 'Denied popup must not reach HTTP');
  assert.match(evidence, /Blocked popup count visible only for the owning tab; no extra native view/);
  assert.match(evidence, /Zoom readback followed selected distinct-origin view and reset to 100 percent/);
  assert.match(evidence, /Browser full screen restored host state without changing page full screen/);
  assert.equal(cookieChecks.get('/tabs-after-close'), '', 'Deleted domain must not send its cookie');
  assert.equal(cookieChecks.get('/tabs-preserved'), 'fixture_second=synthetic-two', 'Other domain must retain its synthetic cookie');
  assert.equal(cookieChecks.get('/tabs-cleared-first'), '', 'Profile cookie clear must remove first domain cookie');
  assert.equal(cookieChecks.get('/tabs-cleared-second'), '', 'Profile cookie clear must remove second domain cookie');
  assert.match(evidence, /Unsupported mixed scope and denied profile clear preserved both cookies[\s\S]*Explicit profile cookie clear returned an empty cookie scan[\s\S]*Profile cookie clear preserved both origin localStorage markers/);
  assert.match(evidence, /Revoked cookie deletion left both domains untouched[\s\S]*Exact domain deletion preserved the other domain[\s\S]*Preserved domain HTTP check completed/);
  assert.match(evidence, /Fresh fixture cookie scan returned an empty list[\s\S]*Both views returned only the two synthetic cookie domains[\s\S]*Closed view cookie scan identity rejected/);
  assert.match(evidence, /Tab selection preserved both native identities and active address[\s\S]*Per-tab completion state and synthetic background status remained isolated[\s\S]*Find UI showed per-tab match results and discarded stale publication on switch, edit and close[\s\S]*Closing active tab preserved surviving view and navigation[\s\S]*Tabbed host registry drained/);
  assert.match(evidence, /Storage usage rejected an unapproved origin and observed first fixture cache[\s\S]*Distinct fixture origins returned distinct finite storage usage/);
  assert.equal(cookieChecks.get('/tabs-storage-denied'), 'fixture_first=synthetic-one');
  assert.equal(cookieChecks.get('/tabs-storage-deleted'), 'fixture_first=synthetic-one', 'Origin storage clear must preserve cookies');
  assert.equal(cookieChecks.get('/tabs-storage-kept'), '', 'Other origin cookie scope remains unchanged');
  assert.match(evidence, /Unapproved origin and revoked storage clear left fixture storage untouched[\s\S]*Exact origin storage clear preserved the other origin and cookie scope/);
  assert.equal(httpCacheRequests, 2, 'HTTP cache must be reused before clear and refetched after clear');
  assert.equal(cookieChecks.get('/tabs-http-cache-cookie'), 'fixture_first=synthetic-one', 'HTTP cache clear must retain cookies');
  assert.match(evidence, /Denied and mixed cache scopes preserved cached HTTP response[\s\S]*HTTP cache clear refetched resource and preserved origin storage[\s\S]*HTTP cache clear cookie preservation probe completed/);
  for (const route of ['/tabs-batch-denied', '/tabs-batch-first-deleted', '/tabs-batch-final-first'])
    assert.equal(cookieChecks.get(route), 'fixture_first=synthetic-one', 'Known-origin batch must retain cookie: ' + route);
  for (const route of ['/tabs-batch-kept', '/tabs-batch-second-deleted'])
    assert.equal(cookieChecks.get(route), '', 'Second origin cookie scope must be unchanged: ' + route);
  assert.match(evidence, /Known-origin batch revocation acknowledged one clear and preserved second origin[\s\S]*Known-origin batch cleared both fixture origins without claiming full profile clear/);
  assert.match(evidence, /Seeded persistent synthetic storage for next-process isolation probe[\s\S]*Persistent synthetic cookie present before shutdown/);
  // The first profile remains on disk during the second run. A new profile at
  // the same HTTP origin must not inherit its cookie or storage markers.
  assert.ok((await stat(join(profile, 'Default'))).isDirectory(), 'Disk-backed fixture cache is missing');
  assertNoFixtureProcesses();
  const fresh = await runFixture(true);
  assert.equal(fresh.timedOut, false, fresh.evidence);
  assert.equal(fresh.code, 0, fresh.evidence);
  assert.notEqual(fresh.profile, profile);
  assert.ok((await stat(profile)).isDirectory());
  assert.equal(requests.filter(route => route === '/tabs-fresh-probe').length, 1);
  assert.equal(cookieChecks.get('/tabs-fresh-probe'), '', 'Fresh profile leaked earlier cookies');
  assert.match(fresh.evidence, /Fresh fixture cookie scan returned an empty list[\s\S]*Fresh process saw no earlier LocalStorage or CacheStorage[\s\S]*Tabbed host registry drained/);
  for (const route of loginFixtures.keys()) assert.equal(requests.filter(x => x === route).length, 1, route);
  assert.equal(requests.filter(x => x === '/tabs-login-frame-child').length, 1, 'Child form must actually load');
  assert.ok(!requests.includes('/unused-login'), 'Probe must never submit a form');
  for (const route of loginValueFixtures.keys()) assert.equal(requests.filter(x => x === route).length, 1, route);
  for (const route of loginFillFixtures.keys()) assert.equal(requests.filter(x => x === route).length, 1, route);
  assert.match(fresh.evidence, /Synthetic login fill verified exact markers, input\/change events, existing values and revoked dispatch without submission/);
  assert.match(fresh.evidence, /Duplicate login operation rejected without releasing original slot[\s\S]*A-B-A tab switch invalidated login authorization and preserved empty form[\s\S]*Navigation during native authorization rejected fill and preserved destination form/);
  assert.match(fresh.evidence, /Closing login target invalidated authorization and retired native identity[\s\S]*Reopened login view retained new identity and empty form/);
  assert.equal(requests.filter(x => x === '/tabs-login-renderer-failure').length, 1);
  assert.match(fresh.evidence, /Expected renderer failure invalidated the pending login observation[\s\S]*Failed renderer refused another login observation before recovery[\s\S]*Explicit recovery navigation restored login observation on the same view with empty form/);
  assert.match(fresh.evidence, /Synthetic values check rejected authorization at native dispatch[\s\S]*Synthetic login values equality accepted matching markers and rejected empty and mismatched markers/);
  assert.ok(!fresh.evidence.includes('veil-synthetic-user') && !fresh.evidence.includes('veil-synthetic-marker'), 'Synthetic input values must not appear in diagnostic output');
  assert.match(fresh.evidence, /Login probe discarded result after authorization expired[\s\S]*Boolean-only login probe accepted one form and rejected hidden, ambiguous, cross-origin, iframe and new-password forms/);
  assertNoFixtureProcesses();
  // Only delete the unique directory created by this runner. Preserve failed
  // evidence and refuse to recurse into an unexpected root link.
  assert.ok(!(await lstat(fixtureTemp)).isSymbolicLink(), 'Fixture root was replaced with a link');
  // Preserve successful synthetic observation evidence in the caller's stdout log.
  console.log('Seed fixture observations:\n' + evidence);
  console.log('Fresh fixture observations:\n' + fresh.evidence);
  await rm(fixtureTemp, { recursive: true, force: false, maxRetries: 5, retryDelay: 200 });
  assert.equal(await stat(fixtureTemp).then(() => true, error => {
    if (error.code !== 'ENOENT') throw error;
    return false;
  }), false, 'Fixture directory remained after cleanup');
  passed = true;
  console.log('PASS: same-window tabs, site-data scopes, fresh process isolation and temporary fixture cleanup');
} finally {
  server.closeAllConnections();
  server.close();
  if (!passed) console.error('Fixture evidence directory (not automatically removed on failure): ' + fixtureTemp);
}
