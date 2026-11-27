// SPDX-License-Identifier: MPL-2.0
import test from 'node:test';
import assert from 'node:assert/strict';
import { compareProcessTrace } from '../../scripts/check-cef-process-trace.mjs';

const image = 'C:\\VeilFixture\\smoke\\veil-cef-smoke.exe';
const rootPid = 100, runnerPid = 90;
const pids = [rootPid, 101, 102];
const create = (processName, pid, child, path = image) => ({
    'Process Name': processName, PID: String(pid), Operation: 'Process Create',
    Path: path, Result: 'SUCCESS', Detail: 'PID: ' + child + ', Command line: fixture',
});
const exit = (pid, processName = 'veil-cef-smoke.exe') => ({
    'Process Name': processName, PID: String(pid), Operation: 'Process Exit', Result: 'SUCCESS',
});
function fixture() {
    const events = [
        create('CefNestedSmoke.exe', runnerPid, rootPid),
        create('veil-cef-smoke.exe', rootPid, 101),
        create('veil-cef-smoke.exe', 101, 102),
        exit(102), exit(101), exit(rootPid),
    ];
    const jobNotifications = ['outer', 'inner'].flatMap(Job => pids.flatMap(Pid =>
        ['start', 'exit'].map(Kind => ({ Job, Kind, Pid }))));
    return { events, sample: { schema: 4, rootPid, observed: [{ Pid: 101 }], jobNotifications } };
}
const compare = ({ events, sample }) => compareProcessTrace(events, { runnerPid, image, sample });

test('matches the exact root, descendants and both Job histories', () => {
    const f = fixture();
    const result = compare(f);
    assert.equal(result.status, 'MATCHED_CAPTURE');
    assert.deepEqual(result.tracePidsWithoutLiveSample, [102]);
    assert.deepEqual(result.unmatched.inner.osStartsWithoutJob, []);
    assert.equal(result.traceExits, 3);
});

test('rejects a different root image or launcher PID and an unexpected descendant image', () => {
    let f = fixture();
    f.events[0].PID = '91';
    assert.equal(compare(f).rootMatches, 0);
    f = fixture();
    f.events[0].Path = 'C:\\Other\\veil-cef-smoke.exe';
    assert.equal(compare(f).rootMatches, 0);
    f = fixture();
    f.events[2].Path = 'C:\\Other\\veil-cef-smoke.exe';
    assert.deepEqual(compare(f).unexpectedImagePids, [102]);
});

test('reports missing exits, differences between sources and PID reuse', () => {
    let f = fixture();
    f.events.splice(3, 1);
    const incomplete = compare(f);
    assert.equal(incomplete.status, 'INCOMPLETE_OR_MISMATCHED_CAPTURE');
    assert.deepEqual(incomplete.missingExitPids, [102]);
    assert.deepEqual(incomplete.unmatched.outer.jobExitsWithoutOs, [102]);
    f.events.push(create('unrelated.exe', 6, 102, 'C:\\Other\\unrelated.exe'), exit(102, 'unrelated.exe'));
    assert.deepEqual(compare(f).ambiguousPids, [102]);
    f = fixture();
    f.events.push(create('unrelated.exe', 6, 102, 'C:\\Other\\unrelated.exe'), exit(102, 'unrelated.exe'));
    assert.equal(compare(f).status, 'MATCHED_CAPTURE');
});
