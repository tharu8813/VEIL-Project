// SPDX-License-Identifier: MPL-2.0
// Interactive Debug smoke, disposable Windows VM only. No automatic certificate trust installation.
// Prepare a throwaway 127.0.0.1 certificate (tests/integration/test-cert.mjs), trust its public
// certificate in the disposable guest's test account, then set VEIL_TEST_CERT_DIR to that directory.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createServer } from 'node:https';
import { mkdir, mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { testCertificate } from '../integration/test-cert.mjs';

assert.equal(process.platform, 'win32', 'interactive Windows VM required');
assert.ok(process.env.VEIL_TEST_CERT_DIR, 'pre-provisioned trusted VM test certificate required');
const args = process.argv.slice(2);
assert.ok(args.length === 0 || (args.length === 2 && args[0] === '--profile-parent' && isAbsolute(args[1])),
    'usage: node tests/phase4/vault-retry-https.mjs [--profile-parent <absolute existing directory>]');
const root = args[1] ?? resolve('.veil-dev');
if (!args.length) await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'vault-retry-https-'));
const profile = join(evidence, 'profile');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { key, cert, spki } = await testCertificate(evidence, 'retry');
const counts = new Map();
const requestLog = [];
const kinds = new Set(['ok', 'decline', 'repeat', 'redirect', 'forbidden', 'expired', 'navigate', 'queued']);
const payload = Buffer.alloc(1024, 0x52);
const tls = createServer({ key, cert }, (req, res) => {
    const path = new URL(req.url, 'https://127.0.0.1').pathname;
    const n = (counts.get(path) ?? 0) + 1;
    counts.set(path, n);
    const cookie = /(?:^|;\s*)retry_fixture=1(?:;|$)/.test(req.headers.cookie ?? '');
    requestLog.push({ path, method: req.method, cookie, n });
    res.setHeader('Cache-Control', 'no-store');
    if (req.method !== 'GET') return res.writeHead(405).end();
    if (path.startsWith('/source/')) {
        return res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8',
            'Set-Cookie': 'retry_fixture=1; Path=/; Secure; SameSite=Strict' }).end('<!doctype html><title>Retry fixture</title>');
    }
    if (path === '/hold.bin') {
        res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': 1048576 });
        res.write(Buffer.alloc(65536, 0x48));
        return; // deliberately held until the client cancels
    }
    if (path === '/retry-target.bin') return res.writeHead(200).end(payload);
    const kind = /^\/retry-([a-z]+)\.bin$/.exec(path)?.[1];
    if (!kinds.has(kind)) return res.writeHead(404).end();
    if (!cookie) return res.writeHead(401).end();
    // Browser receives an attachment; VEIL's initial fresh request fails; only explicit retry may succeed.
    if (n === 2) return res.writeHead(kind === 'forbidden' ? 403 : 503).end();
    if (n > 2 && kind === 'repeat') return res.writeHead(503).end();
    if (n > 2 && kind === 'redirect') return res.writeHead(302, { Location: '/retry-target.bin' }).end();
    return res.writeHead(200, { 'Content-Type': 'application/octet-stream',
        'Content-Disposition': 'attachment; filename="veil-retry-' + kind + '.bin"',
        'Content-Length': payload.length }).end(payload);
});
await new Promise((done, fail) => { tls.once('error', fail); tls.listen(0, '127.0.0.1', done); });
const origin = 'https://127.0.0.1:' + tls.address().port;
let http;
try {
    http = await startFixture();
    const report = join(evidence, 'retry.json');
    const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^VEIL_(DEV_VAULT|TEST_)/i.test(name)));
    Object.assign(env, {
        VEIL_TEST_RETRY_ORIGIN: origin, VEIL_TEST_TRUSTED_SPKI: spki, VEIL_TEST_OFFLINE_HOSTS: '1',
        VEIL_DEV_VAULT_PASSPHRASE: 'synthetic-https-retry-pass',
        VEIL_DEV_VAULT_RECOVERY: 'RETRA-RETRB-RETRC-RETRD-RETRE-RETRF-RETRG-RETRH',
    });
    try {
        await promisify(execFile)('dotnet', [desktop, '--smoke', profile, http.origin, 'vault-retry-https', report],
            { timeout: 480_000, env });
    } catch (error) {
        if (!(await readFile(report).catch(() => null))) throw error;
    }
    const result = JSON.parse(await readFile(report, 'utf8'));
    assert.equal(result.error, null, result.error);
    const expected = { ok: 3, decline: 2, repeat: 3, redirect: 3, forbidden: 2, expired: 2, navigate: 2, queued: 2 };
    for (const [kind, count] of Object.entries(expected)) {
        const path = '/retry-' + kind + '.bin';
        assert.equal(counts.get(path), count, path + ' exact server request count');
        assert.ok(requestLog.filter(item => item.path === path).every(item => item.method === 'GET' && item.cookie),
            path + ' uses the synthetic source cookie');
    }
    assert.equal(counts.get('/retry-target.bin') ?? 0, 0, 'retry must not follow redirect');
    assert.equal(counts.get('/hold.bin'), 1, 'one cancelled slot holder');
    const vault = JSON.parse(await readFile(join(evidence, 'retry.vault.json'), 'utf8'));
    const stored = vault.results.filter(item => item.ObjectPath !== null);
    assert.equal(stored.length, 1);
    assert.equal(stored[0].Name, 'veil-retry-ok.bin');
    const objects = await readdir(join(vault.vault, 'objects'));
    assert.deepEqual(objects, [stored[0].ObjectPath.split(/[\\/]/).at(-1)], 'exact encrypted object inventory');
    const files = await readdir(vault.vault, { recursive: true, withFileTypes: true });
    assert.ok(files.every(entry => !entry.name.startsWith('.veil-encrypted-')), 'no partial encrypted staging files');
    console.log('PASS HTTPS explicit retry and live-context guard · evidence ' + evidence);
} finally {
    await writeFile(join(evidence, 'requests.json'), JSON.stringify(requestLog, null, 2));
    http?.server.closeAllConnections?.();
    http?.server.close();
    tls.closeAllConnections?.();
    await new Promise(done => tls.close(done));
}
