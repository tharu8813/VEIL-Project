// Host side of the single Phase 3/4/5 offline VM batch (docs/10-test-strategy.md).
//
//   node tests/vm/make-bundle.mjs [--vm Veil-VM-TESTER]
//
// Builds the Debug desktop (with veil-vault.exe) from the committed tree, packs the committed source,
// the guest script and a SHA-256 manifest, and writes a VirtualBox virtual ISO (.viso) that can be
// attached to the VM as a DVD. Nothing is changed on the host system and nothing is attached here:
// the attach command is printed for the operator.
import { execFile } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { copyFile, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs, promisify } from 'node:util';

const run = promisify(execFile);
const repo = fileURLToPath(new URL('../../', import.meta.url));
const { values } = parseArgs({ options: { vm: { type: 'string', default: 'Veil-VM-TESTER' } } });

const git = async (...args) => (await run('git', args, { cwd: repo, maxBuffer: 64 * 1024 ** 2 })).stdout.trim();
if (await git('status', '--porcelain', '--untracked-files=no')) throw new Error('Commit or stash tracked changes first: the bundle must match one commit.');
const commit = await git('rev-parse', 'HEAD');
const short = commit.slice(0, 8);
const vault = join(repo, 'crates/veil-security/target/release/veil-vault.exe');
if (!(await stat(vault).catch(() => null))?.isFile()) throw new Error('Build crates/veil-security in release first (veil-vault.exe).');

const out = resolve(repo, '.veil-dev', `vm-batch-${short}`);
await rm(out, { recursive: true, force: true });
const desktop = join(out, 'build', 'desktop');
await mkdir(desktop, { recursive: true });

console.log(`commit ${commit}`);
await run('git', ['archive', '--format=zip', '-o', join(out, 'SOURCE.ZIP'), commit], { cwd: repo });
await run('dotnet', ['build', join(repo, 'apps/desktop'), '-c', 'Debug', '-nologo', '-v', 'q', `-p:OutputPath=${desktop}\\`],
    { cwd: repo, maxBuffer: 16 * 1024 ** 2 });
await copyFile(vault, join(desktop, 'veil-vault.exe'));
const powershell = join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
await run(powershell, ['-NoProfile', '-NonInteractive', '-Command',
    `Compress-Archive -Path '${desktop}\\*' -DestinationPath '${join(out, 'DESKTOP.ZIP')}'`]);
await rm(join(out, 'build'), { recursive: true });
await copyFile(join(repo, 'tests/vm/guest-batch.ps1'), join(out, 'BATCH.PS1'));

const files = {};
for (const name of (await readdir(out)).sort()) {
    files[name] = createHash('sha256').update(await readFile(join(out, name))).digest('hex');
}
await writeFile(join(out, 'MANIFEST.JSON'), JSON.stringify({ schema: 1, commit, created: new Date().toISOString(), files }, null, 2));

const entries = [...Object.keys(files), 'MANIFEST.JSON'].map(name => `'/${name}=${join(out, name).replace(/\\/g, '/')}'`);
const viso = join(out, 'VEILBAT.viso');
await writeFile(viso, [`--iprt-iso-maker-file-marker-bourne=${randomUUID()}`, `--volume-id=VEIL_${short.toUpperCase()}`, ...entries, ''].join('\n'));

console.log(`bundle ${out}`);
for (const [name, hash] of Object.entries(files)) console.log(`  ${hash}  ${name}`);
console.log('\nAttach to the powered-off VM (operator step):');
console.log(`  & "$env:ProgramFiles\\Oracle\\VirtualBox\\VBoxManage.exe" storageattach '${values.vm}' --storagectl SATA --port 1 --device 0 --type dvddrive --medium '${viso}'`);
console.log('Then in the guest follow docs/10-test-strategy.md "단일 VM 일괄 시험".');
