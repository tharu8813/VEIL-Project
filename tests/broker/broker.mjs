// ADR-0003 volume broker: unprivileged host tests against the fake BitLocker backend.
// Real BitLocker/VHDX operations need an elevated broker and run only in the isolated VM.
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { connect, createServer } from 'node:net';
import { isAbsolute, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';

const run = promisify(execFile);
const args = process.argv.slice(2);
const desktopBin = args[0] === '--desktop-bin' && args[1] && isAbsolute(args[1])
    ? args[1] : resolve('apps/desktop/bin/Debug/net10.0-windows');
const brokerExe = resolve('apps/broker/Veil.VolumeBroker/bin/Debug/net10.0-windows/Veil.VolumeBroker.exe');
const desktopExe = join(desktopBin, 'Veil.Desktop.exe');
const root = resolve('.veil-dev');
await mkdir(root, { recursive: true });
const directory = await mkdtemp(join(root, 'broker-test-'));
const profile = join(directory, 'profile');
await mkdir(profile);

const userSid = (await run(join(process.env.SystemRoot, 'System32', 'whoami.exe'), ['/user', '/fo', 'csv', '/nh'])).stdout.trim().split(',').at(-1).replaceAll('"', '');
const systemGuid = (await run(join(process.env.SystemRoot, 'System32', 'mountvol.exe'), [resolve('/').slice(0, 3), '/L'])).stdout.trim();
assert.match(userSid, /^S-1-5-/);
assert.match(systemGuid, /^\\\\\?\\Volume\{[0-9a-f-]+\}\\$/i);
const otherGuid = '\\\\?\\Volume{00000000-0000-4000-8000-000000000001}\\';
const secret = `RIGHT-${randomBytes(12).toString('hex')}`;
const wrongSecret = `WRONG-${randomBytes(12).toString('hex')}`;
const volumeId = 'veil-test';
const marker = join(directory, 'open-handles');
const brokers = [];
const evidence = {};

function config(name, overrides = {}) {
    return {
        volumeId,
        imagePath: 'C:\\VEIL-Test\\synthetic.vhdx',
        volumeGuid: systemGuid,
        protectorId: '{11111111-2222-4333-8444-555555555555}',
        userSid,
        clientImages: [process.execPath, desktopExe],
        pipeName: `VEIL.Test.${randomBytes(6).toString('hex')}`,
        backend: 'fake',
        development: true,
        allowUnsignedClients: false,
        logPath: join(directory, `${name}.log`),
        fake: { secretSha256: createHash('sha256').update(secret).digest('hex'), handleMarker: marker },
        ...overrides,
    };
}

async function startBroker(name, settings, flag = '--dev-config') {
    const path = join(directory, `${name}.json`);
    await writeFile(path, JSON.stringify(settings, null, 2));
    const child = spawn(brokerExe, [flag, path], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    const broker = { name, settings, child, stdout: '', stderr: '' };
    child.stdout.on('data', chunk => { broker.stdout += chunk; });
    child.stderr.on('data', chunk => { broker.stderr += chunk; });
    broker.exit = new Promise(done => child.on('exit', code => done(code)));
    brokers.push(broker);
    const stopAt = Date.now() + 15000;
    while (!broker.stdout.includes('"event":"ready"')) {
        const code = await Promise.race([broker.exit, delay(100).then(() => undefined)]);
        if (code !== undefined) return { ...broker, code };
        if (Date.now() > stopAt) throw new Error(`${name} broker did not start`);
    }
    return broker;
}

function send(broker, payload) {
    return new Promise(done => {
        const socket = connect(`\\\\.\\pipe\\${broker.settings.pipeName}`);
        let data = '';
        socket.on('data', chunk => { data += chunk; });
        socket.on('error', error => done({ connectError: error.code }));
        socket.on('end', () => done(data ? JSON.parse(data.split('\n')[0]) : { closed: true }));
        socket.on('connect', () => socket.write(typeof payload === 'string' ? payload : JSON.stringify(payload) + '\n'));
    });
}

const command = (broker, name, extra = {}) => send(broker, { command: name, volumeId, ...extra });

async function gate(clientSettings) {
    const path = join(directory, `client-${randomBytes(4).toString('hex')}.json`);
    const report = path.replace('.json', '-report.json');
    await writeFile(path, JSON.stringify({ development: true, volumeId, ...clientSettings }));
    let exitCode = 0;
    await run(desktopExe, ['--broker-status', path, profile, report], { timeout: 30000 })
        .catch(error => { exitCode = error.code; });
    return { exitCode, ...JSON.parse(await readFile(report, 'utf8')) };
}

try {
    // A: signed clients only, lock/unlock life cycle, malformed requests, secret handling.
    const a = await startBroker('a', config('a'));
    assert.deepEqual((await command(a, 'Status')).status.attached, false);
    assert.deepEqual(await command(a, 'AuthenticatedOwner'), { ok: false, error: 'NotAuthenticatedOwner' });
    assert.deepEqual(await command(a, 'CheckCefChild', { childHandle: '1' }),
        { ok: false, error: 'NotAuthenticatedOwner' });
    const malformed = {
        notJson: 'not json\n',
        extraPath: { command: 'Status', volumeId, path: 'C:\\Windows' },
        lowercase: { command: 'status', volumeId },
        unknown: { command: 'Format', volumeId },
        numeric: { command: 0, volumeId },
        duplicate: `{"command":"Status","command":"Lock","volumeId":"${volumeId}"}\n`,
        unlockWithoutSecret: { command: 'Unlock', volumeId },
        statusWithSecret: { command: 'Status', volumeId, secret },
        ownerWithSecret: { command: 'AuthenticatedOwner', volumeId, secret },
        ownerWithPid: { command: 'AuthenticatedOwner', volumeId, pid: process.pid },
        cefMissingHandle: { command: 'CheckCefChild', volumeId },
        cefNumericHandle: { command: 'CheckCefChild', volumeId, childHandle: 1 },
        cefHandleOnStatus: { command: 'Status', volumeId, childHandle: '1' },
        cefWithSecret: { command: 'CheckCefChild', volumeId, childHandle: '1', secret },
        oversized: JSON.stringify({ command: 'Status', volumeId: 'x'.repeat(5000) }) + '\n',
        trailing: `{"command":"Status","volumeId":"${volumeId}"} {}\n`,
    };
    for (const [name, payload] of Object.entries(malformed)) {
        assert.deepEqual(await send(a, payload), { ok: false, error: 'BadRequest' }, name);
    }
    assert.deepEqual(await command(a, 'Status', { volumeId: 'other' }), { ok: false, error: 'UnknownVolume' });
    assert.deepEqual(await command(a, 'Unlock', { secret }), { ok: false, error: 'NotAttached' });
    const attached = (await command(a, 'Attach')).status;
    assert.equal(attached.attached, true);
    assert.equal(attached.readyToUnlock, true);
    assert.equal(attached.protectionOn, false, 'locked metadata is not an attestation');
    assert.deepEqual(await command(a, 'Attach'), { ok: false, error: 'AlreadyAttached' });
    const started = Date.now();
    const wrong = await command(a, 'Unlock', { secret: wrongSecret });
    evidence.wrongSecretDelayMs = Date.now() - started;
    assert.deepEqual(wrong, { ok: false, error: 'AccessDenied' });
    assert.ok(evidence.wrongSecretDelayMs >= 400, 'failed unlock was not delayed');
    evidence.unsignedDesktop = await gate({ pipeName: a.settings.pipeName });
    assert.equal(evidence.unsignedDesktop.exitCode, 1);
    assert.match(evidence.unsignedDesktop.error, /ProfileLocationException: .*ClientRejected/);
    assert.equal((await command(a, 'Unlock', { secret })).status.unlocked, true);
    assert.equal((await command(a, 'AuthenticatedOwner')).status.unlocked, true);
    assert.deepEqual(await command(a, 'CheckCefChild', { childHandle: '1' }),
        { ok: false, error: 'CefUnavailable' });
    assert.deepEqual(await command(a, 'StageCefChild', { childHandle: '1' }),
        { ok: false, error: 'CefUnavailable' });
    const ownerCheckScript = `const s=require('net').connect(process.argv[1]);s.on('connect',()=>s.write(JSON.stringify({command:'AuthenticatedOwner',volumeId:'${volumeId}'})+'\\n'));s.on('data',d=>{process.stdout.write(d);process.exit(0);});`;
    const otherOwner = await run(process.execPath, ['-e', ownerCheckScript, `\\\\.\\pipe\\${a.settings.pipeName}`]);
    assert.deepEqual(JSON.parse(otherOwner.stdout), { ok: false, error: 'NotAuthenticatedOwner' });
    assert.deepEqual(await command(a, 'Unlock', { secret }), { ok: false, error: 'AlreadyUnlocked' },
        'an unlocked volume must not silently accept another Unlock');
    assert.deepEqual(await command(a, 'Detach'), { ok: false, error: 'MustBeLocked' });
    assert.deepEqual(await command(a, 'AuthenticatedOwner'), { ok: false, error: 'NotAuthenticatedOwner' });
    assert.deepEqual(await command(a, 'StageCefChild', { childHandle: '1' }),
        { ok: false, error: 'NotAuthenticatedOwner' });
    assert.equal((await command(a, 'Status')).status.unlocked, true, 'refused detach must not report locked');
    assert.equal((await command(a, 'Lock')).status.unlocked, false);
    assert.equal((await command(a, 'Unlock', { secret })).status.unlocked, true);
    assert.equal((await command(a, 'AuthenticatedOwner')).status.unlocked, true, 'fresh unlock restores owner authority');
    await writeFile(marker, 'synthetic open handle');
    assert.deepEqual(await command(a, 'Lock'), { ok: false, error: 'HandlesOpen' });
    assert.equal((await command(a, 'Status')).status.unlocked, true, 'refused lock must not report locked');
    assert.deepEqual(await command(a, 'AuthenticatedOwner'), { ok: false, error: 'NotAuthenticatedOwner' });
    assert.deepEqual(await command(a, 'CheckCefChild', { childHandle: '1' }),
        { ok: false, error: 'NotAuthenticatedOwner' });
    assert.deepEqual(await command(a, 'StageCefChild', { childHandle: '1' }),
        { ok: false, error: 'NotAuthenticatedOwner' });
    await rm(marker);
    assert.equal((await command(a, 'Lock')).status.unlocked, false);
    assert.deepEqual(await command(a, 'AuthenticatedOwner'), { ok: false, error: 'NotAuthenticatedOwner' });

    // The client that unlocked exits without locking: the broker locks on its behalf.
    const clientScript = `const s=require('net').connect(process.argv[1]);s.on('connect',()=>s.write(JSON.stringify({command:'Unlock',volumeId:'${volumeId}',secret:process.argv[2]})+'\\n'));s.on('data',d=>{process.stdout.write(d);process.exit(0);});`;
    const child = await run(process.execPath, ['-e', clientScript, `\\\\.\\pipe\\${a.settings.pipeName}`, secret]);
    assert.equal(JSON.parse(child.stdout).status.unlocked, true);
    let relocked = false;
    for (let i = 0; i < 40 && !relocked; i++) {
        relocked = (await command(a, 'Status')).status.unlocked === false;
        if (!relocked) await delay(250);
    }
    assert.ok(relocked, 'volume stayed unlocked after the unlocking client exited');
    assert.deepEqual(await command(a, 'AuthenticatedOwner'), { ok: false, error: 'NotAuthenticatedOwner' });
    assert.equal((await command(a, 'Detach')).status.attached, false);

    // The fake OS unlocks, then throws before returning its result. The child
    // must receive failure and its exit must still trigger the broker's watcher.
    const ambiguousConfig = config('ambiguous-unlock');
    ambiguousConfig.fake.throwAfterUnlock = true;
    const ambiguous = await startBroker('ambiguous-unlock', ambiguousConfig);
    assert.equal((await command(ambiguous, 'Attach')).status.attached, true);
    const ambiguousChild = await run(process.execPath, ['-e', clientScript,
        `\\\\.\\pipe\\${ambiguous.settings.pipeName}`, secret]);
    assert.deepEqual(JSON.parse(ambiguousChild.stdout), { ok: false, error: 'BackendFailure' });
    let ambiguousRelocked = false;
    for (let i = 0; i < 40 && !ambiguousRelocked; i++) {
        ambiguousRelocked = (await command(ambiguous, 'Status')).status.unlocked === false;
        if (!ambiguousRelocked) await delay(250);
    }
    assert.ok(ambiguousRelocked, 'ambiguous unlock remained open after client exit');
    assert.deepEqual(await command(ambiguous, 'AuthenticatedOwner'), { ok: false, error: 'NotAuthenticatedOwner' });
    assert.match(ambiguous.stdout, /"event":"client-exit-lock"[^\n]*"result":"ok"/);
    assert.equal((await command(ambiguous, 'Detach')).status.attached, false);

    // B: unsigned development client allowed; the startup gate follows the broker state.
    const b = await startBroker('b', config('b', { allowUnsignedClients: true }));
    evidence.gateDetached = await gate({ pipeName: b.settings.pipeName });
    assert.match(evidence.gateDetached.error, /not on the registered protected volume/);
    await command(b, 'Attach');
    evidence.gateLocked = await gate({ pipeName: b.settings.pipeName });
    assert.match(evidence.gateLocked.error, /not on the registered protected volume/);
    await command(b, 'Unlock', { secret });
    evidence.gateUnlocked = await gate({ pipeName: b.settings.pipeName, brokerImage: brokerExe });
    assert.equal(evidence.gateUnlocked.exitCode, 0, evidence.gateUnlocked.error);
    assert.equal(evidence.gateUnlocked.Protected, true);
    assert.equal(evidence.gateUnlocked.VolumeGuid.toLowerCase(), systemGuid.toLowerCase());
    evidence.gateWrongServer = await gate({ pipeName: b.settings.pipeName, brokerImage: process.execPath });
    assert.match(evidence.gateWrongServer.error, /unexpected process/);
    evidence.gateWrongVolume = await gate({ pipeName: b.settings.pipeName, volumeId: 'other' });
    assert.match(evidence.gateWrongVolume.error, /UnknownVolume/);
    await command(b, 'Lock');
    evidence.gateRelocked = await gate({ pipeName: b.settings.pipeName });
    assert.match(evidence.gateRelocked.error, /not on the registered protected volume/);
    evidence.gateNoBroker = await gate({ pipeName: `VEIL.Test.absent-${randomBytes(4).toString('hex')}` });
    assert.match(evidence.gateNoBroker.error, /volume broker is unavailable/);

    // An unresponsive named-pipe peer must fail closed within the configured timeout.
    // Use a synthetic development-only broker with no trusted-image requirement.
    const stalledName = `VEIL.Test.stalled-${randomBytes(5).toString('hex')}`;
    const stalledPipe = `\\\\.\\pipe\\${stalledName}`;
    const stalledClients = new Set();
    const stalled = createServer(socket => {
        stalledClients.add(socket);
        socket.on('close', () => stalledClients.delete(socket));
        socket.on('data', () => { /* deliberately no reply */ });
    });
    try {
        await new Promise((ok, fail) => {
            stalled.once('error', fail);
            stalled.listen(stalledPipe, ok);
        });
        const begun = Date.now();
        evidence.gateStalled = await gate({ pipeName: stalledName });
        evidence.gateStalledMs = Date.now() - begun;
        assert.equal(evidence.gateStalled.exitCode, 1);
        assert.match(evidence.gateStalled.error, /ProfileLocationException/);
        assert.ok(evidence.gateStalledMs < 14000, 'stalled IPC did not time out');
    } finally {
        for (const socket of stalledClients) socket.destroy();
        await new Promise(ok => stalled.close(ok));
    }

    // Pipe name already owned by another broker instance.
    const squat = await startBroker('squat', config('squat', { pipeName: b.settings.pipeName }));
    assert.equal(squat.code, 5, 'second owner of a pipe name must fail');

    // C: registered volume on another disk than the profile.
    const c = await startBroker('c', config('c', { volumeGuid: otherGuid, allowUnsignedClients: true }));
    await command(c, 'Attach');
    await command(c, 'Unlock', { secret });
    evidence.gateOtherVolume = await gate({ pipeName: c.settings.pipeName });
    assert.match(evidence.gateOtherVolume.error, /not on the registered protected volume/);

    // D: image content does not match the registration; E: protector missing.
    const d = await startBroker('d', config('d', { fake: { ...config('x').fake, reportedVolumeGuid: otherGuid } }));
    assert.deepEqual(await command(d, 'Attach'), { ok: false, error: 'VolumeMismatch' });
    assert.equal((await command(d, 'Status')).status.attached, false);
    const e = await startBroker('e', config('e', { fake: { ...config('x').fake, reportedProtectorPresent: false } }));
    assert.deepEqual(await command(e, 'Attach'), { ok: false, error: 'VolumeMismatch' });

    const unprotectedConfig = config('protection-off');
    unprotectedConfig.fake.reportedProtectionOnAfterUnlock = false;
    const unprotected = await startBroker('protection-off', unprotectedConfig);
    assert.equal((await command(unprotected, 'Attach')).status.readyToUnlock, true);
    assert.deepEqual(await command(unprotected, 'Unlock', { secret }), { ok: false, error: 'NotProtected' });
    assert.equal((await command(unprotected, 'Status')).status.unlocked, false, 'failed verification must relock');
    assert.deepEqual(await command(unprotected, 'AuthenticatedOwner'), { ok: false, error: 'NotAuthenticatedOwner' });

    // F: client executable not registered. G: pipe ACL for another user.
    const f = await startBroker('f', config('f', { clientImages: [desktopExe] }));
    assert.deepEqual(await command(f, 'Status'), { ok: false, error: 'ClientRejected' });
    const g = await startBroker('g', config('g', { userSid: 'S-1-5-21-1000-2000-3000-1001' }));
    evidence.otherUserConnect = await command(g, 'Status');
    assert.ok(['EACCES', 'EPERM'].includes(evidence.otherUserConnect.connectError), JSON.stringify(evidence.otherUserConnect));

    // Production rules refuse a user-writable configuration and a development config.
    const production = await startBroker('production', config('production', {
        development: false, backend: 'bitlocker', fake: undefined, logPath: undefined,
    }), '--config');
    assert.equal(production.code, 3);
    assert.match(production.stderr, /UnauthorizedAccessException/);
    const devAsProduction = await startBroker('dev-as-production', config('dev-as-production'), '--config');
    assert.equal(devAsProduction.code, 3);
    const bitlockerUnelevated = await startBroker('bitlocker-dev', config('bitlocker-dev', { backend: 'bitlocker', fake: undefined }));
    assert.equal(bitlockerUnelevated.code, 4);

    for (const broker of brokers) broker.child.stdin.end();
    await Promise.all(brokers.map(broker => broker.exit));
    let logs = '';
    for (const broker of brokers) {
        logs += broker.stdout + broker.stderr + await readFile(broker.settings.logPath ?? '', 'utf8').catch(() => '');
    }
    assert.ok(logs.includes('"result":"AccessDenied"') && logs.includes('client-exit-lock'));
    for (const value of [secret, wrongSecret]) assert.ok(!logs.includes(value), 'a secret reached a broker log');
    evidence.logBytes = logs.length;
    await writeFile(join(directory, 'evidence.json'), JSON.stringify(evidence, null, 2));
    console.log('PASS volume broker fake-backend scenarios', directory);
} finally {
    for (const broker of brokers) if (broker.child.exitCode === null) broker.child.kill();
}
