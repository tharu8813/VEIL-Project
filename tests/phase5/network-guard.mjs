// Phase 5 / ADR-0006: network guard checks that run on a normal Windows desktop.
//
//   node tests/phase5/network-guard.mjs
//
// 1. Standard mode: HTTPS-first rules and warning page, tracking prevention, proxy-setting validation.
// 2. Proxy mode with a local forward proxy that marks what it forwards: browsing and VEIL's vault
//    request go through it, and the engine runs with the proxy, WebRTC and QUIC switches.
// 3. Proxy stopped: browsing and the vault request fail; nothing reaches the origin directly.
// DNS, external IPv6, real WebRTC candidates, sleep/resume and OS-level bypass stay NOT RUN (VM).
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { startProxy } from '../integration/proxy.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'network-guard-'));
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const vaultEnv = { ...process.env, VEIL_DEV_VAULT_PASSPHRASE: 'synthetic-network-pass', VEIL_DEV_VAULT_RECOVERY: 'NETWA-NETWB-NETWC-NETWD-NETWE-NETWF-NETWG-NETWH' };
const { server: fixture, origin, direct } = await startFixture();

async function stage(profile, name, report, watch) {
    const child = spawn('dotnet', [desktop, '--smoke', profile, origin, name, report], { env: vaultEnv, windowsHide: true, stdio: 'ignore' });
    const exited = once(child, 'exit');
    let commandLine = null;
    if (watch) {
        // The engine's browser process (no --type) for this profile, while the stage runs.
        for (let i = 0; i < 60 && !commandLine && child.exitCode === null; i++) {
            const { stdout } = await run('powershell', ['-NoProfile', '-Command',
                "Get-CimInstance Win32_Process -Filter \"Name='msedgewebview2.exe'\" | Select-Object -ExpandProperty CommandLine"]).catch(() => ({ stdout: '' }));
            commandLine = stdout.split(/\r?\n/).find(line => line.includes(profile) && !line.includes('--type=')) ?? null;
            if (!commandLine) await delay(500);
        }
    }
    await Promise.race([exited, delay(240_000).then(() => { child.kill(); throw new Error(`${name} timed out`); })]);
    return { ...JSON.parse(await readFile(report, 'utf8')), commandLine };
}

const directTotal = () => [...direct.values()].reduce((sum, n) => sum + n, 0);

try {
    // 1. Standard protection.
    const standard = await stage(join(evidence, 'standard', 'profile'), 'network', join(evidence, 'standard.json'));
    assert.equal(standard.error, null, standard.error);
    console.log(`standard: PASS · ${standard.checks.filter(c => /https-first|tracking|proxy|script/.test(c)).length} policy checks`);

    // 2. Proxy mode, proxy up.
    const { proxy, seen, port } = await startProxy();
    const profile = join(evidence, 'proxy', 'profile');
    await mkdir(profile, { recursive: true });
    await writeFile(join(profile, 'browser-state.json'), JSON.stringify({
        Bookmarks: [], History: [], Tabs: [], Selected: 0,
        Network: { Mode: 1, ProxyScheme: 'http', ProxyHost: '127.0.0.1', ProxyPort: port },
    }));
    const directBefore = directTotal();
    const up = await stage(profile, 'network-proxy', join(evidence, 'proxy-up.json'), true);
    assert.equal(up.error, null, up.error);
    const check = prefix => up.checks.find(c => c.startsWith(prefix));
    assert.match(check('indicator:'), /경로 확인됨/);
    assert.match(check('page title:'), /두 번째 페이지/, 'page did not load through the proxy');
    assert.equal(check('vault download:'), 'vault download: stored 1024');
    assert.ok(seen.includes('/second') && seen.includes('/vault-dl'), `proxy saw: ${seen.join(', ')}`);
    assert.equal(directTotal(), directBefore, `direct requests reached the origin: ${JSON.stringify([...direct])}`);
    assert.ok(up.commandLine, 'engine browser process not observed');
    for (const flag of [`--proxy-server=http://127.0.0.1:${port}`, '--proxy-bypass-list=<-loopback>',
        '--webrtc-ip-handling-policy=disable_non_proxied_udp', '--disable-quic'])
        assert.ok(up.commandLine.includes(flag), `engine command line lacks ${flag}`);
    console.log(`proxy up: PASS · page and vault download via proxy (${seen.length} proxied), 0 direct, engine switches present`);

    // 3. Proxy stopped: fail closed.
    await new Promise(done => proxy.close(done));
    proxy.closeAllConnections?.();
    const down = await stage(profile, 'network-proxy', join(evidence, 'proxy-down.json'));
    assert.equal(down.error, null, down.error);
    const downCheck = prefix => down.checks.find(c => c.startsWith(prefix));
    assert.match(downCheck('indicator:'), /차단됨/);
    assert.doesNotMatch(downCheck('page title:'), /두 번째 페이지/, 'page loaded without the proxy');
    assert.match(downCheck('vault download:'), /^vault download: failed/);
    assert.equal(directTotal(), directBefore, `direct requests after proxy stop: ${JSON.stringify([...direct])}`);
    console.log('proxy down: PASS · page and vault download blocked, 0 direct requests');

    await writeFile(join(evidence, 'summary.json'), JSON.stringify({ standard: standard.checks, up: up.checks, down: down.checks, proxied: seen }, null, 2));
    console.log(`PASS network guard · evidence ${evidence}`);
} finally {
    fixture.close();
}
