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
const titles = new Map([['/tabs-first', 'TAB_FIRST'], ['/tabs-second', 'TAB_SECOND'], ['/tabs-after-close', 'TAB_AFTER_CLOSE']]);
const server = http.createServer((request, response) => {
  requests.push(request.url);
  if (!titles.has(request.url)) { response.writeHead(404).end(); return; }
  const headers = { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' };
  if (request.url === '/tabs-first') headers['Set-Cookie'] = 'fixture_first=synthetic-one; Path=/; HttpOnly; SameSite=Lax';
  if (request.url === '/tabs-second') headers['Set-Cookie'] = 'fixture_second=synthetic-two; Path=/; HttpOnly; SameSite=Lax';
  response.writeHead(200, headers);
  response.end('<!doctype html><title>' + titles.get(request.url) + '</title>');
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
}, 25000);
try {
  const [code] = await once(child, 'exit');
  const evidence = await readFile(join(fixtureTemp, 'veil-cef-wpf-profile-' + child.pid, 'tabs.log'), 'utf8').catch(() => '');
  assert.equal(timedOut, false, evidence);
  assert.equal(code, 0, evidence);
  for (const route of titles.keys()) assert.equal(requests.filter(x => x === route).length, 1, route);
  assert.ok(!requests.includes('/tabs-stale'));
  assert.match(evidence, /Fresh fixture cookie scan returned an empty list[\s\S]*Both views returned only the two synthetic cookie domains[\s\S]*Closed view cookie scan identity rejected/);
  assert.match(evidence, /Tab selection preserved both native identities and active address[\s\S]*Closing active tab preserved surviving view and navigation[\s\S]*Tabbed host registry drained/);
  console.log('PASS: same-window tab selection and independent close');
} finally {
  clearTimeout(timer);
  server.close();
}
