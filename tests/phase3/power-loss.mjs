// SPDX-License-Identifier: MPL-2.0
// Gate B2: arm a synthetic writer in a verified offline VM, then check after an operator power cut.
// This runner never shuts down a machine and never claims to prove the host power-off command.
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { lstat, mkdir, open, readFile, realpath } from 'node:fs/promises';
import { dirname, isAbsolute, join, win32 } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { parseArgs, promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { validatePreflight } from './phase34-vm.mjs';

const run = promisify(execFile);
const repo = fileURLToPath(new URL('../../', import.meta.url));
const normal = path => win32.normalize(path).toLowerCase();

export function validatePaths(primary, evidence, profile) {
    assert.ok(win32.isAbsolute(evidence) && win32.isAbsolute(profile), 'Absolute lab paths required');
    assert.equal(normal(win32.dirname(evidence)), normal(primary.parent), 'Evidence must be a direct child of the verified lab');
    assert.match(win32.basename(evidence), /^power-loss-[a-z0-9-]+$/i, 'Use a dedicated power-loss evidence directory');
    assert.equal(normal(profile), normal(win32.join(evidence, 'profile')), 'Use only the synthetic profile within the evidence directory');
}

export function validateArmed(armed, current, confirmed) {
    assert.equal(confirmed, true, 'Operator must confirm an actual VM power cut, not a normal restart');
    assert.equal(armed.schema, 2, 'Unsupported or stale arm receipt');
    assert.equal(armed.uuid, current.uuid, 'Different VM');
    assert.ok(armed.boot && current.boot && armed.boot !== current.boot, 'Same boot: power loss recovery not tested');
    for (const key of ['volumeId', 'image', 'profile', 'evidence'])
        assert.equal(normal(armed[key]), normal(current[key]), 'Arm/check mismatch: ' + key);
    for (const key of ['desktopSha256', 'vaultSha256', 'origin'])
        assert.equal(armed[key], current[key], 'Arm/check mismatch: ' + key);
    assert.ok(Number.isSafeInteger(armed.before) && armed.before >= 0 &&
        Number.isSafeInteger(armed.committed) && armed.committed >= armed.before + 100,
        'Invalid committed-record watermark');
}

export function checkRecovery(armed, result, exitCode) {
    assert.equal(exitCode, 0, 'Desktop check did not exit successfully');
    assert.equal(result.error, null, result.error);
    const stored = result.checks.find(check => /^storm records contiguous \(max \d+, localStorage \d+\)$/.test(check));
    assert.ok(stored, 'No contiguous-record evidence');
    const max = Number(/max (\d+)/.exec(stored)[1]);
    assert.ok(Number.isSafeInteger(max) && max >= armed.committed, 'Records confirmed before arming were lost');
    return { stored, max };
}

async function canonical(path) {
    assert.equal(normal(await realpath(path)), normal(path), 'Redirected lab path');
    assert.ok(!(await lstat(path)).isSymbolicLink(), 'Redirected lab path');
}

async function writeNew(path, data) {
    const handle = await open(path, 'wx');
    try { await handle.writeFile(JSON.stringify(data, null, 2)); await handle.sync(); }
    finally { await handle.close(); }
}

async function main() {
    const { values } = parseArgs({ options: {
        arm: { type: 'boolean' }, check: { type: 'boolean' }, 'isolated-vm': { type: 'boolean' },
        'confirm-power-cut': { type: 'boolean' }, preflight: { type: 'string' },
        profile: { type: 'string' }, evidence: { type: 'string' }, port: { type: 'string', default: '47391' },
    } });
    assert.ok(Boolean(values.arm) !== Boolean(values.check), 'Choose exactly one of --arm or --check');
    assert.ok(process.platform === 'win32' && values['isolated-vm'] && values.preflight,
        'Run only in the prepared offline VirtualBox guest with a current-boot preflight');
    const { profile, evidence } = values;
    assert.ok(profile && evidence && isAbsolute(profile) && isAbsolute(evidence), 'Absolute profile and evidence required');
    const port = Number(values.port);
    assert.ok(Number.isInteger(port) && port >= 1024 && port <= 65535, 'Invalid fixture port');
    const ps = join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    const receipt = JSON.parse((await readFile(values.preflight, 'utf8')).replace(/^\uFEFF/, ''));
    const { stdout } = await run(ps, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File',
        join(repo, 'tests/phase3/phase34-guest-identity.ps1'), '-Json']);
    const guest = JSON.parse(stdout);
    const { primary } = validatePreflight(receipt, guest);
    validatePaths(primary, evidence, profile);
    await canonical(primary.parent);
    const mounted = await run(join(process.env.SystemRoot, 'System32', 'mountvol.exe'), [primary.drive + '\\', '/L']);
    assert.equal(mounted.stdout.trim().toLowerCase(), primary.volumeId.toLowerCase(), 'Lab volume changed');
    const desktop = process.env.VEIL_DESKTOP_DLL ?? join(repo, 'apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
    assert.ok(isAbsolute(desktop), 'Prebuilt Debug DLL path required');
    const hash = async path => createHash('sha256').update(await readFile(path)).digest('hex');
    const identity = { uuid: guest.uuid, boot: guest.boot, volumeId: primary.volumeId, image: primary.image,
        profile, evidence, desktopSha256: await hash(desktop), vaultSha256: await hash(join(dirname(desktop), 'veil-vault.exe')),
        origin: 'http://127.0.0.1:' + port };
    const armPath = join(evidence, 'armed.json');
    let armed;
    if (values.arm) {
        assert.ok(!values['confirm-power-cut'], 'Power cut confirmation belongs to --check');
        await mkdir(evidence); // exclusive: never reuse an earlier arm or any existing profile
        await canonical(evidence);
    } else {
        await canonical(evidence);
        await canonical(profile);
        armed = JSON.parse(await readFile(armPath, 'utf8'));
        validateArmed(armed, identity, values['confirm-power-cut']);
        JSON.parse(await readFile(join(profile, 'browser-state.json'), 'utf8'));
    }
    const { server, origin } = await startFixture(port);
    assert.equal(origin, identity.origin);
    const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^VEIL_(DEV_VAULT|TEST_|STORM_HOLD)/i.test(name)));
    env.VEIL_TEST_OFFLINE_HOSTS = '1';
    let child;
    try {
        if (values.arm) {
            const report = join(evidence, 'arm-stage.json');
            child = spawn('dotnet', [desktop, '--smoke', profile, origin, 'storm', report], {
                windowsHide: true, stdio: 'ignore', env: { ...env, VEIL_STORM_HOLD_SECONDS: '600' },
            });
            let exited = false, spawnError;
            child.once('exit', () => { exited = true; });
            child.once('error', error => { spawnError = error; });
            for (let i = 0; i < 1200; i++) {
                if (spawnError) throw spawnError;
                if (exited) throw new Error('Writer exited before arming');
                const ready = await readFile(report, 'utf8').then(JSON.parse).catch(() => null);
                if (ready?.error) throw new Error('Writer failed before arming');
                if (ready?.stormReady) {
                    const proof = { schema: 2, ...identity, before: ready.before, committed: ready.committed,
                        armedAt: new Date().toISOString() };
                    assert.ok(Number.isSafeInteger(proof.before) && proof.before >= 0 &&
                        Number.isSafeInteger(proof.committed) && proof.committed >= proof.before + 100);
                    await writeNew(armPath, proof);
                    console.log('ARMED: confirmed records >= ' + proof.committed + '. Operator may power off this disposable VM.');
                    for (let j = 0; j < 6000 && !exited && !spawnError; j++) await delay(100);
                    throw new Error('No verified power cut during the arm session');
                }
                await delay(100);
            }
            throw new Error('Writer never armed');
        }
        const report = join(evidence, 'check-stage.json');
        // A failed check or a successful previous check must not be silently overwritten.
        await writeNew(join(evidence, 'check-started.json'), { ...identity, at: new Date().toISOString() });
        await run('dotnet', [desktop, '--smoke', profile, origin, 'storm-check', report], { timeout: 120_000, env });
        const result = JSON.parse(await readFile(report, 'utf8'));
        const recovered = checkRecovery(armed, result, 0);
        await writeNew(join(evidence, 'power-check.json'), { armed, check: identity, ...recovered,
            recovery: 'PASS', powerCut: 'operator-confirmed; different guest boot observed, host action not independently verified' });
        console.log('PASS post-reboot recovery with operator-confirmed power cut: ' + recovered.stored);
    } finally {
        if (child && child.exitCode === null && child.signalCode === null) child.kill();
        server.closeAllConnections?.();
        server.close();
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
