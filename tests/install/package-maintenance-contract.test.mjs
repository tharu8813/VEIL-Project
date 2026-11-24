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
test('packages carry ADR-0010 versions, notices and SBOM',async()=>{
    const build = await readFile('scripts/build-package.ps1','utf8');
    assert.match(build,/Assert-VeilInternalVersion \$Version/);
    assert.match(build,/-p:FileVersion=\$fileVersion/);
    assert.match(build,/third-party\.mjs --notices[^\n]*THIRD-PARTY-NOTICES\.md[^\n]*--sbom[^\n]*sbom\.cdx\.json/);
    assert.match(maintenance,/Assert-VeilInternalVersion \$manifest\.version/);
    assert.doesNotMatch(build + maintenance,/Major -ne 0/);
});
