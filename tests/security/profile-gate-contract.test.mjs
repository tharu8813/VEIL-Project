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

test('ambiguous backend unlock retains a crash watcher; relock confirms volume state', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    assert.match(broker, /try \{ authenticated = backend\.Unlock\(request\.SecretSpan\); \}\s*catch\s*\{[\s\S]*?Watch\(client\.ProcessId, ownerHandle\);\s*throw;/);
    assert.match(broker, /var ownership = new object\(\);\s*unlockOwner = ownership;/);
    assert.match(broker, /if \(!ReferenceEquals\(unlockOwner, ownership\)\) return;/);
    const watch = broker.slice(broker.indexOf('private void Watch('));
    assert.match(watch, /Unexpected volume during crash relock/);
    assert.match(watch, /if \(result == LockResult\.Locked\)\s*\{\s*status = backend\.Status\(\);[\s\S]*?status\.Unlocked[\s\S]*?status\.VolumeGuid/);
});

test('profile descendants are checked before profile lock and engine setup', () => {
    const check = gate.slice(gate.indexOf('internal static void RejectRedirectedChildren('), gate.indexOf('private static string FinalPath('));
    assert.match(check, /parent\.Attributes\.HasFlag\(FileAttributes\.ReparsePoint\)/);
    assert.match(check, /foreach \(var entry in parent\.EnumerateFileSystemInfos\(\)\)[\s\S]*?entry\.Attributes\.HasFlag\(FileAttributes\.ReparsePoint\)[\s\S]*?throw new ProfileLocationException[\s\S]*?pending\.Push\(child\)/);
    assert.match(startup, /ProfileGate\.Prepare\(root, volume, requireProtected\);\s*ProfileGate\.RejectRedirectedChildren\(root\);\s*using var profileLock/);
    assert.match(startup, /ProfileGate\.Prepare\(expected, volume, requireProtected\);\s*ProfileGate\.RejectRedirectedChildren\(expected\);[\s\S]*?CoreWebView2Environment\.CreateAsync/);
});
test('scratch cleanup cannot bypass protected session disposal', () => {
    assert.match(startup, /try\s*\{\s*if \(scratch is not null\)\s*\{\s*try \{ ProfileScratch\.Clear\(scratch\); \}\s*catch \(Exception error\) when \(error is IOException or UnauthorizedAccessException\) \{ \}\s*\}\s*\}\s*finally\s*\{\s*try \{ protectedSession\?\.Dispose\(\); \}\s*finally\s*\{\s*try \{ notifications\?\.Dispose\(\); \}\s*finally \{ activation\?\.Dispose\(\); \}\s*\}/);
});

test('protected launch lease covers the entire session, including cleanup', async () => {
    const activation = await readFile('apps/desktop/BrowserActivation.cs', 'utf8');
    assert.match(activation, /Utf8\.GetBytes\(sid \+ ":" \+ process\.SessionId\)/);
    assert.match(activation, /string userKey = Convert\.ToHexString\(SHA256\.HashData\(Utf8\.GetBytes\(sid\)\)\);/);
    assert.match(activation, /new Mutex\(false, @"Global\\VEIL\.ProtectedLaunch\." \+ userKey\)/);
    assert.match(activation, /mutex\.WaitOne\(0\)/);
    const stop = activation.slice(activation.indexOf('internal void StopListening()'), activation.indexOf('public void Dispose()'));
    assert.match(stop, /stop\.Cancel\(\)/);
    assert.doesNotMatch(stop, /ReleaseMutex|mutex\.Dispose/);
    assert.match(startup, /activation = !smoke && File\.Exists\(BrokerClientConfig\.ProductPath\) \? new BrowserActivation\(\) : null;[\s\S]*?if \(activation is \{ Primary: false \}\)[\s\S]*?return 1;[\s\S]*?ProtectedProfileSession\.TryOpen/);
    assert.doesNotMatch(startup, /using var activation/);
    const cleanup = startup.slice(startup.indexOf('activation?.StopListening();'));
    assert.match(cleanup, /activation\?\.StopListening\(\);[\s\S]*?ProfileScratch\.Clear\(scratch\)[\s\S]*?protectedSession\?\.Dispose\(\)[\s\S]*?finally \{ activation\?\.Dispose\(\); \}/);
});

test('runtime protection attestation requires explicitly disabled automatic unlock', async () => {
    const backend = await readFile('apps/broker/Veil.VolumeBroker/Backends.cs', 'utf8');
    assert.match(backend, /\$autoUnlockDisabled = \$false\s*try \{[\s\S]*?Invoke-CimMethod -MethodName IsAutoUnlockEnabled[\s\S]*?\$automatic\.ReturnValue -eq 0[\s\S]*?\$automatic\.IsAutoUnlockEnabled -is \[bool\] -and -not \$automatic\.IsAutoUnlockEnabled[\s\S]*?catch \{ \$autoUnlockDisabled = \$false \}/);
    assert.match(backend, /autoUnlockDisabled = \$autoUnlockDisabled/);
    assert.match(backend, /root\.TryGetProperty\("autoUnlockDisabled", out var automatic\) &&\s*automatic\.ValueKind == JsonValueKind\.True/);
    assert.match(backend, /Number\("protection"\) == 1 && autoUnlockDisabled, Number\("lock"\) == 0/);
});


test('future CEF protected profile preflight is read-only and refuses unverified volume', async () => {
    const layout = await readFile('apps/desktop/Engine/CefProfileLayout.cs', 'utf8');
    const preflight = await readFile('apps/desktop/Engine/CefProtectedProfilePreflight.cs', 'utf8');
    const native = await readFile('apps/cef-smoke/wpf.cc', 'utf8');
    const session = await readFile('apps/desktop/BrowserSession.cs', 'utf8');
    assert.match(layout, /!Path\.IsPathFullyQualified\(productRoot\)/);
    assert.match(layout, /EngineRoot = Path\.Combine\(ProductRoot, "cef"\);/);
    for (const [property, dir] of [['Cache','cache'], ['Scratch','temp'], ['Logs','logs']])
        assert.match(layout, new RegExp(property + ' = Path\\.Combine\\(EngineRoot, "' + dir + '"\\);'));
    assert.match(preflight, /ProfileGate\.RejectElevated\(\);[\s\S]*?ProfileGate\.Verify\(layout\.ProductRoot, broker, requireProtected: true\)/);
    assert.match(preflight, /FileAttributes\.Directory[\s\S]*?FileAttributes\.ReparsePoint[\s\S]*?ProfileGate\.Verify\(path, broker, requireProtected: true\);\s*RequireSameVolume\(initial, location\);/);
    assert.match(preflight, /ProfileGate\.RejectRedirectedChildren\(layout\.EngineRoot\)/);
    assert.match(preflight, /RequireSameVolume\(initial, ProfileGate\.Verify\(layout\.ProductRoot, broker, requireProtected: true\)\);/);
    assert.match(preflight, /!expected\.Protected \|\| !actual\.Protected \|\|[\s\S]*?expected\.VolumeGuid, actual\.VolumeGuid/);
    assert.match(preflight, /try \{ return File\.GetAttributes\(path\); \}\s*catch \(FileNotFoundException\) \{ return null; \}\s*catch \(DirectoryNotFoundException\) \{ return null; \}/);
    assert.doesNotMatch(preflight, /Directory\.CreateDirectory|ProfileGate\.Prepare|requireProtected:\s*false|UnmanagedDevelopmentVolume|SetEnvironmentVariable|Process\.Start/);
    assert.match(native, /if \(!CefInitialize\([\s\S]*?const int result = RunManagedWindow\(\);/);
    assert.match(session, /EngineHost \{ get; \} = new WebView2BrowserEngine\(\);/);
    assert.doesNotMatch(startup, /CefProtectedProfilePreflight|CefDevelopmentEngine/);
});


test('CEF protected preparation verifies every fixed child before and after creation without fallback or launch', async () => {
    const layout = await readFile('apps/desktop/Engine/CefProfileLayout.cs', 'utf8');
    const prepare = await readFile('apps/desktop/Engine/CefProtectedProfilePreparation.cs', 'utf8');
    assert.match(layout, /PreparationSteps =>\s*\[\(ProductRoot, EngineRoot\), \(EngineRoot, Cache\), \(EngineRoot, Scratch\), \(EngineRoot, Logs\)\];/);
    assert.match(prepare, /ProfileGate\.RejectElevated\(\);\s*RequireAuthorization\(stillAuthorized\);[\s\S]*?var initial = ProfileGate\.Verify\(layout\.ProductRoot, broker, requireProtected: true\);/);
    assert.match(prepare, /CefProtectedProfilePreflight\.Inspect\(layout\.ProductRoot, broker\);\s*foreach \(var \(parent, child\) in layout\.PreparationSteps\)/);
    assert.match(prepare, /foreach \(var \(parent, child\) in layout\.PreparationSteps\)\s*\{\s*RequireAuthorization\(stillAuthorized\);\s*CefProtectedProfilePreflight\.RequireSameVolume\(initial,\s*ProfileGate\.Verify\(layout\.ProductRoot, broker, requireProtected: true\)\);\s*CefProtectedProfilePreflight\.RequireSameVolume\(initial,\s*ProfileGate\.Verify\(parent, broker, requireProtected: true\)\);[\s\S]*?var prepared = ProfileGate\.Prepare\(child, broker, requireProtected: true\);\s*CefProtectedProfilePreflight\.RequireSameVolume\(initial, prepared\);\s*ProfileGate\.RejectRedirectedChildren\(child\);\s*RequireAuthorization\(stillAuthorized\);/);
    assert.match(prepare, /CefProtectedProfilePreflight\.Inspect\(layout\.ProductRoot, broker\);\s*foreach \(string path in layout\.Directories\)\s*CefProtectedProfilePreflight\.RequireSameVolume\(initial,\s*ProfileGate\.Verify\(path, broker, requireProtected: true\)\);\s*RequireAuthorization\(stillAuthorized\);/);
    assert.match(prepare, /if \(!stillAuthorized\(\)\)\s*throw new ProfileLocationException/);
    assert.equal((prepare.match(/ProfileGate\.Prepare\(/g) ?? []).length, 1);
    assert.doesNotMatch(prepare, /requireProtected:\s*false|UnmanagedDevelopmentVolume|Directory\.CreateDirectory|Directory\.Delete|File\.Delete|SetEnvironmentVariable|Process\.Start|CefInitialize\(/);
    assert.doesNotMatch(startup, /CefProtectedProfilePreparation/);
});

test('CEF preparation lease is bound to a fresh authenticated registered volume and owns its exclusive lock', async () => {
    const session = await readFile('apps/desktop/ProtectedProfileSession.cs', 'utf8');
    const broker = await readFile('apps/desktop/BrokerClient.cs', 'utf8');
    assert.match(broker, /RegisteredProductProfilePath =>\s*!config\.Development && config\.RequireWindowsService/);
    assert.match(session, /if \(broker\.Unlock\(secret\)\)[\s\S]*?FullyEncrypted: true, ProtectionOn: true, Unlocked: true, ProtectorPresent: true[\s\S]*?authenticatedVolumeGuid = state\.VolumeGuid;/);
    assert.match(session, /Environment\.CurrentManagedThreadId != ownerThread \|\|\s*disposed \|\| notifications\.LockRequested \|\| authenticatedVolumeGuid is null/);
    assert.match(session, /ProfileGate\.Verify\(root, broker, requireProtected: true\);[\s\S]*?location\.VolumeGuid, authenticatedVolumeGuid, StringComparison\.OrdinalIgnoreCase/);
    assert.match(session, /session\.RequireCefPreparationAuthority\(root\);\s*ProfileGate\.RejectRedirectedChildren\(root\);\s*session\.RequireCefPreparationAuthority\(root\);\s*var handle = new FileStream\(Path\.Combine\(root, "profile\.lock"\), FileMode\.OpenOrCreate,\s*FileAccess\.ReadWrite, FileShare\.None\);\s*try\s*\{\s*session\.RequireCefPreparationAuthority\(root\);/);
    assert.match(session, /private CefPreparationLease\(/);
    assert.match(session, /released \|\|\s*handle\.SafeFileHandle\.IsClosed \|\| handle\.SafeFileHandle\.IsInvalid\)\s*return false;\s*session\.RequireCefPreparationAuthority\(ProductRoot\);/);
    assert.match(session, /released = true;\s*handle\.Dispose\(\);/);
    assert.match(session, /disposed = true;\s*authenticatedVolumeGuid = null;/);
    assert.doesNotMatch(startup, /AcquireCefPreparationLease|CefPreparationLease/);
});


test('integrated CEF child environment is prepared only through the live owning lease', async () => {
    const session = await readFile('apps/desktop/ProtectedProfileSession.cs', 'utf8');
    const plan = session.slice(session.indexOf('internal IReadOnlyDictionary<string, string?> PrepareChildEnvironment()'),
        session.indexOf('public void Dispose()', session.indexOf('internal IReadOnlyDictionary<string, string?> PrepareChildEnvironment()')));
    assert.match(plan, /PrepareChildEnvironment\(\)\s*\{\s*RequireCurrentPreparation\(\);\s*var layout = CefProtectedProfilePreparation\.Prepare\(ProductRoot,\s*session\.broker, StillAuthorized\);\s*RequireCurrentPreparation\(\);/);
    assert.match(plan, /foreach \(System\.Collections\.DictionaryEntry entry in Environment\.GetEnvironmentVariables\(\)\)[\s\S]*?var plan = CefChildEnvironmentPlan\.Build\(layout, inherited\);\s*RequireCurrentPreparation\(\);\s*return plan;/);
    assert.match(plan, /if \(!StillAuthorized\(\)\)\s*throw new ProfileLocationException/);
    assert.doesNotMatch(plan, /Process\.Start|SetEnvironmentVariable|CefInitialize|UnmanagedDevelopmentVolume|Directory\.Delete|File\.Delete/);
    assert.doesNotMatch(startup, /PrepareChildEnvironment|AcquireCefPreparationLease/);
});

test('broker authenticated-owner snapshot is live, read-only and granted only after verified unlock', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    const client = await readFile('apps/desktop/BrokerClient.cs', 'utf8');
    const owner = broker.slice(broker.indexOf('case BrokerCommand.AuthenticatedOwner:'), broker.indexOf('case BrokerCommand.Attach'));
    assert.match(owner, /!Registered\(status\) \|\| !status\.Unlocked \|\| !status\.FullyEncrypted/);
    assert.match(owner, /!status\.ProtectionOn \|\| !status\.ProtectorPresent/);
    assert.match(owner, /unlockOwner is null \|\| authenticatedOwner != client/);
    assert.match(owner, /authenticatedOwnerHandle is not \{ \} liveHandle \|\| !Native\.IsRunning\(liveHandle\)/);
    assert.doesNotMatch(owner, /backend\.(Attach|Unlock|Lock|Detach)\(/);
    assert.match(broker, /if \(!Registered\(status\) \|\| !status\.Unlocked \|\| !status\.FullyEncrypted \|\|[\s\S]*?authenticatedOwner = client;/);
    assert.match(broker, /private void Watch\(int pid, SafeProcessHandle handle\)\s*\{\s*ClearAuthenticatedOwner\(\);/);
    assert.match(broker, /if \(!ReferenceEquals\(unlockOwner, ownership\)\) return;\s*ClearAuthenticatedOwner\(\);/);
    assert.match(native, /WaitForSingleObject\(handle, 0\) == 0x102/);
    assert.match(protocol, /\(parsed == BrokerCommand\.Unlock\) != \(secret is not null\)/);
    assert.match(client, /QueryAuthenticatedOwner\(\) => QueryVolume\("AuthenticatedOwner"\)/);
});


test('CEF preparation rechecks the live broker owner around path validation and never falls back to Status alone', async () => {
    const session = await readFile('apps/desktop/ProtectedProfileSession.cs', 'utf8');
    const authority = session.slice(session.indexOf('private void RequireCefPreparationAuthority('),
        session.indexOf('// Owns the exclusive file handle.'));
    assert.match(authority, /RequireBrokerOwner\(\);\s*var location = ProfileGate\.Verify\(root, broker, requireProtected: true\);\s*if \(!string\.Equals\(location\.VolumeGuid, authenticatedVolumeGuid, StringComparison\.OrdinalIgnoreCase\)\)\s*throw new ProfileLocationException[\s\S]*?RequireBrokerOwner\(\);\s*if \(disposed \|\| notifications\.LockRequested\)/);
    assert.equal((authority.match(/RequireBrokerOwner\(\);/g) ?? []).length, 2);
    assert.match(authority, /var owner = broker\.QueryAuthenticatedOwner\(\);\s*if \(owner is not \{ FullyEncrypted: true, ProtectionOn: true, Unlocked: true, ProtectorPresent: true \} \|\|\s*!string\.Equals\(owner\.VolumeGuid, authenticatedVolumeGuid, StringComparison\.OrdinalIgnoreCase\)\)\s*throw new ProfileLocationException/);
    assert.doesNotMatch(authority, /catch\s*\(|QueryRegisteredVolume\(|requireProtected:\s*false/);
    assert.doesNotMatch(startup, /QueryAuthenticatedOwner|PrepareChildEnvironment/);
});


test('broker process generation binds the crash watcher and child admission stays disconnected', async () => {
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    assert.match(native, /GetProcessTimes\(process, out long created, out _, out _, out _\)[\s\S]*?new ClientIdentity\([\s\S]*?, created\)/);
    assert.match(native, /OpenForWait\(ClientIdentity client\)[\s\S]*?created == client\.CreatedFileTime && IsRunning\(process\)/);
    assert.match(broker, /var ownerHandle = Native\.OpenForWait\(client\);\s*if \(ownerHandle\.IsInvalid\)/);
    assert.doesNotMatch(broker, /CefChildAdmission/);
    assert.doesNotMatch(protocol, /CefChildAdmission|RedeemChild|GrantChild/);
});
