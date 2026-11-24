// SPDX-License-Identifier: MPL-2.0
// C2: operator-driven VM cut at the Rust unit-test rotation checkpoint, never a product hook.
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { lstat, mkdir, open, readFile, realpath } from 'node:fs/promises';
import { isAbsolute, join, win32 } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs, promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';
import { validatePreflight } from '../phase3/phase34-vm.mjs';
import { validatePaths } from '../phase3/power-loss.mjs';

const run = promisify(execFile);
const repo = fileURLToPath(new URL('../../', import.meta.url));
const files = ['objects/1234567890abcdef1234567890abcdef.veil',
    'meta/1234567890abcdef1234567890abcdef.veil', 'index/202611250102030123456.veil'];
const phases = ['before-marker', 'after-marker'];
const normal = path => win32.normalize(path).toLowerCase();
const hash = async path => createHash('sha256').update(await readFile(path)).digest('hex');

export function validateRotationReceipt(armed, current, confirmed) {
    assert.equal(confirmed, true, 'Operator power-cut confirmation required');
    assert.equal(armed.schema, 1);
    assert.equal(armed.kind, 'rotation');
    assert.equal(armed.uuid, current.uuid);
    assert.ok(armed.boot && current.boot && armed.boot !== current.boot, 'Same boot');
    assert.ok(phases.includes(armed.phase));
    for (const key of ['phase', 'testSha256']) assert.equal(armed[key], current[key]);
    for (const key of ['volumeId', 'image', 'profile', 'evidence']) assert.equal(normal(armed[key]), normal(current[key]));
    assert.deepEqual(Object.keys(armed.originals).sort(), [...files].sort());
    for (const value of Object.values(armed.originals)) assert.match(value, /^[a-f0-9]{64}$/);
}

async function canonical(path) {
    assert.equal(normal(await realpath(path)), normal(path), 'Redirected lab path');
    assert.ok(!(await lstat(path)).isSymbolicLink());
}
async function writeNew(path, data) {
    const handle = await open(path, 'wx');
    try { await handle.writeFile(JSON.stringify(data, null, 2)); await handle.sync(); }
    finally { await handle.close(); }
}
async function exists(path) {
    return lstat(path).then(() => true, error => {
        if (error.code !== 'ENOENT') throw error;
        return false;
    });
}

async function main() {
    const { values: v } = parseArgs({ options: {
        arm: { type: 'boolean' }, check: { type: 'boolean' }, 'isolated-vm': { type: 'boolean' },
        'confirm-power-cut': { type: 'boolean' }, preflight: { type: 'string' }, phase: { type: 'string' },
        evidence: { type: 'string' }, 'test-exe': { type: 'string' },
    } });
    assert.ok(Boolean(v.arm) !== Boolean(v.check), 'Choose arm or check');
    assert.ok(process.platform === 'win32' && v['isolated-vm'] && v.preflight, 'Verified offline VM required');
    assert.ok(phases.includes(v.phase), 'Choose before-marker or after-marker');
    const exe = v['test-exe'], evidence = v.evidence;
    assert.ok(exe && evidence && isAbsolute(exe) && isAbsolute(evidence), 'Absolute test executable and evidence paths required');
    const profile = join(evidence, 'profile');
    const receipt = JSON.parse((await readFile(v.preflight, 'utf8')).replace(/^\uFEFF/, ''));
    const ps = join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    const { stdout } = await run(ps, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File',
        join(repo, 'tests/phase3/phase34-guest-identity.ps1'), '-Json']);
    const guest = JSON.parse(stdout);
    const { primary } = validatePreflight(receipt, guest);
    validatePaths(primary, evidence, profile);
    await canonical(primary.parent);
    const mounted = await run(join(process.env.SystemRoot, 'System32', 'mountvol.exe'), [primary.drive + '\\', '/L']);
    assert.equal(mounted.stdout.trim().toLowerCase(), primary.volumeId.toLowerCase());
    const identity = { schema: 1, kind: 'rotation', uuid: guest.uuid, boot: guest.boot, volumeId: primary.volumeId,
        image: primary.image, profile, evidence, phase: v.phase, testSha256: await hash(exe) };
    const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^VEIL_UNIT_ROTATION_/i.test(name)));
    Object.assign(env, { VEIL_UNIT_ROTATION_ROOT: profile, VEIL_UNIT_ROTATION_PHASE: v.phase });
    async function fixture(mode, expected) {
        const result = await run(exe, ['--exact', 'vault_generation::tests::rotation_vm_fixture', '--nocapture'],
            { timeout: 60_000, env: { ...env, VEIL_UNIT_ROTATION_FIXTURE: mode }, windowsHide: true });
        assert.ok(result.stdout.split(/\r?\n/).includes(expected), 'Missing explicit fixture execution evidence');
    }
    const readyPath = join(profile, '.rotation-unit-ready');
    const armPath = join(evidence, 'armed.json');
    const disarmed = join(evidence, 'disarmed.json');
    if (v.arm) {
        assert.ok(!v['confirm-power-cut']);
        await mkdir(evidence);
        await canonical(evidence);
        await mkdir(profile);
        await canonical(profile);
        await fixture('prepare', 'ROTATION_FIXTURE_PREPARED');
        const originals = Object.fromEntries(await Promise.all(files.map(async file => [file, await hash(join(profile, file))])));
        const child = spawn(exe, ['--exact', 'vault_generation::tests::rotation_interruption_child'],
            { env, stdio: 'ignore', windowsHide: true });
        let exited = false, spawnError, armed = false;
        child.once('exit', () => { exited = true; });
        child.once('error', error => { spawnError = error; });
        try {
            const stop = Date.now() + 60_000;
            while (!exited && !spawnError && Date.now() < stop) {
                if (await readFile(readyPath, 'utf8').then(x => x === v.phase, () => false)) {
                    await writeNew(armPath, { ...identity, originals, armedAt: new Date().toISOString() });
                    armed = true;
                    console.log('ARMED: ' + v.phase + ' · operator may cut this disposable VM within 45 seconds.');
                    const deadline = Date.now() + 45_000;
                    while (!exited && !spawnError && Date.now() < deadline) await delay(100);
                    break;
                }
                await delay(100);
            }
            throw spawnError ?? new Error('No power cut observed within the arm window');
        } finally {
            try {
                if (armed) await writeNew(disarmed, { reason: 'Arm returned without operator power cut' });
            } finally {
                if (!exited && !spawnError) child.kill();
            }
        }
    }
    await canonical(evidence);
    await canonical(profile);
    const armed = JSON.parse(await readFile(armPath, 'utf8'));
    validateRotationReceipt(armed, identity, v['confirm-power-cut']);
    assert.ok(!(await exists(disarmed)), 'Arm session disarmed');
    assert.equal(await readFile(readyPath, 'utf8'), v.phase, 'Missing checkpoint receipt');
    await writeNew(join(evidence, 'check-started.json'), identity);
    for (const file of files) assert.equal(await hash(join(profile, file)), armed.originals[file], 'Original ciphertext changed');
    await fixture('check', 'ROTATION_FIXTURE_RECOVERED');
    await writeNew(join(evidence, 'power-check.json'), { armed, check: identity, recovery: 'PASS',
        scope: 'Rust unit-test rotation boundary, not installed Release',
        powerCut: 'operator-confirmed; host action not independently verified' });
    console.log('PASS rotation boundary recovery · ' + v.phase);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
