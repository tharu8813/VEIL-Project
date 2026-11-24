// Stage 1 developer-only regression batch. No product profile or VM changes.
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, writeFile, open } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { platform, release } from 'node:os';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const run = args.includes('--run');
const plan = args.includes('--plan');
if (run === plan || new Set(args).size !== args.length ||
    args.some(arg => !['--run', '--plan', '--include-vault-persistence'].includes(arg))) {
    throw new Error('Use --plan or --run, optionally with --include-vault-persistence.');
}
const root = fileURLToPath(new URL('../../', import.meta.url));
const steps = [
    { id: 'vault-build', command: 'cargo', args: ['build', '--release', '--locked', '--manifest-path', 'crates/veil-security/Cargo.toml', '--bin', 'veil-vault'], build: true },
    { id: 'desktop-build', command: 'dotnet', args: ['build', 'apps/desktop/Veil.Desktop.csproj', '-c', 'Debug', '-p:RestoreLockedMode=true'], build: true },
    { id: 'desktop-regression', command: process.execPath, args: ['tests/integration/desktop.mjs'] },
    { id: 'vault-capture', command: process.execPath, args: ['tests/phase4/vault-capture.mjs'] },
    ...(args.includes('--include-vault-persistence') ? [{ id: 'vault-persistence', command: process.execPath, args: ['tests/phase4/vault-persist.mjs'] }] : []),
].map(step => ({ ...step, status: 'NOT RUN' }));
if (plan) {
    console.log(JSON.stringify({ mode: 'plan', steps }, null, 2));
} else {
    if (platform() !== 'win32') throw new Error('Run only in an interactive standard-user Windows session.');
    const elevated = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        '([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)'],
        { encoding: 'utf8' }).trim();
    if (elevated !== 'False') throw new Error('Use a non-elevated Windows shell.');
    const evidenceRoot = join(root, '.veil-dev');
    await mkdir(evidenceRoot, { recursive: true });
    const directory = await mkdtemp(join(evidenceRoot, 'stage1-batch-'));
    const env = Object.fromEntries(Object.entries(process.env).filter(([key]) =>
        !/^(VEIL_DESKTOP_DLL$|VEIL_DEV_VAULT|VEIL_TEST_|WEBVIEW2_)/i.test(key)));
    // Do not silently ignore WebView2 overrides: the product explicitly refuses them.
    if (Object.keys(process.env).some(key => /^WEBVIEW2_/i.test(key)))
        throw new Error('Remove WEBVIEW2_ overrides before running the suite.');
    function git(args) {
        try { return execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim(); }
        catch { return null; }
    }
    const summary = { mode: 'run', commit: git(['rev-parse', 'HEAD']),
        dirty: null, os: release(), node: process.version, startedAt: new Date().toISOString(), steps };
    const status = git(['status', '--porcelain']);
    summary.dirty = status === null ? null : status.length > 0;
    const save = () => writeFile(join(directory, 'summary.json'), JSON.stringify(summary, null, 2));
    await save();
    let buildFailed = false;
    for (const step of steps) {
        if (buildFailed) { step.reason = 'Prerequisite build failed'; await save(); continue; }
        console.log('RUN', step.id);
        const start = Date.now();
        const log = await open(join(directory, step.id + '.log'), 'w');
        try {
            const result = await new Promise(resolve => {
                const child = spawn(step.command, step.args, { cwd: root, env, stdio: ['ignore', log.fd, log.fd], windowsHide: true });
                child.once('error', error => resolve({ error: error.message }));
                child.once('close', (exitCode, signal) => resolve({ exitCode, signal }));
            });
            Object.assign(step, result);
            step.status = result.exitCode === 0 ? 'PASS' : 'FAIL';
        } finally {
            await log.close();
        }
        step.durationMs = Date.now() - start;
        if (step.build && step.status !== 'PASS') buildFailed = true;
        await save();
        console.log(step.status, step.id);
    }
    summary.finishedAt = new Date().toISOString();
    await save();
    console.log('Evidence:', directory);
    if (steps.some(step => step.status !== 'PASS')) process.exitCode = 1;
}
