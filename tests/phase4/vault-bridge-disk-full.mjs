// SPDX-License-Identifier: MPL-2.0
// ADR-0010 C2: actual ENOSPC through the desktop HTTP/pipe vault bridge.
// Only the separately preflighted small VHDX may be filled.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { isAbsolute, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { parseArgs, promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { fillToReserve, verifiedDiskFullLab, verifyLabMount } from './vault-disk-full.mjs';

const { values } = parseArgs({ options: { 'profile-parent': { type: 'string' },
    'browser-profile-parent': { type: 'string' }, preflight: { type: 'string' } } });
const smallParent = values['profile-parent'], browserParent = values['browser-profile-parent'];
assert.ok(browserParent && isAbsolute(browserParent), 'verified separate browser lab required');
const { primary, diskFull, capacity } = await verifiedDiskFullLab(smallParent, values.preflight);
await verifyLabMount(browserParent, primary);
const desktop = process.env.VEIL_DESKTOP_DLL;
assert.ok(desktop && isAbsolute(desktop), 'prebuilt Debug desktop DLL required');
const evidence = await mkdtemp(join(browserParent, 'vault-bridge-disk-full-'));
const disposable = await mkdtemp(join(smallParent, 'vault-bridge-full-'));
const smallRoot = join(disposable, 'synthetic');
await mkdir(smallRoot);
const filler = join(disposable, 'filler.bin');
const report = join(evidence, 'bridge.json');
const profile = join(evidence, 'profile');
const { server, origin } = await startFixture();
const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^VEIL_(DEV_VAULT|TEST_)/i.test(name)));
Object.assign(env, {
    VEIL_DEV_VAULT_PASSPHRASE: 'synthetic-bridge-disk-full-pass',
    VEIL_DEV_VAULT_RECOVERY: 'AAAAA-BBBBB-CCCCC-DDDDD-EEEEE-FFFFF-GGGGG-HHHHH',
    VEIL_TEST_DISK_FULL_VAULT: smallRoot, VEIL_TEST_OFFLINE_HOSTS: '1',
});
let settled = false;
const execution = promisify(execFile)('dotnet', [desktop, '--smoke', profile, origin, 'vault-bridge-disk-full', report],
    { timeout: 480_000, env, maxBuffer: 2 * 1024 ** 2 }).then(result => ({ ...result, code: 0 }), error => error)
    .finally(() => { settled = true; });
async function waitSignal(name, timeout = 150_000) {
    const stop = Date.now() + timeout;
    while (!settled && Date.now() < stop) {
        if (await readFile(join(evidence, name), 'utf8').then(value => value.length > 0, () => false)) return;
        await delay(100);
    }
    throw new Error('Desktop did not reach ' + name);
}
const result = { scope: 'desktop HTTP/pipe vault bridge, real disk full; not installed service, engine download event or power loss',
    capacity, status: 'FAIL' };
try {
    await waitSignal('seed-ready');
    await verifyLabMount(smallParent, diskFull);
    result.fill = await fillToReserve(smallParent, filler, capacity);
    await writeFile(join(evidence, 'filled'), 'filled', { flag: 'wx' });
    await waitSignal('failure-checked');
    await rm(filler);
    await writeFile(join(evidence, 'released'), 'released', { flag: 'wx' });
    const done = await execution;
    const desktopResult = JSON.parse(await readFile(report, 'utf8'));
    assert.equal(desktopResult.error, null, desktopResult.error);
    assert.equal(done.code, 0, 'desktop failed after disk-full test');
    const vault = JSON.parse(await readFile(join(evidence, 'bridge.vault.json'), 'utf8'));
    assert.equal(vault.entries.length, 2);
    assert.equal(vault.originalUnchanged, true);
    result.status = 'PASS';
    console.log('PASS real disk-full through desktop vault bridge · evidence ' + evidence);
} catch (error) {
    result.error = error.message;
    throw error;
} finally {
    // On a normal failure, free the volume before allowing the desktop to exit.
    try {
        await rm(filler, { force: true });
        await writeFile(join(evidence, 'released'), 'released', { flag: 'wx' }).catch(error => {
            if (error.code !== 'EEXIST') throw error;
        });
    } finally {
        server.closeAllConnections?.();
        server.close();
        const done = await execution;
        await writeFile(join(evidence, 'desktop.log'), String(done.stdout ?? '') + String(done.stderr ?? ''));
        await writeFile(join(evidence, 'summary.json'), JSON.stringify(result, null, 2));
    }
}
