import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, readdir, readFile, writeFile } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';
import { startFixture } from './fixture.mjs';

const run = promisify(execFile);
const args = process.argv.slice(2);
const crashCycle = args.includes('--crash-cycle');
const [option, location, mode, ...extra] = args.filter(arg => arg !== '--crash-cycle');
if (option !== undefined &&
    (option !== '--profile-parent' || !location || !isAbsolute(location) ||
     (mode !== undefined && mode !== '--lock-cycle') || extra.length !== 0)) {
    throw new Error('Use --crash-cycle, or --profile-parent followed by an existing absolute directory, optionally with --lock-cycle.');
}
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const directory = await mkdtemp(join(root, 'desktop-test-'));
const profileRoot = location ? await mkdtemp(join(location, 'veil-desktop-')) : directory;
const profile = join(profileRoot, 'profile');
const { server, origin, requests } = await startFixture();
const desktop = resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');

async function launch(path, stage) {
    const report = join(directory, `${stage}.json`);
    try {
        await run('dotnet', [desktop, '--smoke', path, origin, stage, report], { timeout: 90000 });
    } catch (error) {
        if (!(await readFile(report).catch(() => null))) throw error;
    }
    return JSON.parse(await readFile(report, 'utf8'));
}

async function waitForSignal(name) {
    const stopAt = Date.now() + 5 * 60_000;
    while (Date.now() < stopAt) {
        try {
            await readFile(join(directory, name));
            return;
        } catch (error) {
            if (error.code !== 'ENOENT') throw error;
        }
        await delay(250);
    }
    throw new Error(`Timed out waiting for ${name}`);
}

async function verifyInterruptedWrite() {
    const state = join(profile, 'browser-state.json');
    const report = join(directory, 'crash.json');
    const child = spawn('dotnet', [desktop, '--smoke', profile, origin, 'crash', report], {
        windowsHide: true,
        stdio: 'ignore',
    });
    const exited = once(child, 'exit');
    exited.catch(() => {});
    async function wait(read) {
        for (let i = 0; i < 120; i++) {
            const result = await read();
            if (result) return result;
            await delay(250);
        }
        throw new Error('Interrupted-write checkpoint timed out');
    }

    try {
        const ready = await wait(() => readFile(report, 'utf8').then(JSON.parse).catch(() => null));
        assert.equal(ready.interruptedStateReady, true, ready.error);
        assert.ok(Number.isInteger(ready.browserPid) && ready.browserPid > 0);
        const committed = await readFile(state);
        assert.ok(child.kill(), 'Could not terminate the synthetic test host');
        const [exitCode, signal] = await exited;
        assert.ok(exitCode !== 0 || signal);
        await wait(() => {
            try { process.kill(ready.browserPid, 0); return false; }
            catch (error) { if (error.code === 'ESRCH') return true; throw error; }
        });
        assert.deepEqual(await readFile(state), committed);
        assert.equal(await readFile(state + '.new', 'utf8'), '{"Bookmarks":');
        const orphan = join(profileRoot, 'orphaned-profile');
        await mkdir(orphan);
        await writeFile(join(orphan, 'browser-state.json.new'), '{"Bookmarks":');
        assert.equal((await launch(orphan, 'orphaned')).error, 'JsonException');
        assert.deepEqual(await readdir(orphan), ['browser-state.json.new', 'profile.lock']);
    } finally {
        if (child.exitCode === null && child.signalCode === null) child.kill();
    }
}

try {
    for (const stage of ['write', 'read']) {
        const result = await launch(profile, stage);
        console.log(stage, result);
        assert.equal(result.error, null);
        if (stage === 'write') {
            assert.ok((await readdir(profile)).includes('browser-state.json'));
            assert.ok((await readdir(join(profile, 'webview2'))).includes('EBWebView'));
            if (crashCycle) await verifyInterruptedWrite();
            if (mode === '--lock-cycle') {
                await writeFile(join(directory, 'ready.json'), JSON.stringify({ profile, origin }));
                console.log('Lock checkpoint', directory);
                await waitForSignal('locked');
                const before = Object.fromEntries(requests);
                const blocked = await launch(profile, 'locked');
                assert.match(blocked.error, /IOException|UnauthorizedAccessException|DirectoryNotFoundException/);
                assert.deepEqual(Object.fromEntries(requests), before, 'locked launch contacted the fixture');
                await writeFile(join(directory, 'locked-checked'), 'Profile startup blocked');
                await waitForSignal('unlocked');
            }
        }
    }
    assert.ok((requests.get('/download') ?? 0) >= 5, 'expected fixture download requests');
    const files = await readdir(profile, { recursive: true, withFileTypes: true });
    assert.deepEqual(files.filter(file => file.isFile() &&
        (file.name.startsWith('veil-phase0-') || file.name.endsWith('.crdownload'))), []);
    const corrupt = join(profileRoot, 'corrupt-profile');
    await mkdir(corrupt);
    await writeFile(join(corrupt, 'browser-state.json'), '{');
    assert.equal((await launch(corrupt, 'corrupt')).error, 'JsonException');
    assert.ok(!(await readdir(corrupt)).includes('webview2'));
    console.log('PASS desktop integration', directory);
} finally {
    server.close();
}
