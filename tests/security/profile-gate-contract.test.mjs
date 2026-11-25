// SPDX-License-Identifier: MPL-2.0
// Static contract checks; Windows volume identity and filesystem race behavior
// require the existing isolated VM suite.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const gate = await readFile('apps/desktop/ProfileGate.cs', 'utf8');
const startup = await readFile('apps/desktop/Program.cs', 'utf8');

test('missing profile verifies nearest existing parent with the requested protection before creation', () => {
    const prepare = gate.slice(gate.indexOf('internal static ProfileLocation Prepare('),
        gate.indexOf('internal static ProfileLocation Verify('));
    assert.match(prepare, /while \(existing is not null && !existing\.Exists\) existing = existing\.Parent;/);
    assert.match(prepare, /if \(existing is null\)\s+throw new ProfileLocationException/);
    assert.match(prepare, /Verify\(existing\.FullName, broker, requireProtected\);\s+Directory\.CreateDirectory\(requested\);/);
    assert.match(prepare, /return Verify\(requested, broker, requireProtected\);/);
    assert.doesNotMatch(prepare, /UnmanagedDevelopmentVolume|Verify\([^;]*,\s*false\)/);
    assert.equal([...prepare.matchAll(/Directory\.CreateDirectory\(/g)].length, 1);
});

test('protected verification requires the registered encrypted unlocked volume', () => {
    assert.match(gate, /status is \{ FullyEncrypted: true, ProtectionOn: true, Unlocked: true, ProtectorPresent: true \}/);
    assert.match(gate, /string\.Equals\(status\.VolumeGuid, volume, StringComparison\.OrdinalIgnoreCase\)/);
    assert.match(gate, /if \(requireProtected && !verified\)\s+throw new ProfileLocationException/);
    assert.match(startup, /requireProtected = true;[\s\S]*?ProfileGate\.Prepare\(root, volume, requireProtected\);[\s\S]*?new FileStream\(Path\.Combine\(root, "profile\.lock"\)/);
});
