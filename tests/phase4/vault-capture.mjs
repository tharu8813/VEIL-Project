// Phase 4-0: download capture into the encrypted vault.
//
//   node tests/phase4/vault-capture.mjs [--profile-parent <existing dir>]
//
// Runs the desktop "vault" smoke stage (auth without login, small 1 KiB, 8 MiB, 64 MiB, cookie-auth,
// redirect, dropped connection, POST) and then checks, with VEIL closed:
//   - every stored object exists and the vault holds nothing else (no staging leftovers);
//   - no file anywhere in the profile contains the run's unique payload in plaintext;
//   - no veil-vault-* file appeared in the user's real Downloads folder (names only are read);
//   - how many bytes the engine had received/written before VEIL cancelled its download.
// Requires crates/veil-security/target/release/veil-vault.exe (built before the desktop app).
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createReadStream } from 'node:fs';
import { mkdir, mkdtemp, readdir, readFile, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { basename, isAbsolute, join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture, vaultPayload } from '../integration/fixture.mjs';

const args = process.argv.slice(2);
const parent = args.includes('--profile-parent') ? args[args.indexOf('--profile-parent') + 1] : undefined;
if (parent !== undefined && !isAbsolute(parent)) throw new Error('--profile-parent must be an absolute existing directory');
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'vault-capture-'));
const profileRoot = parent ? await mkdtemp(join(parent, 'veil-vault-')) : evidence;
const profile = join(profileRoot, 'profile');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const downloadsDir = join(homedir(), 'Downloads');

async function containsPayload(path) {
    const needle = Buffer.from(vaultPayload);
    let tail = Buffer.alloc(0);
    for await (const chunk of createReadStream(path)) {
        const window = Buffer.concat([tail, chunk]);
        if (window.indexOf(needle) >= 0) return true;
        tail = window.subarray(Math.max(0, window.length - needle.length));
    }
    return false;
}

async function files(dir) {
    return (await readdir(dir, { recursive: true, withFileTypes: true }))
        .filter(entry => entry.isFile())
        .map(entry => join(entry.parentPath ?? entry.path, entry.name));
}

const downloadsBefore = new Set((await readdir(downloadsDir).catch(() => [])).filter(name => name.startsWith('veil-vault-')));
const { server, origin } = await startFixture();
try {
    const report = join(evidence, 'vault.json');
    await promisify(execFile)('dotnet', [desktop, '--smoke', profile, origin, 'vault', report], { timeout: 600_000, env: {
        ...process.env, VEIL_DEV_VAULT_PASSPHRASE: 'synthetic-capture-pass', VEIL_DEV_VAULT_RECOVERY: 'CAPTA-CAPTB-CAPTC-CAPTD-CAPTE-CAPTF-CAPTG-CAPTH' } })
        .catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
    const result = JSON.parse(await readFile(report, 'utf8'));
    assert.equal(result.error, null, result.error);
    const vault = JSON.parse(await readFile(join(evidence, 'vault.vault.json'), 'utf8'));

    const stored = vault.results.filter(r => r.Outcome === '보관됨').map(r => r.ObjectPath.toLowerCase());
    // objects/ holds exactly the stored downloads (no staging or partial files); meta/ and index/ hold encrypted metadata.
    const objectFiles = (await files(join(vault.vault, 'objects'))).map(path => path.toLowerCase());
    assert.deepEqual(objectFiles.sort(), [...stored].sort(), 'vault must hold exactly the stored objects');
    assert.ok((await files(vault.vault)).every(path => !basename(path).startsWith('.veil-encrypted-')), 'no staging leftovers');

    const leaks = [];
    for (const path of await files(profileRoot)) if (await containsPayload(path)) leaks.push(path);
    assert.deepEqual(leaks, [], 'download plaintext found on disk');

    const downloadsAfter = (await readdir(downloadsDir).catch(() => [])).filter(name => name.startsWith('veil-vault-') && !downloadsBefore.has(name));
    assert.deepEqual(downloadsAfter, [], 'files appeared in the real Downloads folder');

    const engine = result.checks.filter(check => check.startsWith('engine bytes before cancel'));
    await writeFile(join(evidence, 'summary.json'), JSON.stringify({ profileRoot, results: vault.results, engine }, null, 2));
    for (const r of vault.results) console.log(`${r.Name}\t${r.Outcome}\t${r.Bytes}\t${r.Detail ?? ''}\tengine received=${r.EngineBytesBeforeCancel} scratch=${r.EngineBytesOnDisk}`);
    console.log(`PASS vault capture · stored ${stored.length} · no plaintext in profile · evidence ${evidence}`);
} finally {
    server.close();
}
