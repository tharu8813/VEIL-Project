// SEC-004: long browsing session to see what the engine's taskbar jump list accumulates.
//
//   node tests/security/browse-session.mjs [--profile-parent <dir>] [--runs 3] [--assert-clean]
//
// Repeats the desktop "browse" smoke stage on one profile (30 distinct pages, repeat visits,
// 8 opened-and-closed tabs per run). Checks surviving Windows Jump Lists for this test's
// profile path, loopback origin, and unique title marker. Pairs with Procmon in the VM.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { inspectJumpListChanges, snapshotJumpLists } from './jump-list-evidence.mjs';

const run = promisify(execFile);
const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const parent = option('--profile-parent');
const runs = Number(option('--runs') ?? 3);
if (parent !== undefined && !isAbsolute(parent)) throw new Error('--profile-parent must be an absolute existing directory');
if (!Number.isSafeInteger(runs) || runs < 1) throw new Error('--runs must be a positive integer');
if (!process.env.APPDATA) throw new Error('APPDATA is required to locate Windows Jump Lists');

const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(parent ?? root, 'browse-session-'));
const profile = join(evidence, 'profile');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const recentFolder = join(process.env.APPDATA, 'Microsoft', 'Windows', 'Recent');

const { server, origin, visitTitleMarker } = await startFixture();
try {
    let before = await snapshotJumpLists(recentFolder);
    const findings = [];
    for (let i = 1; i <= runs; i++) {
        const report = join(evidence, `browse-${i}.json`);
        await run('dotnet', [desktop, '--smoke', profile, origin, 'browse', report], { timeout: 240000 })
            .catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
        const result = JSON.parse(await readFile(report, 'utf8'));
        assert.equal(result.error, null, result.error);
        const after = await snapshotJumpLists(recentFolder);
        for (const finding of inspectJumpListChanges(before, after, {
            profile, fixtureHost: new URL(origin).host, visitTitleMarker,
        })) findings.push({ run: i, ...finding });
        before = after;
        console.log(`run ${i}: PASS`);
    }
    const leaked = findings.some(f => f.mentionsProfile || f.fixtureHost || f.visitTitle);
    await writeFile(join(evidence, 'jumplist.json'), JSON.stringify({ origin, profile, runs, leaked, changed: findings }, null, 2));
    console.log(JSON.stringify(findings, null, 2));
    console.log(`${leaked ? 'SYNTHETIC MARKER FOUND' : 'NO SYNTHETIC MARKER IN CHANGED JUMP LISTS'} · evidence ${evidence}`);
    if (leaked && args.includes('--assert-clean')) process.exitCode = 1;
} finally {
    server.close();
}
