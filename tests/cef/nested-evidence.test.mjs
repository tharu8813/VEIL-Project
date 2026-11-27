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
            'smoke/icudtl.dat', 'smoke/resources.pak', 'smoke/locales/en-US.pak', 'runner/CefNestedSmoke.exe',
            'runner/CefNestedSmoke.dll', 'runner/CefNestedSmoke.deps.json', 'runner/CefNestedSmoke.runtimeconfig.json',
            'CEF-LICENSE.txt', 'VEIL-LICENSE.txt', 'run-cef-nested-bundle.ps1'].map(path => ({
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
    const sampleBytes = encode(f.sample);
    const reportBytes = encode({
        manifestSha256: createHash('sha256').update(manifestBytes).digest('hex').toUpperCase(),
        stdoutSha256: createHash('sha256').update(sampleBytes).digest('hex').toUpperCase(),
        ...f.report
    });
    return checkNestedEvidence({ expectedCommit: checkout, manifestBytes, reportBytes, sampleBytes, ...overrides });
}
test('valid unsigned smoke record stays explicitly non-product and non-attested', () => {
    const result = check(fixture());
    assert.equal(result.status, 'CONSISTENT_REPORTED_SMOKE_PASS');
    assert.equal(result.observedDescendants, 2);
    assert.equal(result.restrictedLowDescendants, 1);
    assert.equal(result.restrictedLowRenderers, null);
    assert.equal(result.independentRuntimeVerification, 'NOT ESTABLISHED');
    assert.equal(result.productReleaseGates, 'NOT RUN');
});
test('role-aware evidence requires a restricted low-integrity renderer sample', () => {
    const f = fixture();
    f.sample.schema = 2;
    f.sample.rendererRestrictedLow = true;
    Object.assign(f.sample.observed[0], { Role: 'renderer', AppContainer: false });
    Object.assign(f.sample.observed[1], { Role: 'network', AppContainer: false });
    assert.equal(check(f).restrictedLowRenderers, 1);
    f.sample.observed[0].Role = 'gpu';
    assert.throws(() => check(f), /Renderer token assertion/);
    f.sample.observed[0].Role = 'renderer';
    delete f.sample.observed[0].AppContainer;
    assert.throws(() => check(f), /AppContainer sample/);
});
test('module inventory is scoped to named modules on the sampled fixture processes', () => {
    const f = fixture();
    f.sample.schema = 3;
    f.sample.rendererRestrictedLow = true;
    f.sample.browser = { Pid: 100, Role: 'browser', IntegrityRid: 8192, Restricted: false,
        AppContainer: false, Modules: [{ Name: 'libcef.dll', Location: 'runtime' }] };
    Object.assign(f.sample.observed[0], { Role: 'renderer', AppContainer: false, Modules: null });
    Object.assign(f.sample.observed[1], { Role: 'network', AppContainer: false, Modules: null });
    assert.equal(check(f).processesWithModuleInventory, 1);
    f.sample.browser.Modules[0].Name = '../libcef.dll';
    assert.throws(() => check(f), /Invalid module inventory/);
    f.sample.browser.Pid++;
    assert.throws(() => check(f), /Invalid browser process sample/);
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

test('reject mixed runs, raw stdout modifications and old reports without sample binding', () => {
    const f = fixture();
    const swapped = { ...f.sample, rootPid: 200 };
    assert.throws(() => check(f, { sampleBytes: encode(swapped) }), /stdout digest mismatch/);
    assert.throws(() => check(f, { sampleBytes: Buffer.concat([encode(f.sample), Buffer.from('\n')]) }), /stdout digest mismatch/);
    f.report.stdoutSha256 = undefined;
    assert.throws(() => check(f), /stdout digest mismatch/);
});
test('reject missing runtime configuration, locale or license inventory', () => {
    for (const path of ['smoke/locales/en-US.pak', 'runner/CefNestedSmoke.deps.json',
        'runner/CefNestedSmoke.runtimeconfig.json', 'CEF-LICENSE.txt', 'VEIL-LICENSE.txt']) {
        const f = fixture();
        f.manifest.files = f.manifest.files.filter(x => x.path !== path);
        assert.throws(() => check(f), /Missing required fixture file/);
    }
});
