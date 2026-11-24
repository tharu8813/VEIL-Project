import assert from 'node:assert/strict';
import test from 'node:test';
import { validatePaths, validateArmed, checkRecovery } from './power-loss.mjs';

const primary = { parent: 'V:\\lab' };
const evidence = 'V:\\lab\\power-loss-1';
const profile = evidence + '\\profile';
const current = { uuid: 'test-vm', boot: 'boot-2', volumeId: 'volume-test', image: 'C:\\test.vhdx',
    profile, evidence, desktopSha256: 'desktop-hash', vaultSha256: 'vault-hash', origin: 'http://127.0.0.1:47391' };
const armed = { schema: 2, ...current, boot: 'boot-1', before: 10, committed: 115 };

test('power loss uses a dedicated synthetic profile on the verified volume', () => {
    validatePaths(primary, evidence, profile);
    for (const [e, p] of [['C:\\lab\\power-loss-1', profile], [primary.parent, profile],
        [evidence, 'V:\\lab\\other'], ['V:\\lab\\nested\\power-loss-1', profile], ['V:\\lab\\ordinary', profile]])
        assert.throws(() => validatePaths(primary, e, p));
});
test('arm proof requires a different boot, same VM, volume, paths, build and origin', () => {
    validateArmed(armed, current, true);
    assert.throws(() => validateArmed(armed, current, false));
    for (const change of [{ boot: armed.boot }, { uuid: 'other' }, { volumeId: 'other' }, { image: 'C:\\other.vhdx' },
        { profile: 'V:\\other' }, { evidence: 'V:\\other' }, { desktopSha256: 'changed' },
        { vaultSha256: 'changed' }, { origin: 'http://127.0.0.1:1234' }])
        assert.throws(() => validateArmed(armed, { ...current, ...change }, true));
    for (const change of [{ schema: 1 }, { committed: 109 }, { before: -1 }, { committed: 115.5 }])
        assert.throws(() => validateArmed({ ...armed, ...change }, current, true));
});
test('recovery cannot pass after process failure, missing evidence or loss of confirmed records', () => {
    const good = { error: null, checks: ['storm records contiguous (max 120, localStorage 119)'] };
    assert.equal(checkRecovery(armed, good, 0).max, 120);
    assert.throws(() => checkRecovery(armed, good, 1));
    assert.throws(() => checkRecovery(armed, { ...good, error: 'failed' }, 0));
    assert.throws(() => checkRecovery(armed, { ...good, checks: [] }, 0));
    assert.throws(() => checkRecovery(armed, { ...good, checks: ['storm records contiguous (max 114, localStorage 114)'] }, 0));
});
