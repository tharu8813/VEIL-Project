// SPDX-License-Identifier: MPL-2.0
// ADR-0010 package versions, executed in Windows PowerShell 5.1 against scripts/package-common.ps1:
// SemVer with alpha/beta/rc prereleases, update order, Windows file-version mapping and the internal
// channel rule (no stable 1.x+). Synthetic inputs only; nothing is built, signed or installed.
//   node --test tests/install/package-version.test.mjs
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { resolve } from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';

const run = promisify(execFile);
const common = resolve('scripts/package-common.ps1');

async function ps(body) {
    const script = `$ErrorActionPreference = 'Stop'; . '${common.replace(/'/g, "''")}'; ${body}`;
    const { stdout } = await run('powershell', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script]);
    return stdout.trim();
}

const parse = async version => ps(`try { (Get-VeilVersion '${version}').ToString() } catch { 'ERROR' }`);
const internal = async version => ps(`try { (Assert-VeilInternalVersion '${version}').ToString() } catch { 'REFUSED' }`);

test('versions map to Windows file versions', { skip: process.platform !== 'win32' }, async () => {
    const cases = {
        '0.1.0': '0.1.0.1000',
        '1.0.0-alpha.1': '1.0.0.1',
        '1.0.0-alpha.99': '1.0.0.99',
        '1.0.0-beta.1': '1.0.0.101',
        '1.0.0-rc.2': '1.0.0.202',
        '1.0.0': '1.0.0.1000',
        '2.10.3-beta.4': '2.10.3.104',
    };
    for (const [version, file] of Object.entries(cases)) assert.equal(await parse(version), file, version);
});

test('malformed or ambiguous versions are refused', { skip: process.platform !== 'win32' }, async () => {
    for (const version of ['1.0', '1.0.0.0', '01.0.0', '1.0.0-alpha', '1.0.0-alpha.0', '1.0.0-alpha.100',
        '1.0.0-Alpha.1', '1.0.0-dev.1', '1.0.0-alpha.1+build', '1.0.0-alpha.01', ' 1.0.0', '1.0.0-alpha.1 ', '65536.0.0'])
        assert.equal(await parse(version), 'ERROR', JSON.stringify(version));
});

test('update order follows SemVer precedence', { skip: process.platform !== 'win32' }, async () => {
    const ordered = ['0.1.0', '0.2.0', '1.0.0-alpha.1', '1.0.0-alpha.2', '1.0.0-alpha.10', '1.0.0-beta.1', '1.0.0-rc.1', '1.0.0', '1.0.1-alpha.1'];
    const list = ordered.map(v => `'${v}'`).join(',');
    // Same comparison as package-maintenance.ps1: a candidate must be strictly greater than the installed one.
    const result = await ps(`$v = @(${list}); $ok = $true; for ($i = 1; $i -lt $v.Count; $i++) {
        if (-not ((Get-VeilVersion $v[$i]) -gt (Get-VeilVersion $v[$i-1]))) { $ok = $false; "out of order: $($v[$i-1]) -> $($v[$i])" } }
        if ((Get-VeilVersion '1.0.0-alpha.1') -le (Get-VeilVersion '1.0.0-alpha.1')) { 'same version refused' } else { 'same version accepted' }
        if ($ok) { 'ordered' }`);
    assert.match(result, /same version refused/);
    assert.match(result, /ordered/, result);
});

test('internal channel allows 0.x and prereleases but not stable 1.x+', { skip: process.platform !== 'win32' }, async () => {
    assert.equal(await internal('0.3.1'), '0.3.1.1000');
    assert.equal(await internal('1.0.0-alpha.1'), '1.0.0.1');
    assert.equal(await internal('1.0.0-rc.1'), '1.0.0.201');
    assert.equal(await internal('1.0.0'), 'REFUSED');
    assert.equal(await internal('2.0.0'), 'REFUSED');
});
