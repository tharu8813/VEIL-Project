// SPDX-License-Identifier: MPL-2.0
// Live-session proxy outage/recovery. Optional operator transition while proxy is unavailable.
// Does not modify adapters, sleep the VM, or infer a real OS transition from an acknowledgement.
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { parseArgs, promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { startProxy } from '../integration/proxy.mjs';

const run = promisify(execFile);
const phases = ['before', 'blocked', 'after'];
export function validateTransition(records, directRequests, routes) {
    assert.equal(directRequests, 0, 'direct fixture traffic during live session');
    assert.deepEqual(records.map(x => x.phase), phases);
    const pid = records[0].pid;
    assert.ok(Number.isSafeInteger(pid) && pid > 0);
    for (const [i, record] of records.entries()) {
        const blocked = i === 1;
        assert.equal(record.pid, pid, 'engine changed across transition');
        assert.equal(record.loaded, !blocked);
        assert.equal(record.blocked, blocked);
        assert.equal(record.stored, !blocked);
        assert.equal(record.entries, i === 2 ? 2 : 1);
        if (blocked) assert.ok(record.failureName);
        assert.equal(routes[i].page, blocked ? 0 : 1, 'exact proxy page request count');
        assert.equal(routes[i].vault, blocked ? 0 : 1, 'exact proxy vault request count');
    }
    return { status: 'PASS', scope: 'live loopback HTTP proxy outage/recovery only' };
}

async function stopProxy(proxy) {
    if (!proxy?.listening) return;
    const closed = new Promise((done, fail) => proxy.close(error => error ? fail(error) : done()));
    proxy.closeAllConnections?.();
    await closed;
}

async function main() {
    const { values } = parseArgs({ options: { 'operator-transition': { type: 'string' }, 'internet-vm': { type: 'boolean' } } });
    assert.equal(process.platform, 'win32', 'interactive Windows required');
    const manual = values['operator-transition'];
    if (manual) {
        assert.ok(['sleep-resume', 'network-switch'].includes(manual) && values['internet-vm'], 'explicit isolated internet VM required');
        const ps = join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
        const { stdout } = await run(ps, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File',
            fileURLToPath(new URL('../phase3/phase34-guest-identity.ps1', import.meta.url)), '-Json']);
        const guest = JSON.parse(stdout);
        assert.ok(guest.virtualBox && !guest.elevated && !guest.noDefaultRoute, 'normal user in isolated internet VM required');
    }
    const root = resolve('.veil-dev');
    await mkdir(root, { recursive: true });
    const evidence = await mkdtemp(join(root, 'proxy-transition-'));
    const profile = join(evidence, 'profile');
    await mkdir(profile);
    const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
    const { server, origin, direct, requests } = await startFixture();
    let proxy, child, exit, spawnError;
    const records = [], routes = [];
    const count = (map, path) => map.get(path) ?? 0;
    const directTotal = () => [...direct.values()].reduce((a, b) => a + b, 0);
    const control = directTotal();
    const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^VEIL_(DEV_VAULT|TEST_)/i.test(key)));
    Object.assign(env, { VEIL_TEST_OFFLINE_HOSTS: '1', VEIL_DEV_VAULT_PASSPHRASE: 'synthetic-live-transition-pass',
        VEIL_DEV_VAULT_RECOVERY: 'AAAAA-BBBBB-CCCCC-DDDDD-EEEEE-FFFFF-GGGGG-HHHHH' });
    async function waitFile(path, timeout = 180000) {
        const until = Date.now() + timeout;
        while (Date.now() < until) {
            if (spawnError) throw spawnError;
            if (exit) throw new Error('Desktop exited before ' + path);
            const value = await readFile(path, 'utf8').catch(error => {
                if (error.code !== 'ENOENT') throw error;
                return null;
            });
            if (value !== null) return value;
            await delay(100);
        }
        throw new Error('Timed out: ' + path);
    }
    try {
        const first = await startProxy();
        proxy = first.proxy;
        await writeFile(join(profile, 'browser-state.json'), JSON.stringify({ Bookmarks: [], History: [], Tabs: [], Selected: 0,
            Network: { Mode: 1, ProxyScheme: 'http', ProxyHost: '127.0.0.1', ProxyPort: first.port } }));
        child = spawn('dotnet', [desktop, '--smoke', profile, origin, 'network-transition', join(evidence, 'desktop.json')],
            { env, stdio: 'ignore', windowsHide: true });
        const finished = new Promise(done => {
            child.once('exit', (code, signal) => { exit = { code, signal }; done(); });
            child.once('error', error => { spawnError = error; done(); });
        });
        for (const phase of phases) {
            if (phase === 'blocked') {
                await stopProxy(proxy);
                if (manual) {
                    console.log('OPERATOR: ' + manual + ' with proxy down, then create ' + join(evidence, 'operator.done'));
                    await waitFile(join(evidence, 'operator.done'), 240000);
                }
            }
            if (phase === 'after') {
                const resumed = await startProxy({ listenPort: first.port });
                proxy = resumed.proxy;
            }
            const path = '/network-transition/' + phase;
            const beforePage = count(requests, path), beforeVault = count(requests, '/vault-dl');
            await writeFile(join(evidence, phase + '.go'), 'go', { flag: 'wx' });
            const raw = await waitFile(join(evidence, phase + '.done.json'));
            records.push(JSON.parse(raw));
            routes.push({ page: count(requests, path) - beforePage, vault: count(requests, '/vault-dl') - beforeVault });
        }
        await Promise.race([finished, delay(15000, null, { ref: false }).then(() => { throw new Error('Desktop exit timeout'); })]);
        assert.equal(exit?.code, 0);
        assert.equal(JSON.parse(await readFile(join(evidence, 'desktop.json'), 'utf8')).error, null);
        const verdict = validateTransition(records, directTotal() - control, routes);
        await writeFile(join(evidence, 'summary.json'), JSON.stringify({ ...verdict, records, routes,
            directRequests: directTotal() - control, operator: manual ?? null,
            osTransition: manual ? 'OBSERVED: operator acknowledgement only; independent OS evidence required' : 'NOT RUN',
            externalNetwork: 'NOT RUN' }, null, 2), { flag: 'wx' });
        console.log('PASS live proxy transition · ' + evidence);
    } finally {
        if (child && !exit && !spawnError) child.kill();
        await stopProxy(proxy);
        server.closeAllConnections?.();
        server.close();
    }
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
