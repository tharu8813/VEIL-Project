import assert from 'node:assert/strict';
import { once } from 'node:events';
import { execFile, spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const directory = await mkdtemp(join(root, 'browser-crash-'));
const profile = join(directory, 'profile');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server, origin } = await startFixture();
const ready = join(directory, 'before.json');
const recovered = join(directory, 'after.json');
const checks = ['cookie', 'localStorage', 'indexedDB', 'serviceWorker', 'cacheStorage'];
const args = [desktop, '--probe', profile, origin];
const host = spawn('dotnet', [...args, 'crash-write', ready], { windowsHide: true, stdio: 'ignore' });
const hostExit = once(host, 'exit');
// Attach before polling so process startup errors are observed immediately by Node.
hostExit.catch(() => {});

async function until(read, message) {
    for (let attempt = 0; attempt < 100; attempt++) {
        const value = await read();
        if (value) return value;
        await delay(150);
    }
    throw new Error(message);
}

try {
    const before = await until(async () => {
        assert.equal(host.exitCode, null, 'host quit before termination');
        return readFile(ready, 'utf8').then(JSON.parse).catch(() => null);
    }, 'host did not write the pre-termination report');
    assert.ok(!before.error, before.error);
    for (const field of checks) assert.equal(before[field], true, field);
    assert.ok(Number.isInteger(before.browserPid) && before.browserPid > 0);
    assert.ok(host.kill(), 'unable to terminate the synthetic host');
    const [exitCode, exitSignal] = await hostExit;
    assert.ok(exitCode !== 0 || exitSignal, 'host did not terminate abnormally');
    await until(() => {
        try { process.kill(before.browserPid, 0); return false; }
        catch (error) { if (error.code === 'ESRCH') return true; throw error; }
    }, 'browser process did not exit after host termination');

    await run('dotnet', [...args, 'read', recovered], { timeout: 45000 }).catch(async error => {
        if (!(await readFile(recovered).catch(() => null))) throw error;
    });
    const after = JSON.parse(await readFile(recovered, 'utf8'));
    await writeFile(join(directory, 'evidence.json'), JSON.stringify({ before, exitCode, exitSignal, after }, null, 2));
    console.log({ after, exitCode, exitSignal, directory });
    assert.ok(!after.error, after.error);
    for (const field of checks) assert.equal(after[field], true, field);
    console.log('PASS site data after forced WebView2 host termination');
} finally {
    if (host.exitCode === null && host.signalCode === null) host.kill();
    server.close();
}
