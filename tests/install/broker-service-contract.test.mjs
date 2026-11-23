// Static policy checks, not a substitute for executing installation in an isolated Windows VM.
// No administrator privileges or service/volume mutations are performed by this test.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile('scripts/broker-service.ps1', 'utf8');
const client = await readFile('apps/desktop/BrokerClient.cs', 'utf8');
const service = await readFile('apps/broker/Veil.VolumeBroker/BrokerService.cs', 'utf8');

test('production service starts automatically after a reboot', () => {
    assert.match(source, /New-Service\b[^\r\n]*-StartupType\s+Automatic\b/);
    assert.doesNotMatch(source, /New-Service\b[^\r\n]*-StartupType\s+Manual\b/);
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
