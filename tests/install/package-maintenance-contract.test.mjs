// SPDX-License-Identifier: MPL-2.0
// Deferred static contract checks; not execution or security evidence.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const common = await readFile('scripts/package-common.ps1','utf8');
const maintenance = await readFile('scripts/package-maintenance.ps1','utf8');
test('staged inventory is authenticated and exact',()=>{
    assert.match(common,/CheckSignature\(\$false\)/);
    assert.match(common,/SignerInfos\[0\]\.Certificate\.Thumbprint/);
    assert.match(common,/Package hash or length mismatch/);
    assert.match(common,/Unexpected package file/);
    assert.match(maintenance,/Copy-StagedPackage/);
});
test('upgrade stops broker, preserves registration and commits before activation',()=>{
    assert.match(maintenance,/Disable-Broker[\s\S]*?Move\(\$bin,\$previous\)[\s\S]*?Set-BrokerHash[\s\S]*?Write-Durable \$committed[\s\S]*?Start-Broker/);
    assert.match(maintenance,/Automatic downgrade is forbidden/);
    assert.match(maintenance,/profileFormat -ne \$installed\.profileFormat/);
    assert.doesNotMatch(maintenance,/Stop-Process|Remove-RestrictedTree \$data\b/);
});
test('removal preserves registered data and volume',()=>{
    assert.match(maintenance,/Assert-ClosedAndDetached/);
    assert.match(maintenance,/Binaries removed; encrypted VHDX and registration retained/);
});

test('browser candidate is explicitly registered from verified installed package, not forced default',()=>{
    assert.match(maintenance,/if \(\$RegisterBrowser\)[\s\S]*?Read-VeilPackage \$bin \$SignerThumbprint[\s\S]*?Installed client pin mismatch[\s\S]*?Register-VeilBrowser/);
    assert.match(maintenance,/--open-url "%1"/);
    assert.match(maintenance,/URLAssociations/);
    assert.doesNotMatch(maintenance,/UserChoice|SetUserFTA|SetDefaultBrowser/);
    assert.match(maintenance,/Remove-VeilBrowserRegistration\s+Remove-RestrictedTree \$bin/);
});
