import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const directory = await mkdtemp(join(root, 'proxy-'));
const ipv6 = process.argv.includes('--ipv6');
const family = ipv6 ? 'IPv6' : 'IPv4';
const { server: fixture, origin, requests } = await startFixture(0, { ipv6 });
let proxied = 0;
const proxy = createServer((request, response) => {
    response.setHeader('Connection', 'close');
    if (request.url !== origin + '/proxy-up') return response.writeHead(403).end();
    proxied++;
    proxy.close();
    response.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' }).end('VEIL_PROXY_ONLY');
});
proxy.on('connect', (_, socket) => socket.destroy());

try {
    await new Promise((resolve, reject) => proxy.once('error', reject).listen(0, '127.0.0.1', resolve));
    const desktop = 'apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll';
    const control = await probe(
        ['--probe', join(directory, 'control-profile'), origin, 'network-control'],
        join(directory, 'control.json'),
    );
    const result = await probe(
        ['--probe-proxy', join(directory, 'profile'), origin, String(proxy.address().port)],
        join(directory, 'evidence.json'),
    );
    const directRequests = (requests.get('/proxy-up') ?? 0) + (requests.get('/proxy-down') ?? 0);
    console.log({ family, control, ...result, proxied, directRequests, directory });
    assert.equal(control.error, undefined);
    assert.equal(control.directResponse, true);
    assert.equal(requests.get('/network-control'), 1);
    assert.equal(result.error, undefined);
    assert.equal(result.proxyResponse, true);
    assert.equal(proxied, 1);
    assert.equal(result.navigatedDuringOutage, false);
    assert.equal(directRequests, 0);
    console.log(`PASS ${family} loopback HTTP proxy outage with a direct origin control`);

    async function probe(args, report) {
        await run('dotnet', [desktop, ...args, report], { timeout: 45000 }).catch(async error => {
            if (!(await readFile(report).catch(() => null))) throw error;
        });
        return JSON.parse(await readFile(report, 'utf8'));
    }
} finally {
    proxy.close();
    fixture.close();
}
