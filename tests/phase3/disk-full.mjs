// Phase 3 / Gate B: run VEIL with the profile volume completely full, then free the space and
// require the profile (app state + engine storage) to open intact. Run against a small dedicated
// test volume only, e.g. in the isolated VM:
//
//   node tests/phase3/disk-full.mjs --profile-parent W:\VEIL-Full
//
// The filler file is created next to the profile on that volume and removed at the end.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, open, readFile, rm, statfs } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { startFixture } from '../integration/fixture.mjs';

const args = process.argv.slice(2);
const parent = args[args.indexOf('--profile-parent') + 1];
if (!args.includes('--profile-parent') || !parent || !isAbsolute(parent))
    throw new Error('Use --profile-parent <existing directory on a small, dedicated test volume>.');
const volume = await statfs(parent);
const capacity = volume.blocks * volume.bsize;
if (capacity > 2 * 1024 ** 3) throw new Error(`Refusing to fill a ${Math.round(capacity / 1024 ** 3)} GiB volume; use a small test volume.`);

const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'disk-full-'));
const profileRoot = await mkdtemp(join(parent, 'veil-disk-full-'));
const profile = join(profileRoot, 'profile');
const filler = join(profileRoot, 'filler.bin');
const desktop = resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server, origin } = await startFixture();

async function stage(name, report, { killWhenReady = false, timeout = 120_000 } = {}) {
    const child = spawn('dotnet', [desktop, '--smoke', profile, origin, name, report], { windowsHide: true, stdio: 'ignore' });
    const exited = once(child, 'exit');
    exited.catch(() => {});
    let timedOut = false;
    try {
        const stopAt = Date.now() + timeout;
        while (child.exitCode === null && Date.now() < stopAt) {
            const ready = killWhenReady && await readFile(report, 'utf8').then(JSON.parse).catch(() => null);
            if (ready?.stormReady) { process.kill(ready.browserPid); killWhenReady = false; }
            await delay(200);
        }
        if (child.exitCode === null) { timedOut = true; child.kill(); }
        const [code] = await exited;
        const result = await readFile(report, 'utf8').then(JSON.parse).catch(() => ({ error: 'no report' }));
        return { code, timedOut, ...result };
    } finally {
        if (child.exitCode === null && child.signalCode === null) child.kill();
    }
}

async function fill() {
    const handle = await open(filler, 'w');
    const chunk = Buffer.alloc(1024 * 1024, 0x5a);
    let written = 0;
    try {
        for (;;) {
            try { await handle.write(chunk); written += chunk.length; }
            catch (error) {
                if (error.code !== 'ENOSPC') throw error;
                // Top off with small writes until not even 4 KiB fits.
                const small = Buffer.alloc(4096, 0x5a);
                for (;;) { try { await handle.write(small); written += small.length; } catch { break; } }
                break;
            }
        }
    } finally { await handle.close().catch(() => {}); }
    return written;
}

const log = [];
try {
    const seed = await stage('storm', join(evidence, 'seed.json'), { killWhenReady: true });
    assert.equal(seed.error, null, `seed: ${seed.error}`);
    const seeded = seed.checks.find(check => check.startsWith('storm records contiguous'));
    log.push({ step: 'seed', seeded });
    const state = await readFile(join(profile, 'browser-state.json'), 'utf8');

    const filled = await fill();
    const free = (await statfs(parent)).bavail * volume.bsize;
    log.push({ step: 'fill', filledMiB: Math.round(filled / 1024 ** 2), freeBytes: free });
    console.log(`filled ${Math.round(filled / 1024 ** 2)} MiB, ${free} bytes free`);

    // With no space VEIL may refuse to start, fail to open the engine, or fail writes; any of these is
    // acceptable as long as it ends (on its own or after the harness stops it) without damaging the profile.
    const full = await stage('storm', join(evidence, 'full.json'), { killWhenReady: true, timeout: 90_000 });
    log.push({ step: 'full', code: full.code, timedOut: full.timedOut, error: full.error ?? null });
    console.log(`while full: exit=${full.code} timedOut=${full.timedOut} error=${full.error ?? null}`);
    assert.equal(await readFile(join(profile, 'browser-state.json'), 'utf8').then(text => JSON.parse(text) && 'ok'), 'ok',
        'app state unreadable after the disk-full run');

    await rm(filler, { force: true });
    const recovered = await stage('storm-check', join(evidence, 'recovered.json'));
    assert.equal(recovered.error, null, `after freeing space: ${recovered.error}`);
    const stored = recovered.checks.find(check => check.startsWith('storm records contiguous'));
    log.push({ step: 'recovered', stored, stateUnchanged: (await readFile(join(profile, 'browser-state.json'), 'utf8')) === state });
    console.log(`recovered: PASS · ${stored}`);
    await (await open(join(evidence, 'summary.json'), 'w')).writeFile(JSON.stringify({ profileRoot, capacity, log }, null, 2));
    console.log('PASS disk full and recovery', evidence);
} finally {
    await rm(filler, { force: true });
    server.close();
}
