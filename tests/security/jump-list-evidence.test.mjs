import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, utimes, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { inspectJumpListChanges, snapshotJumpLists } from './jump-list-evidence.mjs';

test('detects profile and unique fixture markers in both kinds of changed Jump Lists', async t => {
    const root = await mkdtemp(join(tmpdir(), 'veil-jump-list-'));
    t.after(() => rm(root, { recursive: true, force: true }));
    const markers = {
        profile: 'V:\\VEIL-Lab\\probe-abc\\profile',
        fixtureHost: '127.0.0.1:43123',
        visitTitleMarker: 'VEIL synthetic visit 0564c24e',
    };
    const automatic = 'AutomaticDestinations/0123456789abcdef.automaticDestinations-ms';
    const custom = 'CustomDestinations/fedcba9876543210.customDestinations-ms';
    assert.equal((await snapshotJumpLists(root)).size, 0);

    await mkdir(join(root, 'AutomaticDestinations'));
    await mkdir(join(root, 'CustomDestinations'));
    const time = new Date('2026-01-01T00:00:00Z');
    const path = join(root, automatic);
    const previous = Buffer.from('V:\\VEIL-Lab\\probe-old\\profile', 'utf16le');
    await writeFile(path, Buffer.concat([Buffer.from([4]), previous]));
    await utimes(path, time, time);
    const baseline = await snapshotJumpLists(root);
    await writeFile(path, Buffer.concat([Buffer.from([4]), Buffer.from(markers.profile.toUpperCase(), 'utf16le')]));
    await utimes(path, time, time);
    await writeFile(join(root, custom), Buffer.from(`http://${markers.fixtureHost}/visit?n=5\0${markers.visitTitleMarker} 5`));
    await writeFile(join(root, 'CustomDestinations', 'unexpected.txt'), markers.profile);
    const after = await snapshotJumpLists(root);
    const changes = inspectJumpListChanges(baseline, after, markers);

    assert.deepEqual(changes.map(({ bytes, ...finding }) => finding), [
        { file: automatic, mentionsProfile: true, fixtureHost: false, visitTitle: false },
        { file: custom, mentionsProfile: false, fixtureHost: true, visitTitle: true },
    ]);
    assert.deepEqual(inspectJumpListChanges(after, after, markers), []);
});

test('ignores unrelated loopback hosts and previously used fixture titles', () => {
    const after = new Map([['some-file', {
        digest: 'changed',
        bytes: Buffer.from('127.0.0.1:1234 VEIL synthetic visit prior-run /visit?n=1', 'utf16le'),
    }]]);
    const [finding] = inspectJumpListChanges(new Map(), after, {
        profile: 'C:\\VEIL-Lab\\probe',
        fixtureHost: '127.0.0.1:43210',
        visitTitleMarker: 'VEIL synthetic visit current-run',
    });
    assert.equal(finding.mentionsProfile || finding.fixtureHost || finding.visitTitle, false);
});
