// ADR-0008: VEIL threat engine 1.1 (signed local list) on navigation and downloads.
//
//   node tests/security/threat-engine.mjs
//
// A throwaway P-256 key signs synthetic lists; the Debug-only VEIL_TEST_THREAT_LIST_KEY hook trusts it.
//   valid:    listed host (localhost) and listed URL (/second) show the warning page; only the native bar continues;
//             listed file hash, listed redirect target and listed source host are blocked before the vault publishes;
//             unlisted addresses and files pass (Unknown is not blocked).
//   invalid:  wrong signer, tampered body, expired, unsigned: reported, and browsing continues (fail-open).
//   none:     no list, no engine.
// Loopback only; nothing leaves the machine.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture, vaultPayload } from '../integration/fixture.mjs';
import { newKeyPair, signList, urlKey } from '../../scripts/threat-list.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'threat-engine-'));
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server, origin } = await startFixture();
const trusted = newKeyPair();
const stranger = newKeyPair();
const vaultEnv = { VEIL_DEV_VAULT_PASSPHRASE: 'synthetic-threat-pass', VEIL_DEV_VAULT_RECOVERY: 'TESTA-TESTB-TESTC-TESTD-TESTE-TESTF-TESTG-TESTH' };

const smallFile = Buffer.alloc(64 * 1024, vaultPayload).subarray(0, 1024);
const day = 24 * 3600 * 1000;
const list = (overrides = {}) => Buffer.from(JSON.stringify({
    schema: 1, issued: new Date(Date.now() - day).toISOString(), expires: new Date(Date.now() + 30 * day).toISOString(),
    hosts: ['localhost'],
    urls: [urlKey(`${origin}/second`), urlKey(`${origin}/vault-dl?kind=redirected`)],
    files: [createHash('sha256').update(smallFile).digest('hex')],
    ...overrides,
}));

async function stage(label, expect, files) {
    const profile = join(evidence, label, 'profile');
    await mkdir(join(profile, 'security'), { recursive: true });
    for (const [name, content] of Object.entries(files)) await writeFile(join(profile, 'security', name), content);
    const report = join(evidence, `${label}.json`);
    await run('dotnet', [desktop, '--smoke', profile, origin, 'threats', report], {
        timeout: 240_000, env: { ...process.env, ...vaultEnv, VEIL_TEST_THREAT_EXPECT: expect, VEIL_TEST_THREAT_LIST_KEY: trusted.spki },
    }).catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
    const result = JSON.parse(await readFile(report, 'utf8'));
    assert.equal(result.error, null, `${label}: ${result.error}`);
    console.log(`${label}: PASS (${result.checks.filter(c => !/^(loopback|temp path|desktop init|HTTPS address|search enc|fixture addr|address scheme|Address|FindText)/.test(c)).length} checks)`);
    return result;
}

try {
    const body = list();
    await stage('valid', 'valid', { 'threat-list.json': body, 'threat-list.json.sig': signList(body, trusted.privatePem) });
    await stage('none', 'none', {});
    await stage('wrong-signer', 'invalid', { 'threat-list.json': body, 'threat-list.json.sig': signList(body, stranger.privatePem) });
    const tampered = Buffer.from(body.toString().replace('localhost', 'example.org'));
    await stage('tampered', 'invalid', { 'threat-list.json': tampered, 'threat-list.json.sig': signList(body, trusted.privatePem) });
    const expired = list({ expires: new Date(Date.now() - day).toISOString() });
    await stage('expired', 'invalid', { 'threat-list.json': expired, 'threat-list.json.sig': signList(expired, trusted.privatePem) });
    await stage('unsigned', 'invalid', { 'threat-list.json': body });
    console.log(`PASS threat engine · evidence ${evidence}`);
} finally {
    server.close();
}
