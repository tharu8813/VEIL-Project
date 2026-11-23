// Phase 5 / ADR-0006: secure DNS (DoH) through the engine's secure-DNS preferences.
//
//   node tests/phase5/dns-protection.mjs        (needs openssl on PATH, e.g. Git for Windows)
//
// A local DoH responder (throwaway self-signed certificate, trusted only through the Debug-only
// VEIL_TEST_TRUSTED_SPKI hook) answers synthetic *.test names with 127.0.0.1 and records every query.
//   1. Secure DNS on: a synthetic name loads the fixture page only via DoH; VEIL wrote the setting.
//   2. Responder stopped: resolution fails (no fallback to the system resolver).
//   3. Windows DNS client cache: the synthetic names never appear (a system-DNS control run is reported).
// Real public resolvers, DoH bootstrap of the provider host, and proxy mode stay NOT RUN (VM/internet).
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { X509Certificate, createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:https';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'dns-protection-'));
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server: fixture, origin } = await startFixture();
const port = new URL(origin).port;

// Throwaway certificate for 127.0.0.1 and its SPKI pin for the test-only trust hook.
const key = join(evidence, 'doh-key.pem'), cert = join(evidence, 'doh-cert.pem');
await run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '1',
    '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1']);
const pem = await readFile(cert);
const spki = createHash('sha256').update(new X509Certificate(pem).publicKey.export({ type: 'spki', format: 'der' })).digest('base64');

// Minimal RFC 8484 responder: A for *.test -> 127.0.0.1, everything else NOERROR/NXDOMAIN without answers.
const queried = [];
function answer(query) {
    let offset = 12;
    const labels = [];
    while (query[offset] !== 0) { labels.push(query.subarray(offset + 1, offset + 1 + query[offset]).toString('ascii')); offset += query[offset] + 1; }
    const name = labels.join('.').toLowerCase();
    const type = query.readUInt16BE(offset + 1);
    queried.push(`${name}/${type}`);
    const question = query.subarray(12, offset + 5);
    const ours = name.endsWith('.test');
    const header = Buffer.alloc(12);
    query.copy(header, 0, 0, 2); // id
    header.writeUInt16BE(ours ? 0x8180 : 0x8183, 2); // response, RD+RA, NOERROR / NXDOMAIN
    header.writeUInt16BE(1, 4);
    const records = ours && type === 1
        ? Buffer.from([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 127, 0, 0, 1]) : Buffer.alloc(0);
    header.writeUInt16BE(records.length ? 1 : 0, 6);
    return Buffer.concat([header, question, records]);
}
const keyPem = await readFile(key);
const resolver = createServer({ key: keyPem, cert: pem }, (request, response) => {
    const send = query => {
        try { response.writeHead(200, { 'Content-Type': 'application/dns-message', 'Cache-Control': 'max-age=0' }).end(answer(query)); }
        catch { response.writeHead(400).end(); }
    };
    if (request.method === 'GET') {
        const dns = new URL(request.url, 'https://127.0.0.1').searchParams.get('dns') ?? '';
        return send(Buffer.from(dns.replace(/-/g, '+').replace(/_/g, '/'), 'base64'));
    }
    const chunks = [];
    request.on('data', chunk => chunks.push(chunk)).on('end', () => send(Buffer.concat(chunks)));
});
await new Promise(done => resolver.listen(0, '127.0.0.1', done));
const template = `https://127.0.0.1:${resolver.address().port}/dns-query`;

async function probe(label, dnsTemplate, host) {
    const profile = join(evidence, label, 'profile');
    await mkdir(profile, { recursive: true });
    await writeFile(join(profile, 'browser-state.json'), JSON.stringify({ Bookmarks: [], History: [], Tabs: [], Selected: 0,
        Network: dnsTemplate ? { Mode: 0, DnsTemplate: dnsTemplate } : null }));
    const report = join(evidence, `${label}.json`);
    await run('dotnet', [desktop, '--smoke', profile, origin, 'dns-probe', report], { timeout: 180_000, env: {
        ...process.env, VEIL_TEST_DNS_URL: `http://${host}:${port}/second`, VEIL_TEST_TRUSTED_SPKI: spki,
    } }).catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
    const result = JSON.parse(await readFile(report, 'utf8'));
    assert.equal(result.error, null, `${label}: ${result.error}`);
    const line = result.checks.find(check => check.startsWith('dns probe:'));
    const localState = JSON.parse(await readFile(join(profile, 'webview2', 'EBWebView', 'Local State'), 'utf8').catch(() => '{}'));
    return { line, localState };
}

async function osCacheHas(host) {
    const { stdout } = await run('powershell', ['-NoProfile', '-Command',
        `@(Get-DnsClientCache -ErrorAction SilentlyContinue | Where-Object Entry -eq '${host}').Count`]);
    return Number(stdout.trim()) > 0;
}

try {
    const viaDoh = `veil-doh-${randomUUID().slice(0, 8)}.test`;
    const on = await probe('doh-on', template, viaDoh);
    assert.match(on.line, /error=none/, on.line);
    assert.match(on.line, /두 번째 페이지/, on.line);
    assert.ok(queried.some(q => q.startsWith(`${viaDoh}/`)), `responder never saw ${viaDoh}`);
    assert.deepEqual(on.localState.dns_over_https, { mode: 'secure', templates: template }, 'VEIL did not apply secure DNS');
    assert.equal(await osCacheHas(viaDoh), false, 'name also resolved by the system resolver');
    console.log(`secure DNS: PASS · ${viaDoh} resolved only by the local DoH responder`);

    await new Promise(done => resolver.close(done));
    resolver.closeAllConnections?.();
    const blocked = `veil-doh-${randomUUID().slice(0, 8)}.test`;
    const off = await probe('doh-down', template, blocked);
    assert.match(off.line, /error=HostNameNotResolved/, off.line);
    assert.equal(await osCacheHas(blocked), false, 'fell back to the system resolver');
    console.log('resolver down: PASS · name not resolved, no system-resolver fallback');

    const control = `veil-doh-${randomUUID().slice(0, 8)}.test`;
    const system = await probe('system-dns', null, control);
    const controlCached = await osCacheHas(control);
    console.log(`control (system DNS): ${system.line} · in Windows DNS cache: ${controlCached}`);

    const others = [...new Set(queried.filter(q => !q.startsWith('veil-doh-')))];
    await writeFile(join(evidence, 'summary.json'), JSON.stringify({ template, queried, others, controlCached }, null, 2));
    console.log(`other names the engine asked the DoH server for: ${others.length ? others.join(', ') : 'none'}`);
    console.log(`PASS secure DNS · evidence ${evidence}`);
} finally {
    fixture.close();
    resolver.close();
}
