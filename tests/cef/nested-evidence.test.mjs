// SPDX-License-Identifier: MPL-2.0
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { checkNestedEvidence, assessMitigationCollection } from '../../scripts/check-cef-nested-evidence.mjs';

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
test('best-effort Job notifications expose gaps without replacing live samples', () => {
    const f = fixture();
    Object.assign(f.sample, { schema: 4, rendererRestrictedLow: true,
        browser: { Pid: 100, Role: 'browser', IntegrityRid: 8192, Restricted: false, AppContainer: false, Modules: null },
        jobNotifications: [{ Job: 'outer', Kind: 'start', Pid: 101 },
            { Job: 'outer', Kind: 'start', Pid: 103 }, { Job: 'inner', Kind: 'empty', Pid: 0 }] });
    Object.assign(f.sample.observed[0], { Role: 'renderer', AppContainer: false, Modules: null });
    Object.assign(f.sample.observed[1], { Role: 'network', AppContainer: false, Modules: null });
    assert.deepEqual(check(f).jobNotifications.outer.sampledPidsWithoutStart, [100, 102]);
    assert.deepEqual(check(f).jobNotifications.outer.startPidsWithoutLiveSample, [103]);
    assert.deepEqual(check(f).jobNotifications.outer.startPidsWithoutExit, [101, 103]);
    assert.deepEqual(check(f).jobNotifications.inner.sampledPidsWithoutExit, [100, 101, 102]);
    f.sample.jobNotifications[2].Pid = 101;
    assert.throws(() => check(f), /Invalid job notification/);
});
test('retained handles prove exits for every live sample even without Job notifications', () => {
    const f = fixture();
    Object.assign(f.sample, { schema: 5, rendererRestrictedLow: true,
        browser: { Pid: 100, Role: 'browser', IntegrityRid: 8192, Restricted: false, AppContainer: false, Modules: null },
        jobNotifications: [], handleExits: [{ Pid: 100, ExitCode: 0 },
            { Pid: 101, ExitCode: 0 }, { Pid: 102, ExitCode: 1 }] });
    Object.assign(f.sample.observed[0], { Role: 'renderer', AppContainer: false, Modules: null });
    Object.assign(f.sample.observed[1], { Role: 'network', AppContainer: false, Modules: null });
    assert.equal(check(f).retainedHandleExitCount, 3);
    f.sample.handleExits[2].Pid = 101;
    assert.throws(() => check(f), /Invalid retained-handle exit/);
    f.sample.handleExits.pop();
    assert.throws(() => check(f), /Missing retained-handle exit/);
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


// Keep old evidence readable; new evidence must be complete as a group,
// with explicit query failures distinct from actual zero policy flags.
function mitigationFixture() {
    const f = fixture();
    Object.assign(f.sample, { schema: 5, rendererRestrictedLow: true,
        browser: { Pid: 100, Role: 'browser', IntegrityRid: 8192, Restricted: false, AppContainer: false, Modules: null },
        jobNotifications: [], handleExits: [100, 101, 102].map(Pid => ({ Pid, ExitCode: 0 })) });
    Object.assign(f.sample.observed[0], { Role: 'renderer', AppContainer: false, Modules: null });
    Object.assign(f.sample.observed[1], { Role: 'network', AppContainer: false, Modules: null });
    return f;
}
function addMitigations(f) {
    const Flags = { Aslr: 0, DynamicCode: 0xffffffff, SystemCallDisable: 1,
        ControlFlowGuard: 2, ImageLoad: 0, ChildProcess: 0 };
    f.sample.browser.Mitigation = { Status: 'observed', Flags: { ...Flags }, Win32Error: null };
    f.sample.observed[0].Mitigation = { Status: 'observed', Flags: { ...Flags }, Win32Error: null };
    f.sample.observed[1].Mitigation = { Status: 'query-unavailable', Flags: null, Win32Error: 5 };
    f.sample.mitigationCoverage = { scope: 'last-live-sample-per-process', sampled: 3, available: 2, unavailable: 1 };
    return f;
}
test('legacy schema-5 reports do not establish mitigation evidence', () => {
    assert.equal(check(mitigationFixture()).mitigationEvidence, null);
});
test('reports actual raw flag sets and missing samples by role without granting a baseline', () => {
    const f = addMitigations(mitigationFixture());
    const result = check(f);
    const m = result.mitigationEvidence;
    assert.equal(m.available, 2);
    assert.equal(m.unavailable, 1);
    assert.equal(m.byRole.browser.distinctFlagSets[0].Aslr, 0);
    assert.equal(m.byRole.renderer.distinctFlagSets[0].DynamicCode, 0xffffffff);
    assert.deepEqual(m.byRole.network.missingPids, [102]);
    assert.deepEqual(m.byRole.network.distinctFlagSets, []);
    assert.equal(m.roleBaselineStatus, 'NOT ESTABLISHED');
    assert.equal(result.productReleaseGates, 'NOT RUN');
    f.sample.observed[1].Mitigation = { Status: 'policy-changed', Flags: null, Win32Error: null };
    assert.equal(check(f).mitigationEvidence.unavailable, 1);
});
test('rejects partial mitigation extension and false coverage totals', () => {
    for (const mutate of [
        f => delete f.sample.mitigationCoverage,
        f => delete f.sample.browser.Mitigation,
        f => delete f.sample.observed[0].Mitigation,
        f => { f.sample.mitigationCoverage.available = 3; },
        f => { f.sample.mitigationCoverage.scope = 'all-processes'; },
        f => { f.sample.mitigationCoverage.unavailable = -1; },
        f => { f.sample.mitigationCoverage.extra = true; }
    ]) {
        const f = addMitigations(mitigationFixture()); mutate(f);
        assert.throws(() => check(f), /mitigation/i);
    }
});
test('rejects missing, invalid and fabricated mitigation policy values', () => {
    for (const mutate of [
        m => { delete m.Flags.Aslr; },
        m => { m.Flags.Aslr = -1; },
        m => { m.Flags.Aslr = 0x100000000; },
        m => { m.Flags.Aslr = '0'; },
        m => { m.Flags.Aslr = 0.5; },
        m => { m.Flags.Other = 0; },
        m => { m.Win32Error = 5; },
        m => { m.Status = 'safe'; },
        m => { m.Status = 'identity-unavailable'; }
    ]) {
        const f = addMitigations(mitigationFixture()); mutate(f.sample.browser.Mitigation);
        assert.throws(() => check(f), /mitigation/i);
    }
    for (const mutate of [
        m => { m.Flags = {}; },
        m => { m.Win32Error = 0; },
        m => { m.Win32Error = null; },
        m => { m.Status = 'policy-changed'; }
    ]) {
        const f = addMitigations(mitigationFixture()); mutate(f.sample.observed[1].Mitigation);
        assert.throws(() => check(f), /mitigation/i);
    }
});
test('separates distinct flag sets within the same role rather than averaging them', () => {
    const f = addMitigations(mitigationFixture());
    const clone = structuredClone(f.sample.observed[0]);
    clone.Pid = 103;
    clone.Mitigation.Flags.Aslr = 4;
    f.sample.observed.push(clone);
    f.sample.handleExits.push({ Pid: 103, ExitCode: 0 });
    f.sample.mitigationCoverage.sampled++;
    f.sample.mitigationCoverage.available++;
    assert.equal(check(f).mitigationEvidence.byRole.renderer.distinctFlagSets.length, 2);
    clone.Mitigation.Flags.Aslr = 0;
    assert.equal(check(f).mitigationEvidence.byRole.renderer.distinctFlagSets.length, 1);
});


function fullMitigationFixture() {
    const f = addMitigations(mitigationFixture());
    f.sample.observed[1].Mitigation = structuredClone(f.sample.browser.Mitigation);
    for (const [Pid, Role] of [[103, 'gpu'], [104, 'utility']]) {
        f.sample.observed.push({ ...structuredClone(f.sample.observed[0]), Pid, Role });
        f.sample.handleExits.push({ Pid, ExitCode: 0 });
    }
    f.sample.mitigationCoverage = { scope: 'last-live-sample-per-process', sampled: 5, available: 5, unavailable: 0 };
    f.sample.jobNotifications = ['outer', 'inner'].flatMap(Job =>
        [100, 101, 102, 103, 104].flatMap(Pid => ['start', 'exit'].map(Kind => ({ Job, Pid, Kind }))));
    return f;
}
test('explicit mitigation collection target is not an admission or independent verification', () => {
    const result = assessMitigationCollection(check(fullMitigationFixture()));
    assert.equal(result.status, 'COLLECTION_TARGET_MET');
    assert.equal(result.roleBaselineStatus, 'NOT ESTABLISHED');
    assert.equal(result.independentRuntimeVerification, 'NOT ESTABLISHED');
    assert.equal(result.productReleaseGates, 'NOT RUN');
});
test('collection target reports missing roles and refuses legacy evidence', () => {
    const legacy = assessMitigationCollection(check(mitigationFixture()));
    assert.equal(legacy.status, 'COLLECTION_INCOMPLETE');
    assert.equal(legacy.unavailableSamples, null);
    const incomplete = assessMitigationCollection(check(addMitigations(mitigationFixture())));
    assert.deepEqual(incomplete.missingRoles, ['gpu', 'utility']);
    assert.deepEqual(incomplete.rolesWithoutFlags, ['network']);
    assert.equal(incomplete.status, 'COLLECTION_INCOMPLETE');
});
test('collection target refuses unknown roles, unavailable policy samples and unsampled Job starts', () => {
    let f = fullMitigationFixture();
    f.sample.observed[3].Role = 'unknown';
    assert.equal(assessMitigationCollection(check(f)).unknownRoleSamples, 1);
    assert.equal(assessMitigationCollection(check(f)).status, 'COLLECTION_INCOMPLETE');
    f = fullMitigationFixture();
    f.sample.observed[2].Mitigation = { Status: 'identity-unavailable', Flags: null, Win32Error: null };
    f.sample.mitigationCoverage.available--;
    f.sample.mitigationCoverage.unavailable++;
    assert.equal(assessMitigationCollection(check(f)).status, 'COLLECTION_INCOMPLETE');
    f = fullMitigationFixture();
    f.sample.jobNotifications.push({ Job: 'outer', Kind: 'start', Pid: 105 });
    assert.deepEqual(assessMitigationCollection(check(f)).unsampledJobPids, [105]);
    assert.equal(assessMitigationCollection(check(f)).status, 'COLLECTION_INCOMPLETE');
});
