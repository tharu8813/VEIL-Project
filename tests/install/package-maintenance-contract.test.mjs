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

test('browser candidate is explicitly registered from verified installed package, not forced default',()=>{
    assert.match(maintenance,/if \(\$RegisterBrowser\)[\s\S]*?Read-VeilPackage \$bin \$SignerThumbprint[\s\S]*?Installed client pin mismatch[\s\S]*?Register-VeilBrowser/);
    assert.match(maintenance,/--open-url "%1"/);
    assert.match(maintenance,/URLAssociations/);
    assert.doesNotMatch(maintenance,/UserChoice|SetUserFTA|SetDefaultBrowser/);
    assert.match(maintenance,/Remove-VeilBrowserRegistration\s+Remove-RestrictedTree \$bin/);
});

test('fresh install refuses existing registration before staging; invalid versions clean owned staging',()=>{
    const staging=maintenance.indexOf('$manifest=Copy-StagedPackage');
    const preflight=maintenance.indexOf("throw 'Existing installation/registration requires administrator recovery, not a fresh install.'");
    assert.ok(preflight>=0 && preflight<staging);
    assert.match(maintenance.slice(maintenance.lastIndexOf('if ($Install)',preflight),preflight),/Get-CimInstance Win32_Service/);
    assert.match(maintenance,/try\s*\{\s*\$manifest=Copy-StagedPackage\s*\[void\]\(Assert-VeilInternalVersion \$manifest.version\)\s*\}\s*catch\s*\{\s*if \(Test-Path -LiteralPath \$stage\) \{ Remove-RestrictedTree \$stage \}\s*throw/);
    assert.ok(maintenance.indexOf("throw 'Unresolved maintenance state.")<preflight);
});


test('package staging uses shared byte copy and authenticates only after copying', () => {
    assert.match(common,/function Copy-VeilPackageFiles[\s\S]*?Get-VeilTreeFiles \$Source -WithRelativePath[\s\S]*?Assert-VeilRelativeFile \$relative[\s\S]*?\[IO\.File\]::Copy/);
    assert.match(maintenance,/New-VeilRestrictedDirectory \$stage\s+Copy-VeilPackageFiles \$source \$stage\s+return Read-VeilPackage \$stage \$SignerThumbprint/);
    assert.match(maintenance,/catch\s*\{\s*if \(Test-Path -LiteralPath \$stage\) \{ Remove-RestrictedTree \$stage \}\s*throw/);
});
test('disk-full verdict requires actual Windows disk-full IO error', async () => {
    const { assertCopyFailure } = await import('../vm/package-staging-disk-full.mjs');
    assert.doesNotThrow(() => assertCopyFailure({ status: 'FAILED', ioCodes: [112] }));
    assert.doesNotThrow(() => assertCopyFailure({ status: 'FAILED', ioCodes: [39] }));
    for (const result of [{ status: 'COPIED', ioCodes: [] }, { status: 'FAILED', ioCodes: [5] },
        { status: 'FAILED', ioCodes: [] }, { status: 'FAILED', ioCodes: ['112'] }])
        assert.throws(() => assertCopyFailure(result));
});
