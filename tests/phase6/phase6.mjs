// Phase 6 batch smoke (Debug build): bookmarks/history, tabs, search, site permissions, site data,
// password manager and the settings hub, driven through the real native management pages.
//
//   node tests/phase6/phase6.mjs [stage ...]     (needs openssl on PATH, e.g. Git for Windows)
//
// One synthetic profile runs every stage in order; "-read" stages restart VEIL to check persistence.
// Two loopback HTTPS origins (trusted only through the Debug-only VEIL_TEST_TRUSTED_SPKI hook) and the
// HTTP fixture are the only reachable hosts (VEIL_TEST_OFFLINE_HOSTS maps every other name to NOTFOUND).
// After the run the profile, reports and logs are scanned for the synthetic password-manager secrets.
// NOT covered: real keyboard/mouse/IME/DPI/screen reader, real camera/microphone/location devices.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { startHttpsFixtures } from './https-fixture.mjs';

const run = promisify(execFile);
const all = ['p6-library', 'p6-library-read', 'p6-tabs', 'p6-search', 'p6-search-read', 'p6-permissions',
    'p6-permissions-read', 'p6-sitedata', 'p6-passwords', 'p6-settings'];
const selected = process.argv.slice(2);
const stages = selected.length ? all.filter(stage => selected.includes(stage)) : all;
assert.ok(stages.length > 0, `unknown stage; choose from ${all.join(', ')}`);

const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'phase6-'));
const profile = join(evidence, 'profile');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server, origin, requests } = await startFixture();
const https = await startHttpsFixtures(evidence);
const env = {
    ...process.env,
    VEIL_TEST_TRUSTED_SPKI: https.spki,
    VEIL_TEST_HTTPS_A: https.origins[0],
    VEIL_TEST_HTTPS_B: https.origins[1],
    VEIL_TEST_OFFLINE_HOSTS: '1',
};
const secrets = ['p6-synthetic-master-passphrase', 'P6-Synthetic-Site-Secret-41'];

async function scan(directory, found = []) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const path = join(directory, entry.name);
        if (entry.isDirectory()) { await scan(path, found); continue; }
        if (entry.name.endsWith('.pem')) continue;
        const bytes = await readFile(path).catch(() => null);
        if (!bytes) continue;
        for (const secret of secrets)
            for (const encoding of ['utf8', 'utf16le'])
                if (bytes.includes(Buffer.from(secret, encoding))) found.push(`${path} (${encoding}: ${secret.slice(0, 12)}…)`);
    }
    return found;
}

const results = [];
let failed = false;
try {
    for (const stage of stages) {
        const report = join(evidence, `${stage}.json`);
        const started = Date.now();
        let output = '';
        try {
            const { stdout, stderr } = await run('dotnet', [desktop, '--smoke', profile, origin, stage, report], { timeout: 240_000, env });
            output = stdout + stderr;
        } catch (error) {
            output = String(error.stdout ?? '') + String(error.stderr ?? '');
            if (!(await readFile(report).catch(() => null))) {
                results.push({ stage, error: `no report: ${error.message.split('\n')[0]}` });
                failed = true;
                console.log(`FAIL ${stage}: no report`);
                break;
            }
        }
        await writeFile(join(evidence, `${stage}.log`), output);
        const result = JSON.parse(await readFile(report, 'utf8'));
        const seconds = Math.round((Date.now() - started) / 1000);
        result.checks ??= [];
        results.push({ stage, seconds, error: result.error, checks: result.checks.length });
        for (const line of result.checks.filter(check => check.startsWith('observed:'))) console.log(`  ${line}`);
        if (result.error) {
            failed = true;
            console.log(`FAIL ${stage} (${seconds}s): ${result.error}`);
            console.log(`  last checks: ${result.checks.slice(-3).join(' | ')}`);
            break;
        }
        console.log(`PASS ${stage} (${seconds}s, ${result.checks.length} checks)`);
    }

    const typed = [...requests.keys(), ...https.requests].filter(path => path.includes('p6marker'));
    assert.deepEqual(typed, [], 'typing in the address bar reached a server');
    if (stages.includes('p6-passwords') && !failed) {
        const leaks = await scan(evidence);
        await writeFile(join(evidence, 'secret-scan.json'), JSON.stringify(leaks, null, 2));
        assert.deepEqual(leaks, [], `synthetic password-manager secrets found on disk:\n${leaks.join('\n')}`);
        console.log('secret scan: PASS · no synthetic secret in profile, engine data, reports or logs');
    }
    await writeFile(join(evidence, 'summary.json'), JSON.stringify({ origin, https: https.origins, results }, null, 2));
    assert.ok(!failed, 'a Phase 6 stage failed');
    console.log(`PASS phase 6 batch · evidence ${evidence}`);
} finally {
    server.close();
    await https.stop();
}
