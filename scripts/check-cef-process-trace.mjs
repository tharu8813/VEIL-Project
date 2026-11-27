// SPDX-License-Identifier: MPL-2.0
import { createHash } from 'node:crypto';
import { lstat, readFile } from 'node:fs/promises';
import { join, win32 } from 'node:path';
import { pathToFileURL } from 'node:url';
import { checkNestedEvidence } from './check-cef-nested-evidence.mjs';
import { parseCsv } from '../tests/security/outside-writes.mjs';

const require = (valid, reason) => { if (!valid) throw new Error(reason); };
const isPid = id => Number.isInteger(id) && id > 0 && id <= 0xffffffff;
const pathKey = path => win32.normalize(path).toLowerCase();
const parseJson = bytes => JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');

// The CSV must cover the same single invocation as the hash-bound bundle report.
// Pair events in capture order so an unrelated process reusing a PID stays outside the tree.
export function compareProcessTrace(events, { runnerPid, image, sample }) {
    require(isPid(runnerPid) && isPid(sample.rootPid) && runnerPid !== sample.rootPid &&
        [4, 5].includes(sample.schema) && Array.isArray(sample.jobNotifications), 'Missing runtime process identity');
    require(/^[a-z]:[\\/]/i.test(image) && win32.basename(image).toLowerCase() === 'veil-cef-smoke.exe',
        'Expected the absolute Windows fixture image path as recorded by Procmon');
    const expectedImage = pathKey(image);
    const active = new Map();
    const seen = new Set();
    const ended = new Set();
    const unexpectedImagePids = [];
    const ambiguousPids = new Set();
    let rootMatches = 0;
    for (const event of events) {
        const pid = Number(event.PID);
        const processName = event['Process Name']?.toLowerCase();
        if (!isPid(pid) || !processName) continue;
        if (event.Operation === 'Process Create' && event.Result === 'SUCCESS') {
            const created = Number(/^PID:\s*(\d+)(?:,|$)/.exec(event.Detail ?? '')?.[1]);
            if (!isPid(created) || !event.Path) continue;
            const root = pid === runnerPid && processName === 'cefnestedsmoke.exe' &&
                created === sample.rootPid && pathKey(event.Path) === expectedImage;
            if (root) rootMatches++;
            if (!root && !active.has(pid)) {
                if (active.has(created)) ambiguousPids.add(created);
                continue;
            }
            if (seen.has(created)) ambiguousPids.add(created);
            seen.add(created);
            active.set(created, win32.basename(event.Path).toLowerCase());
            if (pathKey(event.Path) !== expectedImage) unexpectedImagePids.push(created);
        } else if (event.Operation === 'Process Exit' && active.has(pid)) {
            if (active.get(pid) !== processName) {
                ambiguousPids.add(pid);
                continue;
            }
            active.delete(pid);
            ended.add(pid);
        }
    }
    const sorted = ids => [...ids].sort((a, b) => a - b);
    const difference = (left, right) => sorted([...left].filter(id => !right.has(id)));
    const unmatched = {};
    for (const job of ['outer', 'inner']) {
        const notifications = sample.jobNotifications.filter(event => event.Job === job);
        const starts = new Set(notifications.filter(event => event.Kind === 'start').map(event => event.Pid));
        const exits = new Set(notifications.filter(event => event.Kind === 'exit' ||
            event.Kind === 'abnormal-exit').map(event => event.Pid));
        unmatched[job] = {
            osStartsWithoutJob: difference(seen, starts),
            jobStartsWithoutOs: difference(starts, seen),
            osExitsWithoutJob: difference(ended, exits),
            jobExitsWithoutOs: difference(exits, ended),
        };
    }
    const sampled = new Set([sample.rootPid, ...sample.observed.map(entry => entry.Pid)]);
    const retainedExits = sample.schema === 5 ? new Set(sample.handleExits.map(entry => entry.Pid)) : null;
    const unobservedSamples = difference(sampled, seen);
    const agrees = rootMatches === 1 && active.size === 0 && unexpectedImagePids.length === 0 &&
        ambiguousPids.size === 0 && unobservedSamples.length === 0 &&
        Object.values(unmatched).every(job => Object.values(job).every(ids => ids.length === 0));
    return {
        status: agrees ? 'MATCHED_CAPTURE' : 'INCOMPLETE_OR_MISMATCHED_CAPTURE',
        rootMatches,
        traceStarts: seen.size,
        traceExits: ended.size,
        tracePidsWithoutLiveSample: difference(seen, sampled),
        liveSamplesWithoutTraceStart: unobservedSamples,
        missingExitPids: sorted(active.keys()),
        traceExitsWithoutRetainedHandle: retainedExits ? difference(ended, retainedExits) : null,
        retainedHandleExitsWithoutTrace: retainedExits ? difference(retainedExits, ended) : null,
        unexpectedImagePids: sorted(unexpectedImagePids),
        ambiguousPids: sorted(ambiguousPids),
        unmatched,
        productReleaseGates: 'NOT RUN',
        limitations: 'Correlates one exported capture; capture loss and full process-handle coverage are not established.',
    };
}

async function readBounded(path, limit) {
    const file = await lstat(path);
    require(file.isFile() && file.size > 0 && file.size <= limit, 'Invalid or excessive input file');
    const bytes = await readFile(path);
    require(bytes.length === file.size, 'Input size changed');
    return bytes;
}

async function main(args) {
    require(args.length === 5,
        'Usage: node scripts/check-cef-process-trace.mjs <commit> <bundle-manifest> <evidence-dir> <trace.csv> <guest-image-path>');
    const [expectedCommit, manifest, evidence, trace, image] = args;
    const [manifestBytes, reportBytes, sampleBytes, traceBytes] = await Promise.all([
        readBounded(manifest, 4 * 1024 * 1024), readBounded(join(evidence, 'result.json'), 65536),
        readBounded(join(evidence, 'stdout.json'), 1024 * 1024), readBounded(trace, 32 * 1024 * 1024),
    ]);
    checkNestedEvidence({ expectedCommit, manifestBytes, reportBytes, sampleBytes });
    const events = parseCsv(traceBytes.toString('utf8'));
    require(events.length > 0 && ['Process Name', 'PID', 'Operation', 'Path', 'Result', 'Detail']
        .every(column => Object.hasOwn(events[0], column)), 'Missing Procmon process trace columns');
    const result = compareProcessTrace(events, { runnerPid: parseJson(reportBytes).runnerPid, image,
        sample: parseJson(sampleBytes) });
    console.log(JSON.stringify({ sourceCommit: expectedCommit, traceSha256: digest(traceBytes),
        sampleSha256: digest(sampleBytes), ...result }, null, 2));
    if (result.status !== 'MATCHED_CAPTURE') process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
    main(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
