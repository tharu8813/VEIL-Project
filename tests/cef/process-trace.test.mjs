// SPDX-License-Identifier: MPL-2.0
import test from 'node:test';
import assert from 'node:assert/strict';
import { compareProcessTrace, parseProcessTraceCsv } from '../../scripts/check-cef-process-trace.mjs';

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
test('reports retained handle corroboration separately from Job notification gaps', () => {
    const f = fixture();
    f.sample.schema = 5;
    f.sample.handleExits = [{ Pid: 100, ExitCode: 0 }, { Pid: 101, ExitCode: 0 }];
    f.sample.jobNotifications = f.sample.jobNotifications.filter(e => !(e.Pid === 101 && e.Kind === 'exit'));
    const result = compare(f);
    assert.deepEqual(result.traceExitsWithoutRetainedHandle, [102]);
    assert.deepEqual(result.retainedHandleExitsWithoutTrace, []);
    assert.deepEqual(result.unmatched.outer.osExitsWithoutJob, [101]);
    assert.equal(result.status, 'INCOMPLETE_OR_MISMATCHED_CAPTURE');
});

test('rejects malformed lifecycle rows even when both Job PID sets still agree', () => {
    for (const [row, reason] of [
        [{ ...create('veil-cef-smoke.exe', 101, 999), Detail: 'unparseable child' }, 'malformed-create'],
        [{ ...create('veil-cef-smoke.exe', 101, 999), Path: '' }, 'malformed-create'],
        [create('unrelated.exe', 101, 999), 'process-name-mismatch'],
        [exit(101, ''), 'process-name-mismatch'],
        [{ ...exit(101), Result: 'ACCESS DENIED' }, 'unsuccessful-exit'],
    ]) {
        const f = fixture();
        f.events.splice(3, 0, row);
        const result = compare(f);
        assert.equal(result.status, 'INCOMPLETE_OR_MISMATCHED_CAPTURE');
        assert.deepEqual(result.invalidTreeEvents, [{ row: 4, pid: 101, reason }]);
        assert.deepEqual(result.unmatched.inner.osStartsWithoutJob, []);
        assert.deepEqual(result.unmatched.inner.osExitsWithoutJob, []);
    }
});

test('ignores unrelated malformed lifecycle rows outside the tracked tree', () => {
    const f = fixture();
    f.events.splice(3, 0, { ...create('unrelated.exe', 77, 999), Detail: 'unparseable' });
    assert.equal(compare(f).status, 'MATCHED_CAPTURE');
});

test('retained handle exit missing from the trace cannot be a matched capture', () => {
    const f = fixture();
    f.sample.schema = 5;
    f.sample.handleExits = [{ Pid: 100, ExitCode: 0 }, { Pid: 101, ExitCode: 0 },
        { Pid: 999, ExitCode: 0 }];
    const result = compare(f);
    assert.deepEqual(result.retainedHandleExitsWithoutTrace, [999]);
    assert.equal(result.status, 'INCOMPLETE_OR_MISMATCHED_CAPTURE');
});

const columns = ['Process Name', 'PID', 'Operation', 'Path', 'Result', 'Detail'];
const header = columns.join(',');
const csvRow = event => columns.map(key => '"' + (event[key] ?? '').replaceAll('"', '""') + '"').join(',');

test('strict trace CSV preserves quoted commas, escaped quotes, multiline fields and CRLF', () => {
    const f = fixture();
    f.events[0].Detail += ', quoted "value"\r\nsecond line';
    const csv = '\uFEFF' + header + '\r\n' + f.events.map(csvRow).join('\r\n') + '\r\n';
    const events = parseProcessTraceCsv(csv);
    assert.equal(events[0].Detail, f.events[0].Detail);
    assert.equal(compare({ events, sample: f.sample }).status, 'MATCHED_CAPTURE');
    assert.equal(parseProcessTraceCsv(header + '\n' + csvRow(f.events[0])).length, 1);
});

test('strict trace CSV rejects incomplete tails, row width mismatch and ambiguous headers', () => {
    const csv = header + '\n' + fixture().events.map(csvRow).join('\n');
    for (const [bad, message] of [
        [csv + '\n"unfinished', /Unterminated/],
        [csv + '\nshort,row', /record width/],
        [csv + ',extra', /record width/],
        [csv + '\n', null],
        [csv.replace(header, header + ',PID'), /duplicate/],
        [csv.replace(header, header + ','), /Empty or duplicate/],
        [csv.replace('Operation', 'Other'), /Missing Procmon process trace columns/],
        [header + '\nname"suffix,1,Process Exit,,SUCCESS,', /Unexpected quote/],
        [header + '\n"name"x,1,Process Exit,,SUCCESS,', /Unexpected text/],
        [header + '\r' + csvRow(fixture().events[0]), /line ending/],
    ]) {
        if (message) assert.throws(() => parseProcessTraceCsv(bad), message);
        else assert.equal(parseProcessTraceCsv(bad).length, fixture().events.length);
    }
});
