// ADR-0005: persistent vault across VEIL restarts — encrypted index, recovery code, rotation,
// index/metadata damage recovery, tamper detection and authenticated export.
//
//   node tests/phase4/vault-persist.mjs [--profile-parent <existing directory>]
//
// Uses only Debug-build test variables (VEIL_DEV_VAULT_*); the passphrases are synthetic.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import { isAbsolute, join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture, vaultPayload } from '../integration/fixture.mjs';

const root = resolve('.veil-dev');
const [option, parent, ...extra] = process.argv.slice(2);
if (option !== undefined && (option !== '--profile-parent' || !parent || !isAbsolute(parent) || extra.length)) {
    throw new Error('Use --profile-parent followed by an existing absolute test directory.');
}
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(parent ?? root, 'vault-persist-'));
const profile = join(evidence, 'profile');
const vaultDir = join(profile, 'vault');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const P1 = 'synthetic-pass-one', R1 = 'AAAAA-BBBBB-CCCCC-DDDDD-EEEEE-FFFFF-GGGGG-HHHHH';
const P2 = 'synthetic-pass-two', R2 = 'JJJJJ-KKKKK-LLLLL-MMMMM-NNNNN-PPPPP-QQQQQ-RRRRR';
const { server, origin } = await startFixture();
let step = 0;

async function stage(name, env) {
    const report = join(evidence, `${++step}-${name}.json`);
    const clean = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('VEIL_DEV_VAULT') && !key.startsWith('VEIL_TEST_')));
    await promisify(execFile)('dotnet', [desktop, '--smoke', profile, origin, name, report], { timeout: 600_000, env: { ...clean, ...env } })
        .catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
    const result = JSON.parse(await readFile(report, 'utf8'));
    const vault = JSON.parse(await readFile(report.replace(/\.json$/, '.vault.json'), 'utf8').catch(() => 'null'));
    return { result, vault };
}

async function isPayloadFile(path, size) {
    let offset = 0;
    const pattern = Buffer.from(vaultPayload);
    for await (const chunk of createReadStream(path)) {
        // The fixture sends 64 KiB blocks, each filled with the pattern from its start.
        for (let i = 0; i < chunk.length; i++, offset++) if (chunk[i] !== pattern[(offset % 65536) % pattern.length]) return false;
    }
    return offset === size;
}

async function reopen(env, label) {
    const { result, vault } = await stage('vault-reopen', env);
    assert.equal(result.error, null, `${label}: ${result.error}`);
    return vault;
}

async function flipByte(path) {
    const bytes = await readFile(path);
    bytes[Math.floor(bytes.length / 2)] ^= 0x55;
    await writeFile(path, bytes);
}

try {
    // 1. Store five downloads (fresh vault created with P1 / R1).
    const stored = await stage('vault', { VEIL_DEV_VAULT_PASSPHRASE: P1, VEIL_DEV_VAULT_RECOVERY: R1 });
    assert.equal(stored.result.error, null, stored.result.error);
    const names = stored.vault.results.filter(r => r.Outcome === '보관됨').map(r => r.Name).sort();
    assert.equal(names.length, 5);
    console.log(`stored: ${names.join(', ')}`);

    // 2. Restart: the list is decrypted from the index and every item exports byte-exact.
    let vault = await reopen({ VEIL_DEV_VAULT_PASSPHRASE: P1 }, 'reopen');
    assert.equal(vault.opened, true);
    assert.deepEqual(vault.entries.map(e => e.Name).sort(), names);
    for (const item of vault.exports) {
        assert.equal(item.failure, null, `${item.Name}: ${item.failure}`);
        assert.ok(await isPayloadFile(item.destination, item.Bytes), `${item.Name} export differs`);
    }
    console.log('reopen: PASS · 5 items listed and exported byte-exact');

    const backup = await stage('vault-backup', { VEIL_DEV_VAULT_PASSPHRASE: P1 });
    assert.equal(backup.result.error, null, backup.result.error);
    assert.ok(await isPayloadFile(backup.vault.destination, backup.vault.Bytes), 'backed up file differs after restore');
    console.log('backup and content-key rotation: PASS · metadata and byte-exact large file preserved');

    // 3. Wrong passphrase is refused.
    vault = await reopen({ VEIL_DEV_VAULT_PASSPHRASE: 'not-the-passphrase' }, 'wrong passphrase');
    assert.equal(vault.opened, false);
    console.log('wrong passphrase: refused');

    // 4. Index deleted, one metadata file damaged, a stale staging file left behind:
    //    the list is rebuilt, the damaged item is kept as a recovered item, staging is removed.
    for (const file of await readdir(join(vaultDir, 'index'))) await rm(join(vaultDir, 'index', file));
    const metas = await readdir(join(vaultDir, 'meta'));
    await flipByte(join(vaultDir, 'meta', metas[0]));
    await writeFile(join(vaultDir, 'objects', '.veil-encrypted-stale'), 'partial');
    await writeFile(join(vaultDir, 'objects', 'x.veil'), 'unrecognized object name');
    vault = await reopen({ VEIL_DEV_VAULT_PASSPHRASE: P1 }, 'rebuild');
    assert.equal(vault.entries.length, 5);
    assert.equal(vault.entries.filter(e => e.Recovered).length, 1);
    assert.ok(vault.exports.every(item => item.failure === null), 'all items still export after rebuild');
    assert.ok(!(await readdir(join(vaultDir, 'objects'))).includes('.veil-encrypted-stale'));
    assert.ok((await readdir(join(vaultDir, 'objects'))).includes('x.veil'), 'unknown file preserved');
    console.log('rebuild: PASS · index regenerated, 1 recovered item, staging removed');

    // 5. A tampered object fails authentication and leaves no exported file.
    const victim = vault.entries.find(e => !e.Recovered);
    await flipByte(join(vaultDir, 'objects', `${victim.Id}.veil`));
    vault = await reopen({ VEIL_DEV_VAULT_PASSPHRASE: P1 }, 'tamper');
    const bad = vault.exports.find(e => e.Id === victim.Id);
    assert.notEqual(bad.failure, null, 'tampered item must not export');
    assert.ok(!(await readdir(resolve(bad.destination, '..'))).includes(`${victim.Id}.bin`), 'no partial export');
    assert.equal(vault.exports.filter(e => e.failure === null).length, 4);
    console.log('tamper: PASS · tampered item refused, no file written');

    // 6. Recovery code → new passphrase and code; old secrets stop working.
    const rotated = await stage('vault-rotate', { VEIL_DEV_VAULT_RECOVERY: R1, VEIL_TEST_NEW_PASSPHRASE: P2, VEIL_TEST_NEW_RECOVERY: R2 });
    assert.equal(rotated.result.error, null, rotated.result.error);
    assert.equal((await reopen({ VEIL_DEV_VAULT_PASSPHRASE: P1 }, 'old pass')).opened, false);
    assert.equal((await reopen({ VEIL_TEST_RECOVER: R1 }, 'old code')).opened, false);
    assert.equal((await reopen({ VEIL_DEV_VAULT_PASSPHRASE: P2 }, 'new pass')).opened, true);
    assert.equal((await reopen({ VEIL_TEST_RECOVER: R2 }, 'new code')).opened, true);
    console.log('rotation: PASS · old passphrase/code refused, new ones open');

    const linked = await stage('vault-profile', {
        VEIL_TEST_OLD_PASSPHRASE: P2, VEIL_TEST_OLD_RECOVERY: R2,
        VEIL_TEST_NEW_RECOVERY: 'SSSSS-TTTTT-UUUUU-VVVVV-WWWWW-XXXXX-YYYYY-ZZZZZ',
    });
    assert.equal(linked.result.error, null, linked.result.error);
    console.log('profile link: PASS · fresh setup, cancellation, migration, recovery, and prompt-free reopen');

    // No download plaintext anywhere in the profile (exports live outside it).
    const needle = Buffer.from(vaultPayload);
    const files = (await readdir(profile, { recursive: true, withFileTypes: true })).filter(e => e.isFile());
    for (const entry of files) {
        const path = join(entry.parentPath ?? entry.path, entry.name);
        let tail = Buffer.alloc(0);
        for await (const chunk of createReadStream(path)) {
            const window = Buffer.concat([tail, chunk]);
            assert.equal(window.indexOf(needle), -1, `plaintext in ${path}`);
            tail = window.subarray(Math.max(0, window.length - needle.length));
        }
    }
    for (const dir of (await readdir(evidence)).filter(name => name.startsWith('exports-')))
        await rm(join(evidence, dir), { recursive: true, force: true });
    console.log(`PASS vault persistence · evidence ${evidence}`);
} finally {
    server.close();
}
