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
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:https';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startDoh } from '../integration/doh.mjs';
import { startFixture } from '../integration/fixture.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'dns-protection-'));
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server: fixture, origin } = await startFixture();
const port = new URL(origin).port;

const doh = await startDoh(evidence);
const { template, spki, queried } = doh;

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

    await doh.stop();
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
    doh.server.close();
}
