// SPDX-License-Identifier: MPL-2.0
// Debug browser capture wrapper + actual Rust filesystem failures. Does not fill a disk.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createReadStream } from 'node:fs';
import { mkdir, mkdtemp, readFile, readdir } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture, vaultPayload } from '../integration/fixture.mjs';

assert.equal(process.platform, 'win32', 'interactive Debug Windows session required');
const args = process.argv.slice(2);
assert.ok(!args.length || (args.length === 2 && args[0] === '--profile-parent' && isAbsolute(args[1])));
const root = args[1] ?? resolve('.veil-dev');
if (!args.length) await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'vault-publication-'));
const profile = join(evidence, 'profile');
const report = join(evidence, 'publication.json');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server, origin } = await startFixture();
try {
    const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^VEIL_(DEV_VAULT|TEST_)/i.test(name)));
    Object.assign(env, { VEIL_DEV_VAULT_PASSPHRASE: 'synthetic-publication-pass',
        VEIL_DEV_VAULT_RECOVERY: 'AAAAA-BBBBB-CCCCC-DDDDD-EEEEE-FFFFF-GGGGG-HHHHH', VEIL_TEST_OFFLINE_HOSTS: '1' });
    await promisify(execFile)('dotnet', [desktop, '--smoke', profile, origin, 'vault-publication-faults', report],
        { timeout: 480_000, env }).catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
    const result = JSON.parse(await readFile(report, 'utf8'));
    assert.equal(result.error, null, result.error);
    const { entries } = JSON.parse(await readFile(join(evidence, 'publication.vault.json'), 'utf8'));
    assert.equal(entries.length, 4);
    assert.equal(new Set(entries.map(e => e.Id)).size, 4);
    assert.deepEqual(entries.filter(e => e.Recovered).map(e => e.bytes), [2048]);
    assert.deepEqual(entries.filter(e => !e.Recovered).map(e => e.bytes).sort((a,b) => a-b), [1024, 1024, 8 * 1024 ** 2]);
    const vault = join(profile, 'vault');
    assert.deepEqual((await readdir(join(vault, 'objects'))).sort(), entries.map(e => e.Id + '.veil').sort());
    const needle = Buffer.from(vaultPayload);
    for (const entry of await readdir(profile, { recursive: true, withFileTypes: true })) {
        if (!entry.isFile()) continue;
        const path = join(entry.parentPath ?? entry.path, entry.name);
        let tail = Buffer.alloc(0);
        for await (const chunk of createReadStream(path)) {
            const bytes = Buffer.concat([tail, chunk]);
            assert.equal(bytes.indexOf(needle), -1, 'plaintext payload in ' + path);
            tail = bytes.subarray(Math.max(0, bytes.length - needle.length));
        }
    }
    console.log('PASS publication fault recovery · original preserved · no plaintext · evidence ' + evidence);
} finally { server.closeAllConnections?.(); server.close(); }
