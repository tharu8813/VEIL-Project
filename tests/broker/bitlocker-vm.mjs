// Run only in an isolated Windows VM against its registered disposable VHDX.
// The console harness reads the one-use BitLocker passphrase from stdin.
import assert from 'node:assert/strict';
import { connect } from 'node:net';
import { readFile } from 'node:fs/promises';

const [configPath] = process.argv.slice(2);
const config = JSON.parse((await readFile(configPath, 'utf8')).replace(/^\uFEFF/, ''));
assert.equal(config.development, true);
assert.equal(config.backend, 'bitlocker');
assert.match(config.pipeName, /^VEIL\.Lab\./);
assert.equal(config.volumeId, 'isolated-vm');

async function command(name, extra = {}) {
    const socket = connect(`\\\\.\\pipe\\${config.pipeName}`);
    let reply = '';
    return await new Promise((resolve, reject) => {
        socket.setTimeout(75_000, () => socket.destroy(new Error(`Timed out: ${name}`)));
        socket.once('error', reject);
        socket.once('connect', () => socket.write(JSON.stringify({ command: name, volumeId: config.volumeId, ...extra }) + '\n'));
        socket.on('data', chunk => {
            reply += chunk.toString('utf8');
            if (reply.length > 4096) socket.destroy(new Error('Broker response too large'));
            if (reply.includes('\n')) {
                try { resolve(JSON.parse(reply.split('\n')[0])); }
                catch (error) { reject(error); }
                socket.destroy();
            }
        });
        socket.once('end', () => reject(new Error(`Incomplete response: ${name}`)));
    });
}

async function requireStatus(name, extra) {
    const response = await command(name, extra);
    if (!response.ok) throw new Error(`${name}: ${response.error}`);
    console.log(name, response.status);
    return response.status;
}

if (process.argv.includes('--status-only')) {
    await requireStatus('Status');
} else {
    let secret = '';
    for await (const chunk of process.stdin) secret += chunk;
    secret = secret.trimEnd();
    if (!secret || secret.length > 256) throw new Error('A disposable test passphrase is required');
    let cleanup = false;
    try {
        assert.equal((await requireStatus('Status')).attached, false, 'Start with the lab VHDX detached');
        cleanup = true;
        const locked = await requireStatus('Attach');
        assert.equal(locked.unlocked, false);
        assert.equal(locked.readyToUnlock, true);
        const unlocked = await requireStatus('Unlock', { secret });
        assert.equal(unlocked.unlocked, true);
        assert.equal(unlocked.fullyEncrypted, true);
        assert.equal(unlocked.protectionOn, true);
        assert.equal(unlocked.protectorPresent, true);
        assert.equal((await requireStatus('Lock')).unlocked, false);
        assert.equal((await requireStatus('Detach')).attached, false);
        cleanup = false;
        console.log('PASS real BitLocker broker lifecycle');
    } finally {
        secret = '';
        if (cleanup) {
            const { status } = await command('Status');
            if (status?.unlocked) await requireStatus('Lock');
            if (status?.attached) await requireStatus('Detach');
        }
    }
}
