// SPDX-License-Identifier: MPL-2.0
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { checkNestedEvidence } from '../../scripts/check-cef-nested-evidence.mjs';

const checkout = 'a'.repeat(40);
const encode = value => Buffer.from(JSON.stringify(value));
function fixture() {
    const manifest = {
        schema: 1, scope: 'unsigned-nested-cef-smoke-only', sourceCommit: checkout,
        cefArchiveSha256: '1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a',
        runtimeStatus: 'NOT RUN', installable: false,
        files: ['smoke/veil-cef-smoke.exe', 'smoke/veil-cef-smoke.dll', 'smoke/libcef.dll',
            'smoke/icudtl.dat', 'smoke/resources.pak', 'runner/CefNestedSmoke.exe',
            'runner/CefNestedSmoke.dll', 'run-cef-nested-bundle.ps1'].map(path => ({
            path, bytes: 100, sha256: 'b'.repeat(64)
        }))
    };
    const report = { scope: 'nested-win32-smoke-only', sourceCommit: checkout,
        status: 'PASS', productReleaseGates: 'NOT RUN' };
    const sample = { scope: 'nested-win32-smoke-only', rootPid: 100, exit: 0,
        outerEmpty: true, innerEmpty: true, restricted: true,
        observed: [{ Pid: 101, IntegrityRid: 0, Restricted: true },
            { Pid: 102, IntegrityRid: 8192, Restricted: false }] };
    return { manifest, report, sample };
}
function check(f, overrides = {}) {
    const manifestBytes = encode(f.manifest);
    const reportBytes = encode({ manifestSha256: createHash('sha256').update(manifestBytes).digest('hex').toUpperCase(), ...f.report });
    return checkNestedEvidence({ expectedCommit: checkout, manifestBytes, reportBytes,
        sampleBytes: encode(f.sample), ...overrides });
}
test('valid unsigned smoke record stays explicitly non-product and non-attested', () => {
    const result = check(fixture());
    assert.equal(result.status, 'CONSISTENT_REPORTED_SMOKE_PASS');
    assert.equal(result.observedDescendants, 2);
    assert.equal(result.restrictedLowDescendants, 1);
    assert.equal(result.independentRuntimeVerification, 'NOT ESTABLISHED');
    assert.equal(result.productReleaseGates, 'NOT RUN');
});
test('reject wrong checkout, manifest hash, scope and release claim', () => {
    assert.throws(() => check(fixture(), { expectedCommit: 'c'.repeat(40) }), /commit mismatch/);
    let f = fixture(); f.report.manifestSha256 = 'c'.repeat(64);
    assert.throws(() => check(f), /digest mismatch/);
    f = fixture(); f.report.productReleaseGates = 'PASS';
    assert.throws(() => check(f), /scope or commit/);
    f = fixture(); f.report.status = 'FAIL';
    assert.throws(() => check(f), /did not report PASS/);
});
test('reject absent or contradictory token and job observations', () => {
    let f = fixture(); f.sample.observed = [];
    assert.throws(() => check(f), /token samples/);
    f = fixture(); f.sample.observed[0].Restricted = false;
    assert.throws(() => check(f), /not supported/);
    f = fixture(); f.sample.observed[0].IntegrityRid = 8192;
    assert.throws(() => check(f), /not supported/);
    f = fixture(); f.sample.innerEmpty = 'true';
    assert.throws(() => check(f), /empty job/);
    f = fixture(); f.sample.exit = 35;
    assert.throws(() => check(f), /empty job/);
});
test('reject duplicate or root samples, missing and duplicate manifest pins', () => {
    let f = fixture(); f.sample.observed[0].Pid = f.sample.rootPid;
    assert.throws(() => check(f), /descendant sample/);
    f = fixture(); f.sample.observed.push(f.sample.observed[0]);
    assert.throws(() => check(f), /descendant sample/);
    f = fixture(); f.manifest.files.push({ ...f.manifest.files[0], path: f.manifest.files[0].path.toUpperCase() });
    assert.throws(() => check(f), /Duplicate file pin/);
    f = fixture(); f.manifest.files[0].path = 'smoke/../other.exe';
    assert.throws(() => check(f), /Invalid file pin/);
    f = fixture(); f.manifest.files[0].path = 'other.exe';
    assert.throws(() => check(f), /Missing required fixture file/);
});
