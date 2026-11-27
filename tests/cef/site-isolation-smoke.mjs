// SPDX-License-Identifier: MPL-2.0
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { resolve, dirname } from 'node:path';
import http from 'node:http';

assert.equal(process.platform, 'win32');
assert.equal(process.argv.length, 3, 'Pass the built veil-cef-smoke.exe');
const executable = resolve(process.argv[2]);
const requests = [];
const server = http.createServer((request, response) => {
  const port = server.address().port;
  const first = '127.0.0.1:' + port;
  const second = 'localhost:' + port;
  if (request.method === 'GET' && request.headers.host === first && request.url === '/site-parent') {
    requests.push('parent');
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    response.end(`<!doctype html><body><script>
      Promise.all(['/site-same', 'http://${second}/site-cross'].map(url =>
        new Promise(resolve => {
          const frame = document.createElement('iframe');
          frame.onload = resolve;
          frame.src = url;
          document.body.append(frame);
        }))).then(() => document.title = 'VEIL_CEF_SITE_READY');
      </script></body>`);
  } else if (request.method === 'GET' &&
      ((request.headers.host === first && request.url === '/site-same') ||
       (request.headers.host === second && request.url === '/site-cross'))) {
    requests.push(request.url);
    response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
    response.end('<!doctype html><title>Synthetic fixture frame</title>');
  } else {
    response.writeHead(404).end();
  }
});

server.listen(0, '127.0.0.1');
await once(server, 'listening');
try {
  const child = spawn(executable, [], {
    cwd: dirname(executable),
    env: { ...process.env, VEIL_CEF_SITE_SMOKE_PORT: String(server.address().port) },
    stdio: 'ignore',
    windowsHide: true,
  });
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    if (child.exitCode === null && child.pid) {
      const killed = spawnSync('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], {
        windowsHide: true, timeout: 10000,
      });
      if (killed.status !== 0) child.kill();
    }
  }, 30000);
  try {
    const [code] = await once(child, 'exit');
    assert.equal(timedOut, false, 'Site-isolation fixture timed out after requests: ' + requests.join(', '));
    assert.deepEqual(requests.sort(), ['/site-cross', '/site-same', 'parent']);
    assert.equal(code, 0, 'Expected one cross-site OOPIF and no same-site OOPIF');
    console.log('PASS: cross-site iframe had a CDP iframe target and same-site iframe did not');
  } finally {
    clearTimeout(timer);
  }
} finally {
  server.close();
}
