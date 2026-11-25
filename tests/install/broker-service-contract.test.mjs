// Static policy checks, not a substitute for executing installation in an isolated Windows VM.
// No administrator privileges or service/volume mutations are performed by this test.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile('scripts/broker-service.ps1', 'utf8');
const client = await readFile('apps/desktop/BrokerClient.cs', 'utf8');
const service = await readFile('apps/broker/Veil.VolumeBroker/BrokerService.cs', 'utf8');
const backend = await readFile('apps/broker/Veil.VolumeBroker/Backends.cs', 'utf8');

test('production service starts automatically after a reboot', () => {
    assert.match(source, /New-Service\b[^\r\n]*-StartupType\s+Manual\b/);
    assert.match(source, /New-Service\b[^\r\n]*-StartupType\s+Manual\b[^]*?sc\.exe failure[^]*?\$controller\.Start\(\)[^]*?WaitForStatus\('Running'[^]*?Set-Service -Name \$serviceName -StartupType Automatic/);
});

test('broker recovery is bounded and exits on invalid registration', () => {
    assert.match(source, /sc\.exe\s+failure\s+\$serviceName\s+reset=\s+86400\s+actions=\s+restart\/30000\/restart\/60000\/restart\/300000/);
    assert.match(source, /sc\.exe\s+failureflag\s+\$serviceName\s+flag=\s+1/);
    assert.match(source, /Service recovery registration failed/);
    assert.match(source, /Service failure flag registration failed/);
    assert.match(service, /broker\.SecureVolumeAsync\(\)\.GetAwaiter\(\)\.GetResult\(\)/);
});

test('installed browser must require a registered Windows service', () => {
    assert.match(source, /requireWindowsService=\$true/);
    assert.match(client, /!config\.RequireWindowsService/);
    assert.match(client, /The volume broker pipe does not belong to the running Windows service/);
});

test('uninstall must preserve the user VHDX and refuse an attached image', () => {
    assert.match(source, /Assert-Detached\s+\$registered\.imagePath/);
    assert.match(source, /Remove-VeilTree\s+\$binRoot/);
    assert.match(source, /Remove-VeilTree\s+\$dataRoot/);
    assert.doesNotMatch(source, /Remove-Item[^\r\n]*\$ImagePath/i);
});

test('registration requires a verified recovery backup and persists the protector ID only', () => {
    assert.match(source, /\$recoveryId\s*=\s*Register-VeilRecoveryBackup[^]*?recoveryProtectorId=\$recoveryId\.ToString\('B'\)[^]*?New-Service/);
    assert.match(source, /Test-VeilRecoveryBackup\s+\$verifiedImage\s+\$registered\.volumeGuid\s+\$recoveryId/);
    assert.doesNotMatch(source, /NumericalPassword\s*=|RecoveryPassword\s*=/i);
    assert.match(backend, /KeyProtectorType = \[uint32\]2/);
    assert.match(backend, /protector &= [^]*?SameProtector\(id, recovery\)/);
});

test('packaged install authenticates the complete installed tree before service creation', async () => {
    const maintenance = await readFile('scripts/package-maintenance.ps1', 'utf8');
    assert.match(source, /if \(\$PackageRoot\)[^]*?Read-VeilPackage \$PackageRoot \$SignerThumbprint[^]*?Copy-VeilPackageFiles \$PackageRoot \$binRoot[^]*?Read-VeilPackage \$binRoot \$SignerThumbprint[^]*?New-Service/);
    assert.match(maintenance, /broker-service\.ps1'\) -PackageRoot \$stage -BrokerSource/);
    assert.doesNotMatch(maintenance, /\[IO\.File\]::Copy\(\(Join-Path \$stage \$name\)/);
    assert.match(source, /function New-RestrictedDirectory[^]*?New-VeilRestrictedDirectory \$Path/);
});

test('failed service registration rechecks SCM before deleting owned files', async () => {
    const maintenance = await readFile('scripts/package-maintenance.ps1', 'utf8');
    assert.match(source, /\$observed = Get-CimInstance Win32_Service[^]*?if \(\$installed -or \$observed\)[^]*?Set-Service -Name \$serviceName -StartupType Disabled[^]*?throw \$installError[^]*?if \(\$binCreated\) \{ Remove-VeilTree/);
    assert.match(maintenance, /\$installError=\$_[^]*?if \(-not \(Test-Path -LiteralPath \$bin\) -and -not \(Test-Path -LiteralPath \$data\) -and\s*-not \(Get-CimInstance Win32_Service[^]*?Remove-RestrictedTree \$stage/);
});
