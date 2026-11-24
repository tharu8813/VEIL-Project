// SPDX-License-Identifier: MPL-2.0
// ADR-0010 C2: actual storage exhaustion at the Rust vault process boundary.
// Only the separately preflighted, disposable BitLocker VHDX in the offline VM may be filled.
// This is not a browser/UI, index-publication, power-loss or installed-broker test.
import assert from 'node:assert/strict';
import { execFile, spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { lstat, mkdir, mkdtemp, open, readFile, readdir, realpath, rm, statfs, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve, win32 } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs, promisify } from 'node:util';
import { validatePreflight } from '../phase3/phase34-vm.mjs';

const MiB = 1024 ** 2;
const run = promisify(execFile);
const repo = fileURLToPath(new URL('../../', import.meta.url));
const password = 'synthetic-disk-full-pass';
const recovery = 'AAAAA-BBBBB-CCCCC-DDDDD-EEEEE-FFFFF-GGGGG-HHHHH';

export function assertLabChild(parent, registered) {
    const actual = win32.normalize(parent).toLowerCase();
    assert.equal(win32.dirname(actual), win32.normalize(registered).toLowerCase());
    assert.match(win32.basename(actual), /^veil-phase34-[a-z0-9]{6}$/i);
}

export function frames(payload) {
    const pieces = [];
    for (let offset = 0; offset < payload.length; offset += 64 * 1024) {
        const chunk = payload.subarray(offset, offset + 64 * 1024);
        const header = Buffer.alloc(4);
        header.writeUInt32LE(chunk.length);
        pieces.push(header, chunk);
    }
    const end = Buffer.alloc(12);
    end.writeBigUInt64LE(BigInt(payload.length), 4);
    return Buffer.concat([...pieces, end]);
}

function session(exe, vault, first, commands) {
    const input = Buffer.concat([Buffer.from(first + '\n'), ...commands.map(c =>
        typeof c === 'string' ? Buffer.from(c + '\n') : c), Buffer.from('quit\n')]);
    const result = spawnSync(exe, ['serve', vault], { input, encoding: 'utf8', timeout: 120_000,
        maxBuffer: MiB, windowsHide: true });
    if (result.error) throw result.error;
    assert.equal(result.status, 0, result.stderr || 'vault process failed');
    const replies = result.stdout.trimEnd().split(/\r?\n/);
    assert.equal(replies.shift(), 'ok', 'vault session did not open');
    return replies;
}

export const freeBytes = async path => {
    const s = await statfs(path);
    return s.bavail * s.bsize;
};

export async function fillToReserve(parent, filler, capacity) {
    const reserve = 2 * MiB;
    const chunk = randomBytes(MiB);
    let written = 0;
    const handle = await open(filler, 'wx');
    try {
        while (true) {
            const available = await freeBytes(parent);
            if (available <= reserve) break;
            const amount = Math.min(chunk.length, available - reserve);
            if (written + amount > capacity) throw new Error('Filler exceeded verified volume capacity.');
            const { bytesWritten } = await handle.write(chunk, 0, amount);
            assert.equal(bytesWritten, amount, 'short filler write');
            written += bytesWritten;
            if (written % (8 * MiB) === 0 || amount < MiB) await handle.sync();
        }
        await handle.sync();
    } finally { await handle.close(); }
    const remaining = await freeBytes(parent);
    assert.ok(written > 0 && remaining >= 512 * 1024 && remaining <= 3 * MiB,
        'Unable to establish a bounded partial-write condition.');
    return { written, remaining };
}

export async function verifyLabMount(parent, volume) {
    assertLabChild(parent, volume.parent);
    assert.equal(win32.normalize(await realpath(parent)).toLowerCase(), win32.normalize(parent).toLowerCase());
    assert.ok(!(await lstat(parent)).isSymbolicLink());
    const mounted = await run(join(process.env.SystemRoot, 'System32', 'mountvol.exe'), [volume.drive + '\\', '/L']);
    assert.equal(mounted.stdout.trim().toLowerCase(), volume.volumeId.toLowerCase());
}

export async function verifiedDiskFullLab(parent, receiptPath) {
    if (process.platform !== 'win32' || !parent || !isAbsolute(parent) || !receiptPath)
        throw new Error('Run only through phase34-vm in a prepared offline Windows VM.');
    const receipt = JSON.parse((await readFile(receiptPath, 'utf8')).replace(/^\uFEFF/, ''));
    const ps = join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    const { stdout } = await run(ps, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File',
        join(repo, 'tests/phase3/phase34-guest-identity.ps1'), '-Json']);
    const { primary, diskFull } = validatePreflight(receipt, JSON.parse(stdout));
    assert.ok(diskFull, 'No verified disk-full volume');
    await verifyLabMount(parent, diskFull);
    const fs = await statfs(parent);
    const capacity = fs.blocks * fs.bsize;
    assert.ok(capacity > 0 && capacity <= 2 * 1024 ** 3, 'Refusing large or invalid volume');
    assert.ok(await freeBytes(parent) > 24 * MiB, 'Insufficient initial free space for test');
    return { primary, diskFull, capacity };
}

async function main() {
    const { values } = parseArgs({ options: { 'profile-parent': { type: 'string' }, preflight: { type: 'string' } } });
    const parent = values['profile-parent'];
    if (process.platform !== 'win32' || !parent || !isAbsolute(parent) || !values.preflight)
        throw new Error('Run only through phase34-vm in a prepared offline Windows VM.');
    const { capacity } = await verifiedDiskFullLab(parent, values.preflight);

    const desktop = process.env.VEIL_DESKTOP_DLL;
    assert.ok(desktop && isAbsolute(desktop), 'Use the VM batch prebuilt desktop path');
    const exe = join(dirname(desktop), 'veil-vault.exe');
    const evidence = await mkdtemp(join(parent, 'vault-disk-full-'));
    const vault = join(evidence, 'vault');
    await mkdir(vault);
    const existing = join(vault, 'existing.veil');
    const failed = join(vault, 'failed.veil');
    const retry = join(vault, 'retry.veil');
    const filler = join(evidence, 'filler.bin');
    const data = randomBytes(8 * MiB);
    const seed = randomBytes(64 * 1024);
    const digest = async p => createHash('sha256').update(await readFile(p)).digest('hex');
    const unlock = 'unlock\t' + password;
    const steps = [];
    const report = { scope: 'Rust vault storage exhaustion only', capacity, steps, status: 'FAIL' };
    try {
        assert.deepEqual(session(exe, vault, 'create\t' + password + '\t' + recovery,
            ['seal\t' + existing, frames(seed), 'verify\t' + existing]), ['ok\t' + seed.length, 'ok\t' + seed.length]);
        const original = await digest(existing);
        steps.push({ stage: 'seed', bytes: seed.length });
        try {
            const fill = await fillToReserve(parent, filler, capacity);
            steps.push({ stage: 'fill', ...fill });
            const replies = session(exe, vault, unlock, ['seal\t' + failed, frames(data), 'verify\t' + existing]);
            assert.equal(replies.length, 2);
            assert.match(replies[0], /^err\t.*(?:StorageFull|os error 112|os error 39)/i,
                'Expected a real disk-full write error, not an unrelated failure');
            assert.equal(replies[1], 'ok\t' + seed.length, 'session lost framing or original data');
            assert.equal(await digest(existing), original, 'existing ciphertext changed');
            const names = await readdir(vault);
            assert.ok(!names.includes(basename(failed)), 'failed object was published');
            assert.ok(names.every(name => !name.startsWith('.veil-encrypted-')), 'staging file leaked');
            steps.push({ stage: 'failed-seal', reply: replies[0], existingVerified: true });
        } finally {
            await rm(filler, { force: true });
        }
        assert.deepEqual(session(exe, vault, unlock, ['verify\t' + existing, 'seal\t' + retry, frames(data), 'verify\t' + retry]),
            ['ok\t' + seed.length, 'ok\t' + data.length, 'ok\t' + data.length]);
        assert.equal(await digest(existing), original);
        steps.push({ stage: 'reopen-and-retry', bytes: data.length });
        report.status = 'PASS';
        console.log('PASS vault disk-full core seal and recovery', evidence);
    } catch (error) {
        report.error = error.message;
        throw error;
    } finally {
        await writeFile(join(evidence, 'summary.json'), JSON.stringify(report, null, 2));
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
