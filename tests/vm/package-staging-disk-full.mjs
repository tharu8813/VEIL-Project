// SPDX-License-Identifier: MPL-2.0
// E1 preparation: real ENOSPC in the shared staging byte-copy operation only.
// Never fill Program Files, ProgramData, a system disk, or the installed profile.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { lstat, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { isAbsolute, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs, promisify } from 'node:util';
import { fillToReserve, verifiedDiskFullLab, verifyLabMount } from '../phase4/vault-disk-full.mjs';

const run = promisify(execFile);
const repo = fileURLToPath(new URL('../../', import.meta.url));
const hash = async path => createHash('sha256').update(await readFile(path)).digest('hex');
export function assertCopyFailure(result) {
    assert.equal(result.status, 'FAILED', 'Expected actual failed copy');
    assert.ok(Array.isArray(result.ioCodes) && result.ioCodes.some(code => code === 112 || code === 39),
        'Expected Windows disk-full IOException, not unrelated failure');
}
async function main() {
    const { values: v } = parseArgs({ options: { 'profile-parent': { type: 'string' },
        'evidence-parent': { type: 'string' }, preflight: { type: 'string' } } });
    const small = v['profile-parent'], primaryParent = v['evidence-parent'];
    assert.ok(primaryParent && isAbsolute(primaryParent));
    const { primary, diskFull, capacity } = await verifiedDiskFullLab(small, v.preflight);
    await verifyLabMount(primaryParent, primary);
    const evidence = await mkdtemp(join(primaryParent, 'package-copy-full-'));
    const disposable = await mkdtemp(join(small, 'package-copy-full-'));
    const source = join(evidence, 'synthetic-package'), existing = join(evidence, 'synthetic-existing');
    const stage = join(disposable, 'stage'), filler = join(disposable, 'filler.bin');
    const names = ['Desktop/Veil.Desktop.exe', 'Desktop/Veil.Desktop.dll', 'Broker/Veil.VolumeBroker.exe',
        'manifest.json', 'manifest.p7s'];
    const report = { scope: 'shared staging byte-copy; synthetic unsigned files; no installed update or signature test',
        status: 'FAIL', capacity };
    async function copy() {
        const ps = join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
        const { stdout } = await run(ps, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File',
            join(repo, 'tests/vm/package-copy-probe.ps1'), '-Source', source, '-Destination', stage],
            { timeout: 120000, windowsHide: true });
        return JSON.parse(stdout.replace(/^\uFEFF/, '').trim());
    }
    try {
        for (const root of [source, existing]) {
            await mkdir(join(root, 'Desktop'), { recursive: true });
            await mkdir(join(root, 'Broker'));
            for (const name of names) await writeFile(join(root, name),
                randomBytes(name.endsWith('.dll') ? 8 * 1024 ** 2 : 4096), { flag: 'wx' });
        }
        const inventory = async root => Object.fromEntries(await Promise.all(names.map(async name => [name, await hash(join(root, name))])));
        const original = await inventory(existing), expected = await inventory(source);
        await mkdir(stage);
        // Establish the copy works before deliberately exhausting the separate disposable volume.
        assert.equal((await copy()).status, 'COPIED');
        assert.deepEqual(await inventory(stage), expected);
        await rm(stage, { recursive: true });
        await mkdir(stage);
        try {
            await verifyLabMount(small, diskFull);
            report.fill = await fillToReserve(small, filler, capacity);
            report.failure = await copy();
            assertCopyFailure(report.failure);
            assert.deepEqual(await inventory(existing), original);
            assert.deepEqual(await inventory(source), expected);
            // Only the newly created synthetic stage is removed; no installed path is used.
            assert.ok(!(await lstat(stage)).isSymbolicLink());
            await rm(stage, { recursive: true });
            assert.ok(!(await readdir(disposable)).includes('stage'));
            report.stageCleaned = true;
        } finally {
            await rm(filler, { force: true });
        }
        await verifyLabMount(small, diskFull);
        await mkdir(stage);
        assert.equal((await copy()).status, 'COPIED');
        assert.deepEqual(await inventory(stage), expected);
        assert.deepEqual(await inventory(existing), original);
        assert.deepEqual(await inventory(source), expected);
        report.retryVerified = true;
        report.status = 'PASS';
        console.log('PASS package staging byte-copy disk-full and retry', evidence);
    } catch (error) {
        report.error = error.message;
        throw error;
    } finally {
        await writeFile(join(evidence, 'summary.json'), JSON.stringify(report, null, 2));
    }
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
