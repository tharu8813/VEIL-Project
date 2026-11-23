// Phase 3 / Gate B: kill the WebView2 browser process while IndexedDB and localStorage commits are
// in flight, repeatedly, and require every restart to find the stored records readable and gap-free.
//
//   node tests/phase3/engine-kill.mjs [--profile-parent <existing dir>] [--cycles 5]
//
// Also checks that VEIL notices the lost engine and exits on its own (profile lock released), and
// that a final normal start/close still works.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { startFixture } from '../integration/fixture.mjs';

const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const parent = option('--profile-parent');
const cycles = Number(option('--cycles') ?? 5);
if (parent !== undefined && !isAbsolute(parent)) throw new Error('--profile-parent must be an absolute existing directory');

const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'engine-kill-'));
const profileRoot = parent ? await mkdtemp(join(parent, 'veil-engine-kill-')) : evidence;
const profile = join(profileRoot, 'profile');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server, origin } = await startFixture();

async function stage(name, report, onReady) {
    const child = spawn('dotnet', [desktop, '--smoke', profile, origin, name, report], { windowsHide: true, stdio: 'ignore' });
    const exited = once(child, 'exit');
    exited.catch(() => {});
    try {
        if (onReady) {
            for (let i = 0; ; i++) {
                const ready = await readFile(report, 'utf8').then(JSON.parse).catch(() => null);
                if (ready?.stormReady) { await onReady(ready); break; }
                if (ready?.error !== undefined && ready.error !== null) throw new Error(`${name}: ${ready.error}`);
                if (i > 600) throw new Error(`${name}: writer never became ready`);
                await delay(100);
            }
        }
        const [code] = await Promise.race([exited, delay(90_000).then(() => { throw new Error(`${name}: VEIL did not exit`); })]);
        const result = JSON.parse(await readFile(report, 'utf8'));
        return { code, ...result };
    } finally {
        if (child.exitCode === null && child.signalCode === null) child.kill();
    }
}

const summary = [];
try {
    for (let cycle = 1; cycle <= cycles; cycle++) {
        const report = join(evidence, `storm-${cycle}.json`);
        let killed = null;
        const result = await stage('storm', report, async ready => {
            await delay(250 + cycle * 150); // vary where in the commit stream the kill lands
            process.kill(ready.browserPid); // TerminateProcess on the engine's browser process
            killed = ready;
        });
        assert.ok(killed, 'engine was not killed');
        assert.equal(result.error, null, `cycle ${cycle}: ${result.error}`);
        assert.ok(result.checks.includes('engine loss detected'), `cycle ${cycle}: engine loss not detected`);
        const stored = result.checks.find(check => check.startsWith('storm records contiguous'));
        summary.push({ cycle, before: killed.before, killedAfterAtLeast: killed.before + 100, stored, code: result.code });
        console.log(`cycle ${cycle}: PASS · ${stored} · engine killed after >= ${killed.before + 100}`);
    }
    const final = await stage('storm-check', join(evidence, 'storm-check.json'));
    assert.equal(final.error, null, `final check: ${final.error}`);
    const stored = final.checks.find(check => check.startsWith('storm records contiguous'));
    summary.push({ cycle: 'final', stored, code: final.code });
    console.log(`final: PASS · ${stored}`);
    await writeFile(join(evidence, 'summary.json'), JSON.stringify({ profileRoot, cycles, summary }, null, 2));
    console.log('PASS engine kill during storage commits', evidence);
} finally {
    server.close();
}
