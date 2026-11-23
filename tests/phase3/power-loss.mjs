// Phase 3 / Gate B: power loss while engine storage commits are in flight. VM only.
//
//   node tests/phase3/power-loss.mjs --arm   --profile <dir> --evidence <dir>
//       starts the storage writer and writes <evidence>\armed.json once commits are flowing; the
//       VM is then powered off from the host (VBoxManage controlvm <vm> poweroff).
//   node tests/phase3/power-loss.mjs --check --profile <dir> --evidence <dir>
//       after reboot and volume unlock: app state must parse and the engine storage must open
//       gap-free with at least the records committed before arming.
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { readFile, writeFile } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';

const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const mode = args.includes('--arm') ? 'arm' : args.includes('--check') ? 'check' : null;
const profile = option('--profile');
const evidence = option('--evidence');
if (!mode || !profile || !evidence || !isAbsolute(profile) || !isAbsolute(evidence))
    throw new Error('Use --arm or --check with absolute --profile and --evidence paths.');
const desktop = process.env.VEIL_DESKTOP_DLL ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
// Site storage is per origin (host + port): --arm and --check must serve the same origin.
const { server, origin } = await startFixture(Number(option('--port') ?? 47391));

try {
    if (mode === 'arm') {
        const report = join(evidence, 'arm-stage.json');
        spawn('dotnet', [desktop, '--smoke', profile, origin, 'storm', report], {
            windowsHide: true, stdio: 'ignore', detached: false,
            env: { ...process.env, VEIL_STORM_HOLD_SECONDS: '600' },
        });
        for (let i = 0; i < 1200; i++) {
            const ready = await readFile(report, 'utf8').then(JSON.parse).catch(() => null);
            if (ready?.stormReady) {
                await writeFile(join(evidence, 'armed.json'), JSON.stringify({ ...ready, armedAt: new Date().toISOString() }));
                console.log(`ARMED · committed before arming >= ${ready.before + 100}`);
                // Keep serving the fixture until the power is cut.
                await delay(600_000);
                break;
            }
            await delay(100);
        }
        throw new Error('Writer was not armed (or the power was not cut within 10 minutes).');
    } else {
        const armed = JSON.parse(await readFile(join(evidence, 'armed.json'), 'utf8'));
        JSON.parse(await readFile(join(profile, 'browser-state.json'), 'utf8'));
        const report = join(evidence, 'check-stage.json');
        await promisify(execFile)('dotnet', [desktop, '--smoke', profile, origin, 'storm-check', report], { timeout: 120_000 })
            .catch(async error => { if (!(await readFile(report).catch(() => null))) throw error; });
        const result = JSON.parse(await readFile(report, 'utf8'));
        assert.equal(result.error, null, result.error);
        const stored = result.checks.find(check => check.startsWith('storm records contiguous'));
        const max = Number(/max (\d+)/.exec(stored)[1]);
        assert.ok(max >= armed.before + 100, `records committed before arming were lost (${max} < ${armed.before + 100})`);
        await writeFile(join(evidence, 'power-check.json'), JSON.stringify({ armed, stored, max }, null, 2));
        console.log(`PASS power loss recovery · ${stored} · armed after >= ${armed.before + 100}`);
    }
} finally {
    server.close();
}
