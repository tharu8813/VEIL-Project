// Host-runnable checks for the SEC-004 trace analyzer, using a synthetic Procmon CSV export.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';
import { analyze, parseCsv } from './outside-writes.mjs';

const header = '"Time of Day","Process Name","PID","Operation","Path","Result","Detail"';
const row = (name, pid, operation, path, result = 'SUCCESS', detail = '') =>
    [ '1:00:00.0000000 PM', name, pid, operation, path, result, detail ]
        .map(value => `"${String(value).replace(/"/g, '""')}"`).join(',');

const rows = [
    row('node.exe', 100, 'Process Create', 'C:\\Program Files\\dotnet\\dotnet.exe', 'SUCCESS', 'PID: 200, Command line: dotnet Veil.Desktop.dll'),
    row('dotnet.exe', 200, 'Process Create', 'C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\msedgewebview2.exe', 'SUCCESS', 'PID: 300, Command line: "msedgewebview2.exe" --type=browser'),
    row('dotnet.exe', 200, 'WriteFile', 'V:\\VEIL-Lab\\p\\browser-state.json.new', 'SUCCESS', 'Offset: 0, Length: 120'),
    row('dotnet.exe', 200, 'SetRenameInformationFile', 'V:\\VEIL-Lab\\p\\browser-state.json.new', 'SUCCESS', 'ReplaceIfExists: True, FileName: V:\\VEIL-Lab\\p\\browser-state.json'),
    row('msedgewebview2.exe', 300, 'WriteFile', 'V:\\VEIL-Lab\\p\\webview2\\EBWebView\\Default\\Cookies', 'SUCCESS', 'Offset: 0, Length: 4096'),
    // Leaks the analyzer must report
    row('msedgewebview2.exe', 300, 'CreateFile', 'C:\\Users\\lab\\AppData\\Local\\Temp\\scoped_dir1\\x.tmp', 'SUCCESS', 'Desired Access: Generic Write, Disposition: Create, OpenResult: Created'),
    row('msedgewebview2.exe', 300, 'WriteFile', 'C:\\Users\\lab\\AppData\\Local\\Temp\\scoped_dir1\\x.tmp', 'SUCCESS', 'Offset: 0, Length: 10'),
    row('dotnet.exe', 200, 'SetRenameInformationFile', 'V:\\VEIL-Lab\\p\\a.tmp', 'SUCCESS', 'ReplaceIfExists: False, FileName: C:\\Users\\lab\\moved.bin'),
    row('msedgewebview2.exe', 300, 'CreateFile', 'C:\\Users\\lab\\mapped.dat', 'SUCCESS', 'Desired Access: Generic Read/Write, OpenResult: Opened'),
    row('System', 4, 'WriteFile', 'C:\\Users\\lab\\mapped.dat', 'SUCCESS', 'Offset: 0, Length: 4096, I/O Flags: Non-cached, Paging I/O, Synchronous Paging I/O'),
    // Not leaks
    row('dotnet.exe', 200, 'WriteFile', 'C:\\VEIL-Project\\.veil-dev\\desktop-test-x\\write.json', 'SUCCESS', 'Offset: 0, Length: 50'),
    row('dotnet.exe', 200, 'WriteFile', 'C:\\Users\\lab\\denied.txt', 'ACCESS DENIED', ''),
    row('dotnet.exe', 200, 'WriteFile', '\\Device\\NamedPipe\\VEIL.Lab.broker', 'SUCCESS', ''),
    row('dotnet.exe', 200, 'CreateFile', 'C:\\Windows\\Fonts\\segoeui.ttf', 'SUCCESS', 'Desired Access: Generic Read, OpenResult: Opened'),
    row('explorer.exe', 900, 'WriteFile', 'C:\\Users\\lab\\unrelated.txt', 'SUCCESS', ''),
    row('System', 4, 'WriteFile', 'C:\\pagefile.sys', 'SUCCESS', 'Paging I/O'),
    // Directory opened by the tree, then its index updated by System: not a file write
    row('dotnet.exe', 200, 'CreateFile', 'C:\\Windows', 'SUCCESS', 'Desired Access: Read Data/List Directory, Synchronize, Disposition: Open, Options: Directory, Synchronous IO Non-Alert, Attributes: n/a, ShareMode: Read, Write, OpenResult: Opened'),
    row('System', 4, 'WriteFile', 'C:\\Windows', 'SUCCESS', 'Offset: 0, Length: 4096, I/O Flags: Paging I/O'),
    // Folder opened without the Directory option; System updates its index after the tree uses a file in it
    row('dotnet.exe', 200, 'CreateFile', 'C:\\Users\\lab', 'SUCCESS', 'Desired Access: Read Attributes, Disposition: Open, Options: Open Reparse Point, Attributes: n/a, OpenResult: Opened'),
    row('System', 4, 'WriteFile', 'C:\\Users\\lab', 'SUCCESS', 'Offset: 0, Length: 4096, I/O Flags: Paging I/O'),
    // NTFS bookkeeping caused by the tree's own file operations on C:
    row('msedgewebview2.exe', 300, 'WriteFile', 'C:\\$LogFile', 'SUCCESS', 'Offset: 0, Length: 4096'),
    row('msedgewebview2.exe', 300, 'WriteFile', 'C:\\$Mft', 'SUCCESS', 'Offset: 0, Length: 1024'),
];
const csv = lines => [header, ...lines].join('\r\n');
const trace = csv(rows);
const options = { rootPids: ['100'], volumes: ['V:\\'], allow: ['C:\\VEIL-Project\\.veil-dev\\desktop-test-x'] };

test('parses quoted Procmon CSV fields', () => {
    const [first] = parseCsv('\uFEFF"A","B"\r\n"x, ""y""","2"\r\n');
    assert.deepEqual(first, { A: 'x, "y"', B: '2' });
});

test('follows the process tree and separates inside, allowed and outside writes', () => {
    const report = analyze(parseCsv(trace), options);
    assert.deepEqual(report.processes.map(p => p.pid).sort(), ['100', '200', '300']);
    assert.equal(report.writes.inside, 3);
    assert.equal(report.writes.allowed, 1);
    assert.equal(report.pass, false);
    const paths = Object.fromEntries(report.outside.map(e => [e.path, e.kinds.sort().join('+')]));
    assert.deepEqual(paths, {
        'C:\\Users\\lab\\AppData\\Local\\Temp\\scoped_dir1\\x.tmp': 'create+direct',
        'C:\\Users\\lab\\moved.bin': 'direct',
        'C:\\Users\\lab\\mapped.dat': 'mapped',
    });
});

test('NTFS metadata writes are side effects, not leaks', () => {
    const report = analyze(parseCsv(trace), options);
    assert.deepEqual(report.metadata, { 'C:\\$LogFile': 1, 'C:\\$Mft': 1 });
    assert.deepEqual(report.directories, { 'C:\\Users\\lab': 1 });
    const metaOnly = analyze(parseCsv(csv([rows[0], rows[1], rows.at(-2), rows.at(-1)])), options);
    assert.equal(metaOnly.pass, true);
});

test('unrelated processes, failed writes, pipes and the page file are not attributed', () => {
    const report = analyze(parseCsv(trace), options);
    const all = report.outside.map(e => e.path.toLowerCase()).join('\n');
    for (const path of ['unrelated.txt', 'denied.txt', 'namedpipe', 'pagefile.sys', 'segoeui.ttf'])
        assert.ok(!all.includes(path), path);
});

test('--root-command roots the tree at the matching child process', () => {
    const launcher = row('powershell.exe', 50, 'Process Create', 'C:\\Program Files\\nodejs\\node.exe', 'SUCCESS',
        'PID: 100, Command line: node tests/integration/desktop.mjs --profile-parent V:\\VEIL-Lab');
    const report = analyze(parseCsv(csv([launcher, ...rows])),
        { rootCommands: ['integration/desktop.mjs'], volumes: ['V:\\'], allow: options.allow });
    assert.deepEqual(report.processes.map(p => p.pid).sort(), ['100', '200', '300']);
    assert.equal(report.outside.length, 3);
});

test('a Parent PID column also links children', () => {
    const text = ['"Process Name","PID","Parent PID","Operation","Path","Result","Detail"',
        '"child.exe","7","100","WriteFile","C:\\leak.bin","SUCCESS",""'].join('\n');
    assert.equal(analyze(parseCsv(text), options).outside[0].path, 'C:\\leak.bin');
});

test('Phase 3/4 trace requires protected writes and exempts only the Node evidence writer', () => {
    const phase34 = { rootPids: [100], volumes: ['V:\\'], allowNode: ['C:\\evidence'], requireActivity: true };
    assert.equal(analyze([], phase34).pass, false);
    assert.equal(analyze(parseCsv(csv([
        row('node.exe', 100, 'WriteFile', 'V:\\fixture\\summary.json', 'SUCCESS', ''),
    ])), phase34).pass, false);
    const writes = [
        row('node.exe', 100, 'WriteFile', 'C:\\evidence\\summary.json', 'SUCCESS', ''),
        row('msedgewebview2.exe', 100, 'WriteFile', 'V:\\fixture\\profile.bin', 'SUCCESS', ''),
        row('browser.exe', 100, 'WriteFile', 'C:\\evidence\\secret.bin', 'SUCCESS', ''),
    ];
    const report = analyze(parseCsv(csv(writes)), phase34);
    assert.equal(report.pass, false);
    assert.equal(report.writes.allowed, 1);
    assert.equal(report.outside[0].path, 'C:\\evidence\\secret.bin');
    assert.equal(analyze(parseCsv(csv(writes.slice(0, 1))), phase34).pass, false);
    assert.equal(analyze(parseCsv(csv(writes.slice(0, 2))), phase34).pass, true);
});

test('CLI exits 1 on outside writes and 0 when everything stays on the volume', async () => {
    const run = promisify(execFile);
    await mkdir(resolve('.veil-dev'), { recursive: true });
    const directory = await mkdtemp(join(resolve('.veil-dev'), 'outside-writes-'));
    const leaky = join(directory, 'leaky.csv'), clean = join(directory, 'clean.csv');
    await writeFile(leaky, trace);
    await writeFile(clean, csv(rows.slice(0, 5)));
    const script = resolve('tests/security/outside-writes.mjs');
    const args = ['--root-pid', '100', '--volume', 'V:\\'];
    await assert.rejects(run('node', [script, leaky, ...args]), error => error.code === 1);
    const ok = await run('node', [script, clean, ...args, '--out', join(directory, 'clean.json')]);
    assert.match(ok.stdout, /^PASS/);
    assert.equal(JSON.parse(await readFile(join(directory, 'clean.json'), 'utf8')).pass, true);
});

test('VM batch findings: PID reuse, runner flush, harness helpers and Prefetch are separated; the jump list still fails', () => {
    const vm = { rootPids: [100], volumes: ['V:\\'], allowNode: ['C:\\VEIL-Batch\\src\\.veil-dev'], harnessProcesses: ['powershell.exe'] };
    const events = [
        row('node.exe', 100, 'Process Create', 'C:\\dotnet.exe', 'SUCCESS', 'PID: 200, Command line: dotnet Veil.Desktop.dll --smoke'),
        row('dotnet.exe', 200, 'Process Exit', '', 'SUCCESS', ''),
        // PID 200 reused by an unrelated program: its writes are not the tree's.
        row('svchost.exe', 50, 'Process Create', 'C:\\other.exe', 'SUCCESS', 'PID: 200, Command line: other.exe'),
        row('other.exe', 200, 'WriteFile', 'C:\\Users\\lab\\AppData\\Local\\Temp\\other.tmp', 'SUCCESS', ''),
        // Runner evidence written by node, later flushed by System.
        row('node.exe', 100, 'CreateFile', 'C:\\VEIL-Batch\\src\\.veil-dev\\run\\summary.json', 'SUCCESS', 'OpenResult: Created'),
        row('System', 4, 'WriteFile', 'C:\\VEIL-Batch\\src\\.veil-dev\\run\\summary.json', 'SUCCESS', 'I/O Flags: Paging I/O'),
        // Harness PowerShell started by the runner.
        row('node.exe', 100, 'Process Create', 'C:\\powershell.exe', 'SUCCESS', 'PID: 400, Command line: powershell -NoProfile'),
        row('powershell.exe', 400, 'CreateFile', 'C:\\Users\\lab\\AppData\\Local\\Temp\\__PSScriptPolicyTest_x.ps1', 'SUCCESS', 'OpenResult: Created'),
        // Prefetch flushed by System after the loader opened it for a tree process.
        row('node.exe', 100, 'CreateFile', 'C:\\Windows\\Prefetch\\NODE.EXE-1234.pf', 'SUCCESS', 'OpenResult: Opened'),
        row('System', 4, 'WriteFile', 'C:\\Windows\\Prefetch\\NODE.EXE-1234.pf', 'SUCCESS', 'I/O Flags: Paging I/O'),
        // A test-mode launch may write exactly the report file named on its command line.
        row('node.exe', 100, 'Process Create', 'C:\\dotnet.exe', 'SUCCESS', 'PID: 500, Command line: dotnet C:\\b\\Veil.Desktop.dll --smoke V:\\VEIL-Lab\\p http://127.0.0.1:1 write C:\\VEIL-Batch\\src\\.veil-dev\\desktop-test-x\\write.json'),
        row('dotnet.exe', 500, 'WriteFile', 'C:\\VEIL-Batch\\src\\.veil-dev\\desktop-test-x\\write.json', 'SUCCESS', ''),
        row('node.exe', 100, 'Process Create', 'C:\\msedgewebview2.exe', 'SUCCESS', 'PID: 300, Command line: msedgewebview2.exe'),
        row('msedgewebview2.exe', 300, 'WriteFile', 'V:\\VEIL-Lab\\p\\Cookies', 'SUCCESS', ''),
        // Engine jump list (known leak) must still fail the trace.
        row('msedgewebview2.exe', 300, 'CreateFile', 'C:\\Users\\lab\\AppData\\Roaming\\Microsoft\\Windows\\Recent\\CustomDestinations\\x.tmp', 'SUCCESS', 'OpenResult: Created'),
    ];
    const report = analyze(parseCsv(csv(events)), vm);
    assert.deepEqual(report.outside.map(entry => entry.path), ['C:\\Users\\lab\\AppData\\Roaming\\Microsoft\\Windows\\Recent\\CustomDestinations\\x.tmp']);
    assert.equal(report.pass, false);
    assert.equal(report.writes.allowed, 3);
    assert.deepEqual(Object.keys(report.harness), ['C:\\Users\\lab\\AppData\\Local\\Temp\\__PSScriptPolicyTest_x.ps1']);
    assert.deepEqual(Object.keys(report.osRecords), ['C:\\Windows\\Prefetch\\NODE.EXE-1234.pf']);
    assert.ok(report.processes.some(p => p.pid === '200'), 'exited process stays in the report');
    // Without the jump list write the same trace passes.
    assert.equal(analyze(parseCsv(csv(events.slice(0, -1))), vm).pass, true);
    // Engine writes are never treated as harness, even to the same TEMP folder.
    const engineTemp = analyze(parseCsv(csv([...events.slice(0, -1),
        row('msedgewebview2.exe', 300, 'CreateFile', 'C:\\Users\\lab\\AppData\\Local\\Temp\\e.tmp', 'SUCCESS', 'OpenResult: Created')])), vm);
    assert.equal(engineTemp.pass, false);
    // Any other file next to the report still counts as outside.
    const sibling = analyze(parseCsv(csv([...events.slice(0, -1),
        row('dotnet.exe', 500, 'WriteFile', 'C:\\VEIL-Batch\\src\\.veil-dev\\desktop-test-x\\other.bin', 'SUCCESS', '')])), vm);
    assert.equal(sibling.pass, false);
});
