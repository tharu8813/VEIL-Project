// SEC-004: long browsing session to see what the engine's taskbar jump list accumulates.
//
//   node tests/security/browse-session.mjs [--profile-parent <dir>] [--runs 3]
//
// Repeats the desktop "browse" smoke stage on one profile (30 distinct pages, repeat visits,
// 8 opened-and-closed tabs per run), then reports which jump-list files in the user's
// CustomDestinations folder changed and whether they contain the synthetic page URLs or titles.
// It only reads the jump-list files; run it in the isolated VM for the authoritative trace.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readdir, readFile, stat, writeFile } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';

const run = promisify(execFile);
const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const parent = option('--profile-parent');
const runs = Number(option('--runs') ?? 3);
if (parent !== undefined && !isAbsolute(parent)) throw new Error('--profile-parent must be an absolute existing directory');

const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'browse-session-'));
const profileRoot = parent ? await mkdtemp(join(parent, 'veil-browse-')) : evidence;
const profile = join(profileRoot, 'profile');
const desktop = resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const jumpLists = join(process.env.APPDATA, 'Microsoft', 'Windows', 'Recent', 'CustomDestinations');

async function snapshot() {
    const result = new Map();
    for (const name of await readdir(jumpLists).catch(() => [])) {
        const info = await stat(join(jumpLists, name)).catch(() => null);
        if (info?.isFile()) result.set(name, info.mtimeMs);
    }
    return result;
}

const { server, origin } = await startFixture();
try {
    const before = await snapshot();
    const results = [];
    for (let i = 1; i <= runs; i++) {
        const report = join(evidence, `browse-${i}.json`);
        await run('dotnet', [desktop, '--smoke', profile, origin, 'browse', report], { timeout: 240000 })
            .catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
        const result = JSON.parse(await readFile(report, 'utf8'));
        assert.equal(result.error, null, result.error);
        results.push(result);
        console.log(`run ${i}: PASS`);
    }
    const after = await snapshot();
    const changed = [...after].filter(([name, mtime]) => before.get(name) !== mtime).map(([name]) => name);
    const host = new URL(origin).host;
    const findings = [];
    for (const name of changed) {
        const bytes = await readFile(join(jumpLists, name));
        const text = bytes.toString('utf16le') + '\n' + bytes.toString('latin1');
        findings.push({
            name,
            bytes: bytes.length,
            mentionsProfile: text.toLowerCase().includes(profileRoot.toLowerCase()),
            fixtureHost: text.includes(host) || text.includes('127.0.0.1'),
            visitUrl: /\/visit\?n=\d+/.test(text),
            visitTitle: /VEIL synthetic visit \d+/.test(text),
        });
    }
    await writeFile(join(evidence, 'jumplist.json'), JSON.stringify({ origin, profileRoot, runs, changed: findings }, null, 2));
    console.log(JSON.stringify(findings, null, 2));
    const leaked = findings.some(f => f.fixtureHost || f.visitUrl || f.visitTitle);
    console.log(`${leaked ? 'URL/TITLE IN JUMP LIST' : 'NO URL/TITLE IN JUMP LIST'} · changed=${findings.length} · evidence ${evidence}`);
} finally {
    server.close();
}
