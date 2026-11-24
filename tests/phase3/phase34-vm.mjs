// Batches the existing Debug probes for a verified disposable BitLocker VM. Production and physical tests stay separate.
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFile, lstat, mkdir, mkdtemp, readdir, readFile, realpath, rename, rm, stat, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, join, resolve, win32 } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs, promisify } from 'node:util';

const run = promisify(execFile);
const repo = fileURLToPath(new URL('../../', import.meta.url));
const mainRunners = [
    ['profile-routing', 'tests/phase3/profile-routing.mjs', []],
    ['engine-kill', 'tests/phase3/engine-kill.mjs', []],
    ['vault-capture', 'tests/phase4/vault-capture.mjs', []],
    ['vault-persistence', 'tests/phase4/vault-persist.mjs', []],
    ['jump-list', 'tests/security/browse-session.mjs', ['--runs', '1', '--assert-clean']],
    ['desktop-crash-cycle', 'tests/integration/desktop.mjs', ['--crash-cycle']],
];
// Loopback-only runners. They keep evidence under <cwd>/.veil-dev, so they run with the lab folder as the
// working directory: their engine profiles stay on the protected volume and the write trace covers them.
const localRunners = [
    ['network-guard', 'tests/phase5/network-guard.mjs', [], []],
    ['webrtc-guard', 'tests/phase5/webrtc-guard.mjs', [], []],
    ['dns-protection', 'tests/phase5/dns-protection.mjs', [], ['openssl']],
    ['smartscreen', 'tests/security/smartscreen.mjs', [], ['openssl']],
];
const manual = [
    ['installed-release', 'Signed Release installation, live broker lifecycle, and product startup and lock.'],
    ['recovery-backup', 'Real recovery password verification with the installed broker stopped.'],
    ['physical-writes', 'Procmon capture and analysis from the elevated guest shell.'],
    ['power-loss', 'Arm the dedicated test, cut VM power explicitly, then check after reboot.'],
    ['vault-disk-full', 'Vault operations while the disk is full. The existing disk-full runner covers profile recovery.'],
];
// Need an internet-connected isolated VM (ADR-0004 policy 4), never the offline lab VM.
const internet = [
    ['internet-dns-path', 'Real DNS query path with system DNS, public DoH providers and their bootstrap lookup.'],
    ['internet-ipv6-webrtc', 'External IPv6 and real WebRTC ICE candidates in standard and proxy modes.'],
    ['internet-sleep-switch', 'Sleep/resume and network changes while proxy protection is on.'],
    ['internet-smartscreen', 'Real SmartScreen warning page and download verdicts; Windows SmartScreen setting overriding VEIL.'],
    ['internet-site-compat', 'HTTPS-first and strict tracking prevention on real sites.'],
];

/** Tools a runner needs on PATH; a missing tool leaves that runner NOT RUN with the reason. */
export function makePlan(primaryParent, diskFullParent, missingTools = []) {
    const commands = mainRunners.map(([id, script, flags]) => ({
        id, lab: primaryParent, args: [script, '--profile-parent', primaryParent, ...flags],
    }));
    const local = localRunners.map(([id, script, flags, tools]) => {
        const missing = tools.filter(tool => missingTools.includes(tool));
        return missing.length ? { id, reason: `Missing on PATH: ${missing.join(', ')}.` }
            : { id, lab: primaryParent, cwd: primaryParent, args: [script, ...flags] };
    });
    return [
        ...commands,
        ...local,
        diskFullParent
            ? { id: 'profile-disk-full', args: ['tests/phase3/disk-full.mjs', '--profile-parent', diskFullParent] }
            : { id: 'profile-disk-full', reason: 'No separately verified small BitLocker volume.' },
        ...manual.map(([id, reason]) => ({ id, reason })),
        ...internet.map(([id, reason]) => ({ id, reason })),
    ];
}

export async function executePlan(plan, only, invoke, save) {
    const results = plan.map(test => ({
        ...test, status: 'NOT RUN',
        ...(only.length && !only.includes(test.id) && test.args ? { reason: 'Excluded by --only.' } : {}),
    }));
    for (const id of only) {
        if (!results.some(test => test.id === id)) throw new Error(`Unknown test ID: ${id}`);
    }
    await save(results);
    if (!invoke) return results;
    for (const test of results) {
        if (!test.args || (only.length && !only.includes(test.id))) continue;
        const started = Date.now();
        test.status = 'RUNNING';
        await save(results);
        const result = await invoke(test).catch(error => ({ exitCode: 1, error: error.message }));
        Object.assign(test, result, { status: result.exitCode === 0 ? 'PASS' : 'FAIL', durationMs: Date.now() - started });
        await save(results);
    }
    return results;
}

export function validatePreflight(receipt, guest) {
    if (receipt.schema !== 1 || !guest.virtualBox || !guest.noDefaultRoute || guest.elevated ||
        !guest.uuid || guest.uuid !== receipt.guest?.uuid || guest.boot !== receipt.guest?.boot ||
        !receipt.guest?.elevated) {
        throw new Error('Use the same offline VirtualBox guest and boot as elevated preflight, from a normal-user shell.');
    }
    const primary = receipt.volumes?.find(volume => volume.role === 'primary');
    const diskFull = receipt.volumes?.find(volume => volume.role === 'disk-full');
    if (!primary || receipt.volumes.length !== (diskFull ? 2 : 1)) throw new Error('Invalid preflight volume list.');
    for (const volume of receipt.volumes) {
        if (!/^[a-z]:$/i.test(volume.drive) || volume.drive.toLowerCase() === guest.systemDrive.toLowerCase() ||
            win32.parse(volume.parent).root.toLowerCase() !== `${volume.drive.toLowerCase()}\\` ||
            win32.normalize(volume.parent).toLowerCase() === `${volume.drive.toLowerCase()}\\` ||
            !/^\\\\\?\\Volume\{[a-f\d-]{36}\}\\$/i.test(volume.volumeId)) {
            throw new Error('The preflight lab path must be on its registered non-system volume.');
        }
    }
    if (diskFull && (diskFull.volumeId.toLowerCase() === primary.volumeId.toLowerCase() ||
        diskFull.image.toLowerCase() === primary.image.toLowerCase() ||
        diskFull.drive.toLowerCase() === primary.drive.toLowerCase() ||
        !(diskFull.capacity > 0 && diskFull.capacity <= 2 * 1024 ** 3))) {
        throw new Error('Disk-full testing requires a different small lab VHDX.');
    }
    return { primary, diskFull };
}

async function makeLabFolder(volume) {
    const mountvol = join(process.env.SystemRoot, 'System32', 'mountvol.exe');
    const { stdout } = await run(mountvol, [`${volume.drive}\\`, '/L']);
    if (stdout.trim().toLowerCase() !== volume.volumeId.toLowerCase()) {
        throw new Error(`The volume at ${volume.drive} changed after preflight.`);
    }
    const canonical = await realpath(volume.parent);
    if (win32.normalize(canonical).toLowerCase() !== win32.normalize(volume.parent).toLowerCase()) {
        throw new Error('The lab folder was redirected after preflight.');
    }
    return mkdtemp(join(canonical, 'veil-phase34-'));
}

async function main() {
    const { values } = parseArgs({ options: {
        plan: { type: 'boolean' }, run: { type: 'boolean' }, 'isolated-vm': { type: 'boolean' }, preflight: { type: 'string' },
        'desktop-dll': { type: 'string' }, only: { type: 'string', multiple: true, default: [] },
        'cleanup-on-pass': { type: 'boolean' },
    } });
    if (values.plan && values.run) throw new Error('Choose --plan or --run.');
    const preview = makePlan('<verified-protected-folder>', '<optional-small-volume-folder>');
    if (!values.run) {
        await executePlan(preview, values.only, null, tests => console.log(JSON.stringify({ mode: 'PLAN', tests }, null, 2)));
        return;
    }
    await executePlan(preview, values.only, null, () => {});
    if (process.platform !== 'win32' || !values['isolated-vm'] || !values.preflight) {
        throw new Error('Use --run --isolated-vm --preflight <receipt> in the prepared guest.');
    }
    const receipt = JSON.parse((await readFile(values.preflight, 'utf8')).replace(/^\uFEFF/, ''));
    const powershell = join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    const { stdout } = await run(powershell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File',
        join(repo, 'tests/phase3/phase34-guest-identity.ps1'), '-Json']);
    const guest = JSON.parse(stdout);
    const { primary, diskFull } = validatePreflight(receipt, guest);
    const desktop = values['desktop-dll'] ?? join(repo, 'apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
    if (!isAbsolute(desktop) || !(await stat(desktop)).isFile()) throw new Error('A prebuilt Debug desktop DLL is required.');
    const vault = join(dirname(desktop), 'veil-vault.exe');
    const hash = async path => createHash('sha256').update(await readFile(path)).digest('hex');
    const build = { desktopSha256: await hash(desktop), vaultSha256: await hash(vault) };

    const root = join(repo, '.veil-dev');
    await mkdir(root, { recursive: true });
    const evidence = await mkdtemp(join(root, 'phase34-vm-'));
    const lab = await makeLabFolder(primary);
    const smallLab = diskFull ? await makeLabFolder(diskFull) : undefined;
    const missingTools = [];
    for (const tool of ['openssl']) {
        await run(tool, ['version'], { windowsHide: true }).catch(() => missingTools.push(tool));
    }
    const plan = makePlan(lab, smallLab, missingTools);
    const report = join(evidence, 'summary.json');
    const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^VEIL_(DEV_VAULT|TEST_)/i.test(key)));
    env.VEIL_DESKTOP_DLL = desktop;
    const results = await executePlan(plan, values.only, async test => {
        const log = `${test.id}.log`;
        const before = test.lab ? new Set(await readdir(test.lab)) : null;
        const args = test.cwd ? [join(repo, test.args[0]), ...test.args.slice(1)] : test.args;
        const result = await run(process.execPath, args, {
            cwd: test.cwd ?? repo, env, windowsHide: true, maxBuffer: 4 * 1024 ** 2, timeout: 20 * 60_000,
        })
            .then(result => ({ ...result, code: 0 }), error => error);
        await writeFile(join(evidence, log), `${result.stdout ?? ''}\n${result.stderr ?? ''}`);
        const artifacts = [];
        if (before) {
            for (const entry of await readdir(test.lab, { withFileTypes: true })) {
                if (!entry.isDirectory() || before.has(entry.name)) continue;
                const source = join(test.lab, entry.name);
                for (const file of await readdir(source, { withFileTypes: true })) {
                    if (!file.isFile() || !file.name.endsWith('.json')) continue;
                    const artifact = `${test.id}-${entry.name}-${file.name}`;
                    await copyFile(join(source, file.name), join(evidence, artifact));
                    artifacts.push(artifact);
                }
            }
        }
        return { exitCode: typeof result.code === 'number' ? result.code : 1, log, artifacts };
    }, async tests => {
        await writeFile(report + '.next', JSON.stringify({ scope: 'Debug probes on protected VM volumes', guest, build, lab, missingTools, tests }, null, 2));
        await rename(report + '.next', report);
    });
    console.log(`Evidence: ${report}`);
    for (const test of results) console.log(`${test.status}\t${test.id}`);
    if (results.some(test => test.status === 'FAIL')) process.exitCode = 1;
    else if (!results.some(test => test.status === 'PASS')) process.exitCode = 2;
    else if (values['cleanup-on-pass']) {
        for (const directory of [lab, smallLab].filter(Boolean)) {
            if ((await lstat(directory)).isSymbolicLink()) throw new Error('Refusing to remove a redirected lab directory.');
            await rm(directory, { recursive: true });
        }
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
