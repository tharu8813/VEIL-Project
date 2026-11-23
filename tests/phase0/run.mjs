import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readdir, readFile, writeFile } from 'node:fs/promises';
import { join, relative, resolve } from 'node:path';
import { promisify } from 'node:util';
import { marker, startFixture } from '../integration/fixture.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const runDir = await mkdtemp(join(root, 'phase0-'));
const { origin, server, requests } = await startFixture();
const evidence = { origin, results: {} };
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');

async function inventory(directory) {
    const files = await readdir(directory, { recursive: true, withFileTypes: true });
    const markers = [];
    const downloads = [];
    const readErrors = [];
    for (const file of files.filter(item => item.isFile())) {
        const path = join(file.parentPath, file.name);
        const name = relative(directory, path);
        try {
            if ((await readFile(path)).includes(Buffer.from(marker))) markers.push(name);
        } catch (error) {
            readErrors.push({ name, code: error.code });
        }
        if (file.name.startsWith('veil-phase0-') || file.name.endsWith('.crdownload')) downloads.push(name);
    }
    return { markers: markers.sort(), downloads, readErrors };
}

async function experiment(name, launch, cancelledState) {
    const result = evidence.results[name] = {};
    if (!launch) {
        result.status = 'NOT RUN';
        return;
    }
    const profile = join(runDir, name);
    const fields = ['cookie', 'localStorage', 'indexedDB', 'serviceWorker', 'cacheStorage'];
    const actions = ['write', 'read', 'download'];
    if (name === 'webview2') actions.push('download-feature');
    const expectedHash = createHash('sha256').update(marker).digest('hex').toUpperCase();
    for (const action of actions) {
        const report = join(runDir, `${name}-${action}.json`);
        try {
            result[action] = await launch(profile, action, report);
            assert.ok(!result[action].error, result[action].error);
            if (action.startsWith('download')) {
                assert.equal(result[action].download, cancelledState);
                if (name === 'webview2') result[action].downloadBodyMatched = result[action].downloadBody === expectedHash;
            } else {
                for (const field of fields) assert.equal(result[action][field], true, field);
            }
            result[action].status = 'PASS';
        } catch (error) {
            result[action] = { ...result[action], status: 'FAIL', error: String(error.message) };
        }
    }
    try {
        result.inventory = await inventory(profile);
        assert.deepEqual(result.inventory.downloads, []);
        result.inventory.status = 'PASS';
    } catch (error) {
        result.inventory = { ...result.inventory, status: 'FAIL', error: String(error.message) };
    }
    result.status = [...actions, 'inventory'].every(action => result[action].status === 'PASS')
        ? 'PASS' : 'FAIL';
}

try {
    await experiment('webview2', process.argv.includes('--skip-webview2') ? null : async (profile, action, report) => {
        try {
            await run('dotnet', [desktop, '--probe', profile, origin, action, report], { timeout: 45000 });
        } catch (error) {
            const detail = await readFile(report, 'utf8').then(JSON.parse).catch(() => null);
            if (!detail) throw error;
            return detail;
        }
        return JSON.parse(await readFile(report, 'utf8'));
    }, 'Interrupted');
    await experiment('qt', process.env.VEIL_QT_PYTHON ? async (profile, action) => {
        const args = ['-X', 'faulthandler', 'tests/phase0/qt_probe.py', profile, origin, action];
        try {
            const { stdout } = await run(process.env.VEIL_QT_PYTHON, args, { timeout: 25000 });
            return JSON.parse(stdout.trim());
        } catch (error) {
            if (error.stdout?.trim()) return JSON.parse(error.stdout.trim());
            throw new Error(`Qt process exit ${error.code ?? error.signal}: ${error.stderr?.trim() || error.message}`);
        }
    } : null, 'DownloadCancelled');
} finally {
    server.close();
    evidence.requests = Object.fromEntries(requests);
    console.log(JSON.stringify(evidence, null, 2));
    const output = join(runDir, 'evidence.json');
    await writeFile(output, JSON.stringify(evidence, null, 2));
    console.log(output);
}

if (Object.values(evidence.results).some(result => result.status === 'FAIL') ||
    !Object.values(evidence.results).some(result => result.status === 'PASS')) process.exitCode = 1;
