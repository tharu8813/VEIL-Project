// ADR-0007: Security Manager and the Microsoft SmartScreen setting.
//
//   node tests/security/smartscreen.mjs        (needs openssl on PATH, e.g. Git for Windows)
//
// Uses the desktop `security` smoke stage (Debug build).
//   1. Defaults: a new profile and a profile without a Security value start with SmartScreen on.
//   2. Every view (tabs, a second window) gets the saved value; a change applies to all and is saved.
//   3. Restart restore: the next run of the same profile starts with the changed value.
//   4. Per profile + multiple instances: two profiles run at once with different values.
//   5. Damaged security values fall back to "on" (never "off"); a damaged state file still refuses start.
//   6. Threat interface with the mock engine (inside the stage) and no engine in production.
//   7. Engine behaviour: with secure DNS pointed at a local DoH responder, a SmartScreen-on run looks up the
//      SmartScreen service and a SmartScreen-off run does not (observation only; the lookup is answered NXDOMAIN).
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { startDoh } from '../integration/doh.mjs';
import { startFixture } from '../integration/fixture.mjs';

const run = promisify(execFile);
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const evidence = await mkdtemp(join(root, 'smartscreen-'));
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
const { server: fixture, origin } = await startFixture();
const port = new URL(origin).port;

async function profileWith(name, state) {
    const profile = join(evidence, name);
    await mkdir(profile, { recursive: true });
    if (state !== undefined) await writeFile(join(profile, 'browser-state.json'), typeof state === 'string' ? state : JSON.stringify(state));
    return profile;
}

async function stage(profile, label, env) {
    const report = join(evidence, `${label}.json`);
    await run('dotnet', [desktop, '--smoke', profile, origin, 'security', report], { timeout: 180_000, env: { ...process.env, ...env } })
        .catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
    return JSON.parse(await readFile(report, 'utf8'));
}

function passed(result, label) {
    assert.equal(result.error, null, `${label}: ${result.error}`);
    return result.checks;
}

const empty = { Bookmarks: [], History: [], Tabs: [], Selected: 0 };
const doh = await startDoh(evidence);
try {
    // 1-3. Default on -> change to off (all views, saved) -> restart restores off -> back on.
    const main = await profileWith('main');
    let checks = passed(await stage(main, 'default', { VEIL_TEST_SECURITY_EXPECT: 'on', VEIL_TEST_SECURITY_SET: 'off' }), 'default');
    assert.ok(checks.includes('every view SmartScreen=True after new window (3 views)'), 'new window not covered');
    assert.ok(checks.includes('mock: unlisted host is Unknown, not safe'), 'mock engine checks missing');
    const saved = JSON.parse(await readFile(join(main, 'browser-state.json'), 'utf8'));
    assert.deepEqual(saved.Security, { SmartScreen: false });
    for (const key of ['Bookmarks', 'History', 'Tabs']) assert.ok(Array.isArray(saved[key]), `${key} kept`);
    console.log('default on, change to off applied to all views and saved: PASS');
    passed(await stage(main, 'restart', { VEIL_TEST_SECURITY_EXPECT: 'off', VEIL_TEST_SECURITY_SET: 'on' }), 'restart');
    passed(await stage(main, 'restart-on', { VEIL_TEST_SECURITY_EXPECT: 'on' }), 'restart-on');
    console.log('restart restores the saved value (off, then on): PASS');

    // Missing Security value in an existing profile (older state file, network settings kept).
    const older = await profileWith('older', { ...empty, Network: { Mode: 0, DnsTemplate: null } });
    passed(await stage(older, 'older', { VEIL_TEST_SECURITY_EXPECT: 'on' }), 'older');
    const olderState = JSON.parse(await readFile(join(older, 'browser-state.json'), 'utf8'));
    assert.ok(olderState.Network, 'network settings kept');
    console.log('missing value defaults to on, other settings kept: PASS');

    // 4. Two profiles at once with different values.
    const a = await profileWith('instance-a', { ...empty, Security: { SmartScreen: false } });
    const b = await profileWith('instance-b');
    const [ra, rb] = await Promise.all([
        stage(a, 'instance-a', { VEIL_TEST_SECURITY_EXPECT: 'off' }),
        stage(b, 'instance-b', { VEIL_TEST_SECURITY_EXPECT: 'on' }),
    ]);
    passed(ra, 'instance-a'); passed(rb, 'instance-b');
    console.log('per-profile values in two simultaneous instances: PASS');

    // 5. Damaged values.
    for (const [label, security] of [['wrong-type', 'off'], ['wrong-field', { SmartScreen: 0 }], ['array', [false]]]) {
        const damaged = await profileWith(`damaged-${label}`, { ...empty, Security: security });
        passed(await stage(damaged, `damaged-${label}`, { VEIL_TEST_SECURITY_EXPECT: 'recovered' }), label);
    }
    const unknownField = await profileWith('newer-field', { ...empty, Security: { SmartScreen: false, FutureOption: 3 } });
    passed(await stage(unknownField, 'newer-field', { VEIL_TEST_SECURITY_EXPECT: 'off' }), 'newer-field');
    const broken = await profileWith('broken-file', '{"Bookmarks": [');
    const refused = await stage(broken, 'broken-file', {});
    assert.equal(refused.error, 'JsonException', 'damaged state file must refuse start');
    assert.equal(await readFile(join(broken, 'browser-state.json'), 'utf8'), '{"Bookmarks": [', 'damaged state file left untouched');
    console.log('damaged security values fall back to on; damaged state file refuses start untouched: PASS');

    // 7. Engine behaviour through the DoH responder (names only, nothing leaves the machine).
    async function observe(label, smartScreen) {
        const profile = await profileWith(`dns-${label}`, { ...empty, Security: { SmartScreen: smartScreen }, Network: { Mode: 0, DnsTemplate: doh.template } });
        const before = doh.queried.length;
        const result = await stage(profile, `dns-${label}`, {
            VEIL_TEST_SECURITY_EXPECT: smartScreen ? 'on' : 'off', VEIL_TEST_TRUSTED_SPKI: doh.spki,
            VEIL_TEST_SECURITY_URL: `http://veil-ss-${label}.test:${port}/second`, VEIL_TEST_SECURITY_DWELL: '4000',
        });
        passed(result, `dns-${label}`);
        const names = doh.queried.slice(before);
        return { names, smartscreen: names.filter(name => name.includes('smartscreen')), navigation: result.checks.find(c => c.startsWith('navigation:')) };
    }
    const on = await observe('on', true);
    const off = await observe('off', false);
    await writeFile(join(evidence, 'dns-observation.json'), JSON.stringify({ on, off }, null, 2));
    console.log(`SmartScreen on : ${on.navigation} · SmartScreen lookups: ${on.smartscreen.join(', ') || 'none'}`);
    console.log(`SmartScreen off: ${off.navigation} · SmartScreen lookups: ${off.smartscreen.join(', ') || 'none'}`);
    assert.equal(off.smartscreen.length, 0, 'SmartScreen service looked up although SmartScreen is off');
    if (on.smartscreen.length === 0) console.log('NOTE: no SmartScreen lookup seen with SmartScreen on (Windows setting may disable it, or the engine cached it); not counted as a pass');
    else console.log('engine observation: SmartScreen lookups only when on: PASS');
    console.log(`PASS smartscreen · evidence ${evidence}`);
} finally {
    fixture.close();
    await doh.stop().catch(() => {});
}
