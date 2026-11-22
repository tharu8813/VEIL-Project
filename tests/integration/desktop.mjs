import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readdir, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from './fixture.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const directory = await mkdtemp(join(root, 'desktop-test-'));
const profile = join(directory, 'profile');
const { server, origin, requests } = await startFixture();
const desktop = resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');

async function launch(path, stage) {
    const report = join(directory, `${stage}.json`);
    try {
        await run('dotnet', [desktop, '--smoke', path, origin, stage, report], { timeout: 90000 });
    } catch (error) {
        if (!(await readFile(report).catch(() => null))) throw error;
    }
    return JSON.parse(await readFile(report, 'utf8'));
}

try {
    for (const stage of ['write', 'read']) {
        const result = await launch(profile, stage);
        console.log(stage, result);
        assert.equal(result.error, null);
    }
    assert.ok((requests.get('/download') ?? 0) >= 5, 'expected fixture download requests');
    const files = await readdir(profile, { recursive: true, withFileTypes: true });
    assert.deepEqual(files.filter(file => file.isFile() &&
        (file.name.startsWith('veil-phase0-') || file.name.endsWith('.crdownload'))), []);
    const corrupt = join(directory, 'corrupt-profile');
    await mkdir(corrupt);
    await writeFile(join(corrupt, 'browser-state.json'), '{');
    assert.equal((await launch(corrupt, 'corrupt')).error, 'JsonException');
    assert.ok(!(await readdir(corrupt)).includes('webview2'));
    console.log('PASS desktop integration', directory);
} finally {
    server.close();
}
