import assert from 'node:assert/strict';
import test from 'node:test';
import { executePlan, makePlan, validatePreflight } from './phase34-vm.mjs';

const guest = {
    virtualBox: true, noDefaultRoute: true, elevated: false, uuid: 'lab-guest', boot: 'lab-boot', systemDrive: 'C:',
};
const main = {
    role: 'primary', drive: 'V:', parent: 'V:\\lab', image: 'C:\\lab.vhdx',
    volumeId: '\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\',
};
const small = {
    role: 'disk-full', drive: 'W:', parent: 'W:\\small', image: 'C:\\small.vhdx',
    volumeId: '\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\', capacity: 128 * 1024 ** 2,
};
const receipt = (...volumes) => ({ schema: 1, guest: { ...guest, elevated: true }, volumes });

test('dry plan marks every item NOT RUN, including optional and physical tests', async () => {
    const results = await executePlan(makePlan(main.parent), [], null, () => {});
    assert.ok(results.every(item => item.status === 'NOT RUN'));
    assert.equal(results.find(item => item.id === 'profile-disk-full').args, undefined);
    assert.equal(results.find(item => item.id === 'power-loss').args, undefined);
});

test('keeps separate outcomes and continues after a failed runner', async () => {
    const calls = [];
    const reports = [];
    const results = await executePlan(makePlan(main.parent, small.parent),
        ['jump-list', 'profile-disk-full'], async test => {
            calls.push(test.id);
            if (test.id === 'jump-list') throw new Error('simulated runner failure');
            assert.ok(test.args.includes(small.parent));
            return { exitCode: 0, log: 'mock.log' };
        }, report => reports.push(report.map(test => test.status)));
    assert.deepEqual(calls, ['jump-list', 'profile-disk-full']);
    assert.equal(results.find(test => test.id === 'jump-list').status, 'FAIL');
    assert.equal(results.find(test => test.id === 'profile-disk-full').status, 'PASS');
    assert.equal(results.find(test => test.id === 'installed-release').status, 'NOT RUN');
    assert.ok(reports.some(report => report.includes('RUNNING')));
});

test('refuses substituted or unsafe guest and volume proofs', () => {
    assert.deepEqual(validatePreflight(receipt(main, small), guest), { primary: main, diskFull: small });
    for (const change of [{ boot: 'new-boot' }, { noDefaultRoute: false }, { elevated: true }, { virtualBox: false }]) {
        assert.throws(() => validatePreflight(receipt(main), { ...guest, ...change }));
    }
    assert.throws(() => validatePreflight(receipt({ ...main, parent: 'C:\\lab' }), guest));
    assert.throws(() => validatePreflight(receipt({ ...main, drive: 'C:', parent: 'C:\\lab' }), guest));
    assert.throws(() => validatePreflight(receipt(main, { ...small, image: main.image }), guest));
    assert.throws(() => validatePreflight(receipt(main, { ...small, capacity: 3 * 1024 ** 3 }), guest));
});
