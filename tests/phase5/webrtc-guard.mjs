// ADR-0006 proxy protection: WebRTC must not gather any ICE candidate (ADR-0010 D2).
//
//   node tests/phase5/webrtc-guard.mjs
//
// Loopback only (no STUN server, nothing leaves the machine). The fixture page /webrtc gathers candidates.
//   control:  standard protection gathers at least one UDP host candidate (proves gathering works)
//   proxy:    proxy protection gathers zero ICE candidates, and the page itself came through the proxy
// Found 2026-11-24: the runtime ignores --force-webrtc-ip-handling-policy; only --webrtc-ip-handling-policy works.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { startProxy } from '../integration/proxy.mjs';
import { candidate, judgeProxyCandidates } from '../vm/internet-batch.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'webrtc-guard-'));
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server, origin, direct } = await startFixture();
const { proxy, seen, port } = await startProxy();

async function candidates(label, network) {
    const profile = join(evidence, label, 'profile');
    await mkdir(profile, { recursive: true });
    await writeFile(join(profile, 'browser-state.json'), JSON.stringify({ Bookmarks: [], History: [], Tabs: [], Selected: 0, Network: network }));
    const report = join(evidence, `${label}.json`);
    await run('dotnet', [desktop, '--smoke', profile, origin, 'visit', report], {
        timeout: 180_000, env: { ...process.env, VEIL_TEST_URL: `${origin}/webrtc`, VEIL_TEST_EVAL: 'window.veilCandidates' },
    });
    const result = JSON.parse(await readFile(report, 'utf8'));
    assert.equal(result.error, null, `${label}: ${result.error}`);
    assert.ok(result.checks.includes('visit: error=none') && result.checks.includes('visit: url=' + origin + '/webrtc'), 'fixture navigation must succeed');
    return JSON.parse(result.checks.find(c => c.startsWith('visit: eval=')).slice('visit: eval='.length));
}

try {
    const control = await candidates('standard', null);
    assert.ok(Array.isArray(control) && control.every(line => candidate(line).transport) && control.some(line => candidate(line).transport === 'udp'),
        `control gathered no UDP candidate (${JSON.stringify(control)}); the check would prove nothing`);
    console.log(`control (standard): ${control.length} candidate(s), UDP present`);
    const before = direct.get('/webrtc') ?? 0;
    const proxied = await candidates('proxy', { Mode: 1, ProxyScheme: 'http', ProxyHost: '127.0.0.1', ProxyPort: port });
    const verdict = judgeProxyCandidates(proxied);
    assert.equal(verdict.status, 'PASS', verdict.detail);
    assert.ok(seen.some(path => path === '/webrtc'), 'page did not come through the proxy');
    assert.equal(direct.get('/webrtc') ?? 0, before, 'direct fixture request in proxy mode');
    console.log(`proxy protection: ${verdict.detail}`);
    console.log(`PASS webrtc guard · evidence ${evidence}`);
} finally {
    proxy.close();
    server.close();
}
