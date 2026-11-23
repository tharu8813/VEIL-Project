// SEC-004: classify file writes of a process tree from a Process Monitor CSV export.
//
//   node tests/security/outside-writes.mjs <trace.csv> (--root-pid <pid> | --root-command <text>) --volume V:\
//       [--allow <path-prefix>]... [--out report.json]
//
// A write is any successful WriteFile, SetEndOfFile/Allocation, rename target, or a CreateFile that
// created/overwrote a file. --root-command roots the tree at every process whose creation command line
// contains the text. Descendants are followed through "Process Create" events
// (and a "Parent PID" column when present). Paging writes by the System process to a file the tree
// opened are reported as mapped writes. Exit code 1 when any write lands outside --volume and is not
// covered by --allow. The trace itself must be captured in the isolated VM (Procmon needs admin).
// The CSV is streamed line by line, so multi-hundred-MB traces fit in memory.
import { createReadStream } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { pathToFileURL } from 'node:url';

const WRITE_OPERATIONS = new Set([
    'WriteFile', 'SetEndOfFileInformationFile', 'SetAllocationInformationFile', 'SetRenameInformationFile',
]);

/** Splits one CSV record into fields (quoted fields, doubled quotes). */
function splitRecord(line) {
    const fields = [];
    let field = '', quoted = false;
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (quoted) {
            if (c === '"' && line[i + 1] === '"') { field += '"'; i++; }
            else if (c === '"') quoted = false;
            else field += c;
        } else if (c === '"') quoted = true;
        else if (c === ',') { fields.push(field); field = ''; }
        else field += c;
    }
    fields.push(field);
    return fields;
}

/** Joins physical lines into records (a record with unbalanced quotes continues on the next line)
 *  and keys them by the header row. Calls onRecord for each data record. */
function recordReader(onRecord) {
    let header = null, pending = '';
    return line => {
        if (header === null) line = line.replace(/^\uFEFF/, '');
        pending = pending ? `${pending}\n${line}` : line;
        if ((pending.match(/"/g)?.length ?? 0) % 2 === 1) return;
        const record = pending;
        pending = '';
        if (record === '') return;
        const values = splitRecord(record);
        if (header === null) { header = values.map(name => name.trim()); return; }
        onRecord(Object.fromEntries(header.map((name, i) => [name, values[i] ?? ''])));
    };
}

/** Whole-text convenience parser (tests and small traces). */
export function parseCsv(text) {
    const records = [];
    const read = recordReader(record => records.push(record));
    for (const line of text.split(/\r?\n/)) read(line);
    return records;
}

const normalize = path => path.replace(/\//g, '\\').toLowerCase();
const prefix = path => { const p = normalize(path); return p.endsWith('\\') ? p : p + '\\'; };
const isDrivePath = path => /^[a-z]:\\/i.test(path);

function writeTarget(event) {
    if (event.Operation === 'SetRenameInformationFile') {
        const target = /FileName:\s*(.+?)(?:,\s*\w+:|$)/.exec(event.Detail)?.[1]?.trim();
        return target || event.Path;
    }
    return event.Path;
}

/** True when a CreateFile opened a directory ("Options: Directory, ..."; not "Non-Directory File"). */
function opensDirectory(event) {
    const options = /Options:\s*(.*?)(?:,\s*Attributes:|,\s*ShareMode:|$)/.exec(event.Detail)?.[1] ?? '';
    return options.split(',').some(option => option.trim() === 'Directory');
}

function createsFile(event) {
    if (event.Operation !== 'CreateFile' || event.Result !== 'SUCCESS') return false;
    return /OpenResult:\s*(Created|Overwritten|Superseded)/i.test(event.Detail);
}

/** Incremental analyzer: add(event) for each record in trace order, then report(). */
/**
 * harnessProcesses: image names of test-harness helpers in the tree (e.g. powershell.exe used by the
 * runners). VEIL never starts them, so their writes are reported separately and do not fail the trace.
 */
export function createAnalyzer({ rootPids = [], rootCommands = [], volumes, allow = [], allowNode = [], requireActivity = false,
    harnessProcesses = [] }) {
    const tree = new Map();
    const members = [];
    const join = (pid, parent) => { tree.set(pid, parent); members.push({ pid, parent }); };
    for (const pid of rootPids) join(String(pid), 'root');
    const volumePrefixes = volumes.map(prefix);
    const allowPrefixes = allow.map(prefix);
    const nodePrefixes = allowNode.map(prefix);
    const commands = rootCommands.map(normalize);
    const harnessNames = new Set(harnessProcesses.map(name => name.toLowerCase()));
    // File -> image name of the tree process that opened it (attributes later paging writes).
    const opened = new Map();
    let inside = 0, engineInside = 0, allowed = 0;
    const outside = new Map();
    const metadata = new Map();
    const harness = new Map();
    const osRecords = new Map();
    const names = new Map();
    // Exact report files named as the last argument of a test-mode launch (--smoke/--probe ... <report>).
    // The harness chooses where they go (a disk-full run cannot put them on its full volume).
    const reports = new Set();
    const count = (map, path) => map.set(path, (map.get(path) ?? 0) + 1);

    const classify = (path, event, kind) => {
        if (!isDrivePath(path)) return;
        const key = normalize(path);
        const writer = (kind === 'mapped' ? opened.get(key) : event['Process Name']) ?? '';
        if (volumePrefixes.some(p => key.startsWith(p) || key + '\\' === p)) {
            inside++;
            if (/^msedgewebview2\.exe$/i.test(event['Process Name'])) engineInside++;
            return;
        }
        if (allowPrefixes.some(p => key.startsWith(p) || key + '\\' === p)) { allowed++; return; }
        if (reports.has(key)) { allowed++; return; }
        // The Node runner's evidence folder: its own writes and the System flush of files it wrote.
        if (/^node(\.exe)?$/i.test(writer) && nodePrefixes.some(p => key.startsWith(p) || key + '\\' === p)) {
            allowed++;
            return;
        }
        // NTFS journal/MFT updates follow file operations on that volume; they hold no file data
        // and are listed as side effects. The file operation that caused them is reported itself.
        if (/^[a-z]:\\\$/.test(key)) { count(metadata, path); return; }
        if (harnessNames.has(writer.toLowerCase())) { count(harness, path); return; }
        // Windows Prefetch: the OS records launched images (and files they touched early) for every
        // program. Reported separately as an OS record; it is not a write by VEIL.
        if (kind === 'mapped' && /^[a-z]:\\windows\\prefetch\\[^\\]+\.pf$/.test(key)) { count(osRecords, path); return; }
        const entry = outside.get(key) ?? { path, count: 0, operations: {}, processes: new Set(), kinds: new Set() };
        entry.count++;
        entry.operations[event.Operation] = (entry.operations[event.Operation] ?? 0) + 1;
        entry.processes.add(`${event['Process Name']} (${event.PID})`);
        entry.kinds.add(kind);
        outside.set(key, entry);
    };

    const add = event => {
        const pid = String(event.PID);
        const created = event.Operation === 'Process Create' && event.Result === 'SUCCESS'
            ? /PID:\s*(\d+)/.exec(event.Detail)?.[1] : undefined;
        // Windows reuses PIDs: a process created outside the tree under a PID the tree used before is
        // a different process and must not inherit the tree's membership.
        if (created && !tree.has(pid)) tree.delete(created);
        if (created && commands.length && commands.some(text => normalize(event.Detail).includes(text)) && !tree.has(created))
            join(created, 'root');
        if (!tree.has(pid) && event['Parent PID'] && tree.has(String(event['Parent PID']))) join(pid, String(event['Parent PID']));
        if (tree.has(pid)) {
            names.set(pid, event['Process Name']);
            if (created) {
                join(created, pid);
                const report = /\s--(?:smoke|probe)\s.*\s"?([A-Za-z]:\\[^"]+?\.json)"?\s*$/.exec(event.Detail)?.[1];
                if (report) reports.add(normalize(report));
            }
            // Track files (not directories: the System process updates directory indexes on its own)
            // so later paging writes to them can be attributed to this tree.
            if (event.Operation === 'CreateFile' && event.Result === 'SUCCESS' && isDrivePath(event.Path) && !opensDirectory(event) &&
                !opened.has(normalize(event.Path)))
                opened.set(normalize(event.Path), event['Process Name']);
            if (event.Operation === 'Process Exit') { tree.delete(pid); return; }
            if (event.Result !== 'SUCCESS') return;
            if (WRITE_OPERATIONS.has(event.Operation)) classify(writeTarget(event), event, 'direct');
            else if (createsFile(event)) classify(event.Path, event, 'create');
        } else if (pid === '4' && event.Operation === 'WriteFile' && event.Result === 'SUCCESS' &&
                   /Paging I\/O/i.test(event.Detail) && opened.has(normalize(event.Path))) {
            classify(event.Path, event, 'mapped');
        }
    };

    const report = () => {
        // A paging write to a folder that contains files the tree used is a directory-index update
        // (even when the folder was not opened with the Directory option): list it as a side effect.
        const directories = {};
        for (const [key, entry] of outside) {
            if (!entry.kinds.has('mapped') || entry.kinds.size !== 1) continue;
            const parent = key.endsWith('\\') ? key : key + '\\';
            if ([...opened.keys()].some(path => path.startsWith(parent))) {
                directories[entry.path] = entry.count;
                outside.delete(key);
            }
        }
        const outsideList = [...outside.values()]
            .map(entry => ({ ...entry, processes: [...entry.processes], kinds: [...entry.kinds] }))
            .sort((a, b) => b.count - a.count);
        return {
            processes: members.map(({ pid, parent }) => ({ pid, parent, name: names.get(pid) ?? null })),
            writes: { inside, allowed, outside: outsideList.reduce((n, e) => n + e.count, 0) },
            engineWritesInside: engineInside,
            outside: outsideList,
            metadata: Object.fromEntries(metadata),
            harness: Object.fromEntries(harness),
            osRecords: Object.fromEntries(osRecords),
            directories,
            pass: outsideList.length === 0 && (!requireActivity || engineInside > 0),
        };
    };
    return { add, report };
}

export function analyze(events, options) {
    const analyzer = createAnalyzer(options);
    for (const event of events) analyzer.add(event);
    return analyzer.report();
}

/** Streams a Procmon CSV file through the analyzer without holding it in memory. */
export async function analyzeFile(path, options) {
    const analyzer = createAnalyzer(options);
    const read = recordReader(analyzer.add);
    const lines = createInterface({ input: createReadStream(path, { encoding: 'utf8' }), crlfDelay: Infinity });
    for await (const line of lines) read(line);
    return analyzer.report();
}

function parseArgs(argv) {
    const [trace, ...rest] = argv;
    const options = { trace, rootPids: [], rootCommands: [], volumes: [], allow: [], allowNode: [], harnessProcesses: [], out: null };
    for (let i = 0; i < rest.length; i += 2) {
        const [flag, value] = [rest[i], rest[i + 1]];
        if (value === undefined) throw new Error(`Missing value for ${flag}`);
        if (flag === '--root-pid') options.rootPids.push(value);
        else if (flag === '--root-command') options.rootCommands.push(value);
        else if (flag === '--volume') options.volumes.push(value);
        else if (flag === '--allow') options.allow.push(value);
        else if (flag === '--allow-node') options.allowNode.push(value);
        else if (flag === '--harness-process') options.harnessProcesses.push(value);
        else if (flag === '--require-activity' && value === 'true') options.requireActivity = true;
        else if (flag === '--out') options.out = value;
        else throw new Error(`Unknown option ${flag}`);
    }
    if (!trace || !(options.rootPids.length || options.rootCommands.length) || !options.volumes.length)
        throw new Error('Usage: outside-writes.mjs <trace.csv> (--root-pid <pid> | --root-command <text>) --volume <X:\\> [--allow <prefix>]... [--out <json>]');
    return options;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
    const options = parseArgs(process.argv.slice(2));
    const report = await analyzeFile(options.trace, options);
    if (options.out) await writeFile(options.out, JSON.stringify(report, null, 2));
    console.log(`${report.pass ? 'PASS' : 'FAIL'} writes inside=${report.writes.inside} allowed=${report.writes.allowed} outside=${report.writes.outside} processes=${report.processes.length}`);
    if (options.requireActivity && !report.engineWritesInside) console.log('No WebView2 writes to the protected volume were captured.');
    for (const entry of report.outside.slice(0, 25))
        console.log(`  ${entry.count}\t${entry.kinds.join('+')}\t${entry.path}\t${entry.processes.join(', ')}`);
    const meta = Object.entries(report.metadata);
    if (meta.length) console.log(`  NTFS metadata side effects: ${meta.map(([path, n]) => `${path} x${n}`).join(', ')}`);
    const harness = Object.entries(report.harness);
    if (harness.length) console.log(`  Test-harness writes (not VEIL): ${harness.length} paths, ${harness.reduce((n, [, c]) => n + c, 0)} writes`);
    const records = Object.keys(report.osRecords);
    if (records.length) console.log(`  Windows Prefetch records (OS): ${records.join(', ')}`);
    const dirs = Object.keys(report.directories);
    if (dirs.length) console.log(`  Directory index side effects: ${dirs.join(', ')}`);
    process.exitCode = report.pass ? 0 : 1;
}
