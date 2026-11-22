import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readdir, readFile, writeFile } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';

const run = promisify(execFile);
const parent = resolve('.veil-dev');
await mkdir(parent, { recursive: true });
const directory = await mkdtemp(join(parent, 'profile-routing-'));
const [option, location, ...extra] = process.argv.slice(2);
if (option !== undefined && (option !== '--profile-parent' || !location || !isAbsolute(location) || extra.length > 0)) {
    throw new Error('Use --profile-parent followed by an existing absolute test directory.');
}
const profileRoot = location ? await mkdtemp(join(location, 'veil-synthetic-')) : directory;
const desktop = resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { origin, server, requests } = await startFixture();

async function probe(name, profile, action, env = process.env) {
    const report = join(directory, `${name}.json`);
    let exitCode = 0;
    await run('dotnet', [desktop, '--probe', profile, origin, action, report], {
        cwd: directory,
        env,
        timeout: 45000,
    }).catch(error => { exitCode = error.code; });
    return { exitCode, ...JSON.parse(await readFile(report, 'utf8')) };
}

try {
    const target = join(profileRoot, 'named-user-data');
    const write = await probe('write', target, 'write');
    const read = await probe('read', target, 'read');
    for (const result of [write, read]) {
        assert.equal(result.exitCode, 0, result.error);
        for (const type of ['cookie', 'localStorage', 'indexedDB', 'serviceWorker', 'cacheStorage']) {
            assert.equal(result[type], true, type);
        }
    }
    assert.ok((await readdir(target)).includes('EBWebView'), 'no engine profile at the selected path');
    const navigations = requests.get('/') ?? 0;
    const asFile = join(profileRoot, 'profile-is-file');
    await writeFile(asFile, 'synthetic path obstruction');
    const invalidRoot = await probe('invalid-root', asFile, 'read');
    assert.equal(invalidRoot.exitCode, 1);
    assert.match(invalidRoot.error, /IOException|UnauthorizedAccessException/);
    assert.equal(await readFile(asFile, 'utf8'), 'synthetic path obstruction');

    const inside = join(profileRoot, 'engine-path-blocked');
    await mkdir(inside);
    await writeFile(join(inside, 'EBWebView'), 'synthetic engine path obstruction');
    const blockedEngine = await probe('blocked-engine', inside, 'read');
    assert.equal(blockedEngine.exitCode, 1);
    assert.ok(blockedEngine.error);
    assert.equal(await readFile(join(inside, 'EBWebView'), 'utf8'), 'synthetic engine path obstruction');
    assert.deepEqual(await readdir(inside), ['EBWebView']);

    const override = join(profileRoot, 'unexpected-user-data');
    const requested = join(profileRoot, 'expected-user-data');
    const blockedOverride = await probe('blocked-override', requested, 'read', {
        ...process.env,
        WEBVIEW2_USER_DATA_FOLDER: override,
    });
    assert.equal(blockedOverride.exitCode, 1);
    assert.match(blockedOverride.error, /InvalidOperationException/);
    assert.equal(requests.get('/') ?? 0, navigations, 'a blocked launch reached the fixture');
    const entries = await readdir(profileRoot);
    assert.ok(!entries.includes('unexpected-user-data'));
    assert.ok(!entries.includes('expected-user-data'));
    const workingDirectory = await readdir(directory);
    assert.ok(!workingDirectory.some(name => name.endsWith('.WebView2')), 'unexpected default profile in the probe working directory');

    await writeFile(join(directory, 'evidence.json'), JSON.stringify({
        profileRoot, write, read, invalidRoot, blockedEngine, blockedOverride, navigations,
    }, null, 2));
    console.log('PASS selected profile and fail-closed routing scenarios', directory);
} finally {
    server.close();
}
