// SPDX-License-Identifier: MPL-2.0
import { createHash } from 'node:crypto';
import { readFile, lstat } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const archive = '1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a';
const hex = /^[0-9a-f]{64}$/i;
const commit = /^[0-9a-f]{40}$/;
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const require = (condition, message) => { if (!condition) throw new Error(message); };
const record = x => x !== null && typeof x === 'object' && !Array.isArray(x);
const pid = x => Number.isInteger(x) && x > 0 && x <= 0xffffffff;
function parse(bytes) {
    // Windows PowerShell may emit a UTF-8 BOM; hash the original bytes.
    return JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
}

const mitigationFields = ['Aslr', 'DynamicCode', 'SystemCallDisable', 'ControlFlowGuard', 'ImageLoad', 'ChildProcess'];
const exactKeys = (value, fields) => record(value) && Object.keys(value).length === fields.length &&
    fields.every(field => Object.hasOwn(value, field));
const uint = value => Number.isInteger(value) && value >= 0 && value <= 0xffffffff;
const mitigationStatuses = ['observed', 'query-unavailable', 'identity-unavailable', 'policy-changed'];

// This summarizes last live samples only. Do not infer safe flag values,
// admission, or complete coverage from the existence of a record.
function checkMitigationEvidence(sample) {
    const entries = [...(sample.schema >= 3 ? [sample.browser] : []), ...sample.observed];
    const present = Object.hasOwn(sample, 'mitigationCoverage') ||
        entries.some(entry => Object.hasOwn(entry, 'Mitigation'));
    if (!present) return null; // historical schema-5 and earlier reports
    require(sample.schema === 5 && entries.every(entry => Object.hasOwn(entry, 'Mitigation')),
        'Partial or unsupported mitigation evidence');
    const coverage = sample.mitigationCoverage;
    require(exactKeys(coverage, ['scope', 'sampled', 'available', 'unavailable']) &&
        coverage.scope === 'last-live-sample-per-process' &&
        uint(coverage.sampled) && uint(coverage.available) && uint(coverage.unavailable),
        'Invalid mitigation coverage record');
    const byRole = {};
    let available = 0;
    for (const entry of entries) {
        const m = entry.Mitigation;
        require(exactKeys(m, ['Status', 'Flags', 'Win32Error']) && mitigationStatuses.includes(m.Status),
            'Invalid mitigation observation');
        if (m.Status === 'observed') {
            require(exactKeys(m.Flags, mitigationFields) && mitigationFields.every(key => uint(m.Flags[key])) &&
                m.Win32Error === null, 'Invalid observed mitigation flags');
            available++;
        } else {
            require(m.Flags === null && (m.Status === 'query-unavailable'
                ? Number.isInteger(m.Win32Error) && m.Win32Error > 0 && m.Win32Error <= 0x7fffffff
                : m.Win32Error === null), 'Invalid unavailable mitigation observation');
        }
        const role = byRole[entry.Role] ??= {
            sampled: 0, available: 0, unavailable: 0, missingPids: [], distinctFlagSets: []
        };
        role.sampled++;
        if (m.Status === 'observed') {
            role.available++;
            const key = mitigationFields.map(field => m.Flags[field]).join(',');
            if (!role.distinctFlagSets.some(set => mitigationFields.map(field => set[field]).join(',') === key))
                role.distinctFlagSets.push(Object.fromEntries(mitigationFields.map(field => [field, m.Flags[field]])));
        } else {
            role.unavailable++;
            role.missingPids.push(entry.Pid);
        }
    }
    const unavailable = entries.length - available;
    require(coverage.sampled === entries.length && coverage.available === available &&
        coverage.unavailable === unavailable, 'Mitigation coverage totals mismatch');
    return { scope: coverage.scope, sampled: entries.length, available, unavailable, byRole,
        roleBaselineStatus: 'NOT ESTABLISHED', completeProcessLifetimeCoverage: 'NOT ESTABLISHED' };
}

// Structural consistency review of locally reported evidence. This is NOT
// attestation, provenance, independent execution, or a product gate.
export function checkNestedEvidence({ expectedCommit, manifestBytes, reportBytes, sampleBytes }) {
    require(typeof expectedCommit === 'string' && commit.test(expectedCommit), 'Expected exact checkout SHA');
    const manifest = parse(manifestBytes), report = parse(reportBytes), sample = parse(sampleBytes);
    require(record(manifest) && manifest.schema === 1 &&
        manifest.scope === 'unsigned-nested-cef-smoke-only' &&
        manifest.sourceCommit === expectedCommit && manifest.cefArchiveSha256 === archive &&
        manifest.runtimeStatus === 'NOT RUN' && manifest.installable === false, 'Manifest scope or commit mismatch');
    require(Array.isArray(manifest.files) && manifest.files.length >= 8 && manifest.files.length <= 4096,
        'Invalid manifest inventory');
    const files = new Map();
    for (const pin of manifest.files) {
        require(record(pin) && typeof pin.path === 'string' &&
            /^[A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.-]+)*$/.test(pin.path) &&
            pin.path.split('/').every(x => x !== '.' && x !== '..' && !x.endsWith('.')) &&
            Number.isSafeInteger(pin.bytes) && pin.bytes >= 0 &&
            typeof pin.sha256 === 'string' && hex.test(pin.sha256), 'Invalid file pin');
        const key = pin.path.toLowerCase();
        require(!files.has(key), 'Duplicate file pin');
        files.set(key, pin);
    }
    for (const path of ['smoke/veil-cef-smoke.exe', 'smoke/veil-cef-smoke.dll', 'smoke/libcef.dll',
        'smoke/icudtl.dat', 'smoke/resources.pak', 'smoke/locales/en-US.pak', 'runner/CefNestedSmoke.exe',
        'runner/CefNestedSmoke.dll', 'runner/CefNestedSmoke.deps.json', 'runner/CefNestedSmoke.runtimeconfig.json',
        'CEF-LICENSE.txt', 'VEIL-LICENSE.txt', 'run-cef-nested-bundle.ps1']) {
        require(files.has(path.toLowerCase()) && files.get(path.toLowerCase()).bytes > 0, 'Missing required fixture file: ' + path);
    }
    require(record(report) && report.scope === 'nested-win32-smoke-only' &&
        report.sourceCommit === expectedCommit && report.productReleaseGates === 'NOT RUN',
        'Wrong result scope or commit');
    require(typeof report.manifestSha256 === 'string' && hex.test(report.manifestSha256) &&
        report.manifestSha256.toLowerCase() === digest(manifestBytes), 'Result manifest digest mismatch');
    require(report.status === 'PASS', 'Run did not report PASS');
    require(typeof report.stdoutSha256 === 'string' && hex.test(report.stdoutSha256) &&
        report.stdoutSha256.toLowerCase() === digest(sampleBytes), 'Result stdout digest mismatch');
    require(record(sample) && sample.scope === 'nested-win32-smoke-only' && pid(sample.rootPid) &&
        sample.exit === 0 && sample.outerEmpty === true && sample.innerEmpty === true &&
        sample.restricted === true, 'Missing successful smoke/empty job evidence');
    require(Array.isArray(sample.observed) && sample.observed.length > 0 && sample.observed.length <= 4096,
        'Missing or excessive token samples');
    const seen = new Set();
    require(sample.schema === undefined || [2, 3, 4, 5].includes(sample.schema),
        'Unsupported sample schema');
    const roleAware = sample.schema >= 2;
    const roles = new Set(['renderer', 'gpu', 'network', 'utility', 'unknown']);
    const locations = new Set(['runtime', 'windows', 'other']);
    let processesWithModuleInventory = 0;
    function checkModules(entry) {
        require(entry.Modules === null || (Array.isArray(entry.Modules) &&
            entry.Modules.length > 0 && entry.Modules.length <= 512 &&
            entry.Modules.every(module => record(module) && typeof module.Name === 'string' &&
                /^[^/\\:\u0000-\u001f]{1,255}$/.test(module.Name) && locations.has(module.Location))),
        'Invalid module inventory');
        if (entry.Modules !== null) processesWithModuleInventory++;
    }
    if (sample.schema >= 3) {
        const browser = sample.browser;
        require(record(browser) && browser.Pid === sample.rootPid && browser.Role === 'browser' &&
            Number.isInteger(browser.IntegrityRid) && browser.IntegrityRid >= 0 &&
            browser.IntegrityRid <= 0xffffffff && typeof browser.Restricted === 'boolean' &&
            typeof browser.AppContainer === 'boolean', 'Invalid browser process sample');
        checkModules(browser);
    }
    let restrictedLow = 0;
    let restrictedLowRenderers = 0;
    for (const entry of sample.observed) {
        require(record(entry) && pid(entry.Pid) && entry.Pid !== sample.rootPid &&
            !seen.has(entry.Pid) && Number.isInteger(entry.IntegrityRid) &&
            entry.IntegrityRid >= 0 && entry.IntegrityRid <= 0xffffffff &&
            typeof entry.Restricted === 'boolean', 'Invalid or duplicate descendant sample');
        seen.add(entry.Pid);
        if (roleAware)
            require(roles.has(entry.Role) && typeof entry.AppContainer === 'boolean', 'Invalid role or AppContainer sample');
        if (sample.schema >= 3) checkModules(entry);
        if (entry.Restricted && entry.IntegrityRid <= 0x1000) {
            restrictedLow++;
            if (entry.Role === 'renderer') restrictedLowRenderers++;
        }
    }
    require(restrictedLow > 0, 'Restricted-low token assertion not supported by samples');
    if (roleAware)
        require(sample.rendererRestrictedLow === true && restrictedLowRenderers > 0, 'Renderer token assertion not supported by samples');
    let jobNotifications = null;
    if (sample.schema >= 4) {
        require(Array.isArray(sample.jobNotifications) && sample.jobNotifications.length <= 512,
            'Invalid job notifications');
        const summary = {
            outer: { starts: new Set(), exits: new Set(), empty: false },
            inner: { starts: new Set(), exits: new Set(), empty: false },
        };
        for (const event of sample.jobNotifications) {
            require(record(event) && (event.Job === 'outer' || event.Job === 'inner') &&
                ['start', 'exit', 'abnormal-exit', 'empty'].includes(event.Kind) &&
                (event.Kind === 'empty' ? event.Pid === 0 : pid(event.Pid)), 'Invalid job notification');
            const job = summary[event.Job];
            if (event.Kind === 'empty') job.empty = true;
            else (event.Kind === 'start' ? job.starts : job.exits).add(event.Pid);
        }
        jobNotifications = Object.fromEntries(Object.entries(summary).map(([name, job]) => {
            const sampled = [sample.rootPid, ...seen];
            return [name, {
                startPids: job.starts.size,
                exitPids: job.exits.size,
                emptyNotification: job.empty,
                sampledPidsWithoutStart: sampled.filter(id => !job.starts.has(id)),
                sampledPidsWithoutExit: sampled.filter(id => !job.exits.has(id)),
                startPidsWithoutLiveSample: [...job.starts].filter(id => !sampled.includes(id)),
                startPidsWithoutExit: [...job.starts].filter(id => !job.exits.has(id)),
                exitPidsWithoutStart: [...job.exits].filter(id => !job.starts.has(id)),
            }];
        }));
    }
    let retainedHandleExitCount = null;
    if (sample.schema === 5) {
        const pending = new Set([sample.rootPid, ...seen]);
        require(Array.isArray(sample.handleExits) && sample.handleExits.length === pending.size,
            'Missing retained-handle exit observations');
        for (const entry of sample.handleExits) {
            require(record(entry) && pending.delete(entry.Pid) && Number.isInteger(entry.ExitCode) &&
                entry.ExitCode >= 0 && entry.ExitCode <= 0xffffffff &&
                (entry.Pid !== sample.rootPid || entry.ExitCode === sample.exit),
                'Invalid retained-handle exit observation');
        }
        retainedHandleExitCount = sample.handleExits.length;
    }
    const mitigationEvidence = checkMitigationEvidence(sample);
    return {
        status: 'CONSISTENT_REPORTED_SMOKE_PASS',
        sourceCommit: expectedCommit,
        manifestSha256: digest(manifestBytes),
        reportSha256: digest(reportBytes),
        sampleSha256: digest(sampleBytes),
        observedDescendants: seen.size,
        restrictedLowDescendants: restrictedLow,
        restrictedLowRenderers: roleAware ? restrictedLowRenderers : null,
        processesWithModuleInventory: sample.schema >= 3 ? processesWithModuleInventory : null,
        jobNotifications,
        retainedHandleExitCount,
        mitigationEvidence,
        independentRuntimeVerification: 'NOT ESTABLISHED',
        productReleaseGates: 'NOT RUN',
        limitations: 'Unsigned local evidence from sampled processes; no complete containment audit, site isolation, protected storage or product authorization.'
    };
}

// An opt-in collection target, not a sandbox or production admission gate.
// The target is deliberately strict: if a service was not spawned or a
// query was refused, the operator must retain the incomplete result.
export function assessMitigationCollection(result) {
    const requiredRoles = ['browser', 'renderer', 'gpu', 'network', 'utility'];
    const evidence = result.mitigationEvidence;
    const missingRoles = requiredRoles.filter(role => !evidence?.byRole[role]);
    const rolesWithoutFlags = requiredRoles.filter(role =>
        evidence?.byRole[role] && evidence.byRole[role].available === 0);
    const unknownRoleSamples = evidence?.byRole.unknown?.sampled ?? 0;
    const unsampledJobPids = result.jobNotifications === null ? null :
        [...new Set(Object.values(result.jobNotifications)
            .flatMap(job => job.startPidsWithoutLiveSample))].sort((a, b) => a - b);
    const ready = evidence !== null && evidence.unavailable === 0 &&
        missingRoles.length === 0 && rolesWithoutFlags.length === 0 &&
        unknownRoleSamples === 0 && unsampledJobPids !== null && unsampledJobPids.length === 0;
    return {
        status: ready ? 'COLLECTION_TARGET_MET' : 'COLLECTION_INCOMPLETE',
        requiredRoles, missingRoles, rolesWithoutFlags, unknownRoleSamples,
        unavailableSamples: evidence?.unavailable ?? null, unsampledJobPids,
        roleBaselineStatus: 'NOT ESTABLISHED',
        independentRuntimeVerification: 'NOT ESTABLISHED',
        productReleaseGates: 'NOT RUN'
    };
}

async function readBounded(path, limit) {
    const info = await lstat(path);
    require(info.isFile() && !info.isSymbolicLink() && info.size > 0 && info.size <= limit, 'Invalid evidence file: ' + path);
    const bytes = await readFile(path);
    require(bytes.length === info.size, 'Evidence size changed: ' + path);
    return bytes;
}

async function main(args) {
    const strict = args.length === 7 && args[6] === '--require-mitigation-collection';
    require((args.length === 6 || strict) && args[0] === '--commit' && args[2] === '--manifest' && args[4] === '--evidence',
        'Usage: node scripts/check-cef-nested-evidence.mjs --commit <checkout-sha> --manifest <original-manifest.json> --evidence <result-directory> [--require-mitigation-collection]');
    const evidence = resolve(args[5]);
    const [manifestBytes, reportBytes, sampleBytes] = await Promise.all([
        readBounded(resolve(args[3]), 4 * 1024 * 1024),
        readBounded(join(evidence, 'result.json'), 64 * 1024),
        readBounded(join(evidence, 'stdout.json'), 1024 * 1024)
    ]);
    const result = checkNestedEvidence({ expectedCommit: args[1], manifestBytes, reportBytes, sampleBytes });
    const mitigationCollection = assessMitigationCollection(result);
    console.log(JSON.stringify({ ...result, mitigationCollection }, null, 2));
    if (strict && mitigationCollection.status !== 'COLLECTION_TARGET_MET') process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    main(process.argv.slice(2)).catch(error => {
        console.error('Evidence rejected: ' + error.message);
        process.exitCode = 1;
    });
}
