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
    assert.match(owner, /authenticatedOwnerHandle is not \{ \} liveHandle \|\| !Native\.SameLiveProcess\(liveHandle, pipeClient\)/);
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


test('broker process generation binds the crash watcher without exposing general child commands', async () => {
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    assert.match(native, /GetProcessTimes\(process, out long created, out _, out _, out _\)[\s\S]*?new ClientIdentity\([\s\S]*?, created\)/);
    assert.match(native, /OpenForWait\(ClientIdentity client, SafeProcessHandle observed\)[\s\S]*?SameLiveProcess\(observed, process\) && ObserveProcess\(process\) == client/);
    assert.match(broker, /var ownerHandle = Native\.OpenForWait\(client, pipeClient\);\s*if \(ownerHandle\.IsInvalid\)/);
    assert.doesNotMatch(protocol, /CefChildAdmission|GrantChild/);
});


test('future CEF executable checks use a retained file handle and do not extend broker permissions', async () => {
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefPinnedImage.cs', 'utf8');
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const admin = await readFile('apps/shared/AdminOnlyFile.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    assert.match(pin, /!Native\.MatchesLiveProcessImage\(process, child\)/);
    assert.match(pin, /AdminOnlyFile\.RequireProtectedExecutable\(registeredPath\);\s*var image = new FileStream\(registeredPath, FileMode\.Open, FileAccess\.Read, FileShare\.Read\)/);
    assert.match(pin, /Native\.RequireCanonicalSingleLinkImage\(image\.SafeFileHandle, registeredPath\);\s*if \(!digest\.Matches\(image\) \|\| !Native\.IsSigned\(registeredPath\)\)/);
    assert.match(pin, /if \(!Native\.MatchesLiveProcessImage\(process, child\)\)[\s\S]*?return image;\s*\}\s*catch\s*\{\s*image\.Dispose\(\);\s*throw;/);
    assert.match(native, /GetProcessId\(process\) != \(uint\)expected\.ProcessId[\s\S]*?created != expected\.CreatedFileTime/);
    assert.match(native, /GetFinalPathNameByHandleW\(file, resolved,[\s\S]*?info\.Links != 1/);
    assert.match(admin, /Require\(file\.GetAccessControl\(\), "executable"\);\s*Require\(file\.Directory!\.GetAccessControl\(\), "executable directory"\);/);
    // The disconnected broker-internal diagnostic registration now performs
    // the same pin check. Ordinary IPC and admission paths must not gain it.
    const start = broker.indexOf('private CefDiagnosticActivationOutcome TryRegisterDiagnosticSession(');
    const end = broker.indexOf('private void ClearAuthenticatedOwner()', start);
    assert.ok(start >= 0 && end > start);
    const registration = broker.slice(start, end);
    assert.match(registration, /config\.Development \|\| config\.CefChildImage is not \{ \} image \|\|\s*config\.CefChildSha256 is not \{ \} digest/);
    assert.match(registration, /using var verified = CefPinnedImage\.OpenVerified\(root, rootHandle, image, new CefImageDigest\(digest\)\);\s*registration\.RequireCurrent\(\);/);
    assert.doesNotMatch(broker.slice(0, start) + broker.slice(end), /CefPinnedImage|CefImageDigest/);
    assert.equal((broker.match(/TryRegisterDiagnosticSession\s*\(/g) ?? []).length, 1);
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    assert.doesNotMatch(protocol, /TryRegisterDiagnosticSession|CefDiagnosticSession|CefPinnedImage|CefImageDigest/);
});


test('CEF launch nomination retains a duplicated live handle without treating parent PID as authorization', async () => {
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    const binding = native.slice(native.indexOf('internal static (SafeProcessHandle Handle, ClientIdentity Identity) DuplicateNominatedChild'),
        native.indexOf('// Match the process generation'));
    assert.match(binding, /ObserveProcess\(originalOwner\) != owner/);
    assert.match(binding, /ObserveProcess\(source\) != owner \|\|\s*!SameLiveProcess\(originalOwner, source\) \|\|\s*!DuplicateHandle\(source, nominatedHandle,/);
    assert.match(binding, /QueryLimitedInformation \| Synchronize, false, 0/);
    assert.match(binding, /!HasReportedParent\(identity\.ProcessId, owner\.ProcessId\)/);
    assert.match(binding, /ObserveProcess\(originalOwner\) != owner \|\| ObserveProcess\(child\) != identity/);
    assert.match(binding, /return \(child, identity\);\s*\}\s*catch\s*\{\s*child\.Dispose\(\);/);
    assert.doesNotMatch(broker, /DuplicateNominatedChild/); // Only the scoped preflight helper calls it.
    assert.doesNotMatch(protocol, /NominatedChild|GrantChild|RedeemChild/);
});


test('CEF preflight command is owner-only, separately pinned and never grants child pipe rights', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const config = await readFile('apps/broker/Veil.VolumeBroker/BrokerConfig.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    const command = broker.slice(broker.indexOf('case BrokerCommand.CheckCefChild:'), broker.indexOf('case BrokerCommand.StageCefChild:'));
    assert.match(command, /authenticatedOwner != client[\s\S]*?code = "NotAuthenticatedOwner";[\s\S]*?config\.Development[\s\S]*?code = "CefUnavailable";[\s\S]*?CefChildPreflight\.Check/);
    assert.match(command, /status = backend\.Status\(\);[\s\S]*?!Native\.IsRunning\(preflightOwner\)/);
    assert.doesNotMatch(command, /backend\.(Attach|Unlock|Lock|Detach)\(|CefChildAdmission|GrantChild|RedeemChild/);
    const rejectStart = broker.indexOf('private string? Reject(');
    const redeemStart = broker.indexOf('private byte[] RedeemCefChild(');
    assert.ok(rejectStart >= 0 && redeemStart > rejectStart, 'ordinary client rejection boundary missing');
    const reject = broker.slice(rejectStart, redeemStart);
    assert.match(reject, /config\.ClientImages\.Any\(/);
    assert.match(reject, /Native\.IsSigned\(client\.ImagePath\)/);
    assert.match(reject, /Native\.MatchesSha256\(client\.ImagePath, config\.ClientSha256!/);
    assert.doesNotMatch(reject, /CefChildImage|CefChildSha256|CheckCefChild/);
    assert.match(config, /if \(CefChildImage is not null \|\| CefChildSha256 is not null\)[\s\S]*?if \(development \|\|/);
    assert.match(protocol, /\(parsed is BrokerCommand\.CheckCefChild or BrokerCommand\.StageCefChild or BrokerCommand\.ConfirmCefPreflight\) != childHandle\.HasValue/);
    assert.match(preflight, /using var child = binding\.Handle;\s*using var image = CefPinnedImage\.OpenVerified/);
    assert.match(preflight, /Native\.ObserveProcess\(ownerHandle\) != owner \|\|\s*Native\.ObserveProcess\(child\) != binding\.Identity/);
});


test('CEF staged child is bounded to one pending slot and retired on lock and timeout', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    const pending = await readFile('apps/broker/Veil.VolumeBroker/CefPendingChild.cs', 'utf8');
    const stage = broker.slice(broker.indexOf('case BrokerCommand.StageCefChild:'), broker.indexOf('case BrokerCommand.Attach'));
    assert.match(stage, /authenticatedOwner != client[\s\S]*?config\.Development[\s\S]*?if \(pendingCefChild is not null \|\| completedPreflight is not null\)[\s\S]*?CefChildPreflight\.Stage/);
    assert.match(stage, /pendingCefChild = candidate;\s*_ = MonitorPendingCefChildAsync\(candidate, generation\);/);
    assert.match(stage, /if \(!ReferenceEquals\(pendingCefChild, candidate\)\)\s*\{\s*try \{ StopPendingChild\(candidate\); \}\s*finally \{ candidate\.Dispose\(\); \}/);
    assert.match(broker, /request\.Command is BrokerCommand\.Lock or BrokerCommand\.Detach\)\s*ClearAuthenticatedOwner\(\)/);
    assert.match(broker, /private void ClearAuthenticatedOwner\(\)\s*\{[\s\S]*?authenticatedOwner = null;\s*authenticatedOwnerHandle = null;\s*CefOwnerRevocationCleanup.Run\(diagnosticSession\.Retire, RevokePendingCefChild,\s*RetryDiagnosticSession,/);
    const clearOwner = broker.slice(broker.indexOf('private void ClearAuthenticatedOwner()'), broker.indexOf('private void RevokePendingCefChild()'));
    assert.doesNotMatch(clearOwner, /unlockOwner\s*=/);
    const secure = broker.slice(broker.indexOf('internal async Task SecureVolumeAsync()'), broker.indexOf('private async Task ServeAsync'));
    assert.match(secure, /ClearAuthenticatedOwner\(\);[\s\S]*?var status = backend\.Status\(\)/);
    assert.match(broker, /if \(ReferenceEquals\(pendingCefChild, expected\)\) RevokePendingCefChild\(\)/);
    assert.match(preflight, /var pending = new CefPendingChild\(new CefChildAdmission[\s\S]*?return pending;/);
    assert.match(pending, /admission\.Revoke\(\);\s*try \{ runtime\?\.Dispose\(\); \}\s*finally\s*\{\s*try \{ image\?\.Dispose\(\); \}\s*finally\s*\{\s*try \{ job\?\.Dispose\(\); \}\s*finally \{ process\?\.Dispose\(\); \}/);
    assert.doesNotMatch(stage, /TryRedeem\(/);
});


test('CEF redemption has a dedicated route and never reaches ordinary broker commands', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const pending = await readFile('apps/broker/Veil.VolumeBroker/CefPendingChild.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    const redeem = broker.slice(broker.indexOf('private byte[] RedeemCefChild('), broker.indexOf('private async Task<byte[]> Execute'));
    assert.match(broker, /bool redemptionCandidate = reason == "image" && !config\.Development/);
    assert.match(broker, /if \(redemptionCandidate\)\s*response = request\.Command == BrokerCommand\.RedeemCefChild\s*\? RedeemCefChild\(request, client, pipeClient\) : Protocol\.Error\("ClientRejected"\);\s*else if \(request\.Command == BrokerCommand\.RedeemCefChild\)\s*response = Protocol\.Error\("ClientRejected"\);\s*else\s*response = await Execute/);
    assert.match(redeem, /!pending\.MatchesChild\(client\)[\s\S]*?pendingCefChild = null;\s*using \(pending\)/);
    assert.match(redeem, /!CandidateAuthorityCurrent\(generation\) \|\|\s*!CefChildPreflight\.VerifyRedemption[\s\S]*?!CandidateAuthorityCurrent\(generation\)[\s\S]*?!pending\.TryRedeem/);
    const refresh = broker.slice(broker.indexOf('private bool RefreshAuthenticatedOwner('), broker.indexOf('private bool CandidateAuthorityCurrent('));
    const current = broker.slice(broker.indexOf('private bool CandidateAuthorityCurrent('), broker.indexOf('private async Task MonitorCompletedPreflightAsync('));
    assert.match(refresh, /state\.Attached && state\.Unlocked && state\.FullyEncrypted &&\s*state\.ProtectionOn && state\.ProtectorPresent &&\s*string\.Equals\(state\.VolumeGuid, config\.VolumeGuid, StringComparison\.OrdinalIgnoreCase\)/);
    assert.match(refresh, /Native\.IsRunning\(owner\) &&\s*Native\.ObserveProcess\(owner\) == expected/);
    assert.match(refresh, /authority\.Observe\(authenticatedOwner, unlockOwner, verified, protectedVolume\)[\s\S]*?ClearAuthenticatedOwner\(\);\s*return false;/);
    assert.match(current, /!ReferenceEquals\(unlockOwner, generation\) \|\| authenticatedGeneration is null[\s\S]*?RefreshAuthenticatedOwner\(backend\.Status\(\)\)/);
    assert.match(current, /catch \(Exception error\)[\s\S]*?ClearAuthenticatedOwner\(\);\s*throw;/);
    assert.doesNotMatch(redeem, /backend\.(Attach|Unlock|Lock|Detach)\(|Protocol\.Ok\(/);
    assert.match(preflight, /using var rechecked = CefPinnedImage\.OpenVerified\(child, process,/);
    assert.match(pending, /!disposed && admission\.TryRedeem/);
    assert.match(protocol, /new \{ ok = true, cefChildVerified = true \}/);
    assert.doesNotMatch(startup, /RedeemCefChild|cefChildVerified/);
});


test('native CEF receipt helper does not authorize smoke startup', async () => {
    const header = await readFile('apps/cef-smoke/broker_verification.h', 'utf8');
    const impl = await readFile('apps/cef-smoke/broker_verification.cc', 'utf8');
    const smoke = await readFile('apps/cef-smoke/smoke.cc', 'utf8');
    const wpf = await readFile('apps/cef-smoke/wpf.cc', 'utf8');
    assert.match(header, /return reply == kVerifiedReply/);
    assert.match(impl, /GetNamedPipeServerProcessId[\s\S]*?ServicePid\(\) == expected/);
    assert.match(impl, /CancelIoEx\(pipe, &operation\)/);
    assert.match(impl, /return is_reply\(reply\) && SameServer/);
    assert.doesNotMatch(impl, /GetEnvironmentVariable|CefInitialize/);
    assert.doesNotMatch(smoke + wpf, /RequestChildVerification|broker_verification\.h/);
});


test('native development hosts refuse product configuration before any CEF execution', async () => {
    const paths = await readFile('apps/cef-smoke/protected_paths.h', 'utf8');
    for (const file of ['smoke.cc', 'wpf.cc']) {
        const host = await readFile('apps/cef-smoke/' + file, 'utf8');
        assert.match(host, /int RunWinMain\([\s\S]*?if \(veil_cef::HasProductEnvironment\(\)\) return 28;\s*if \(!sandbox_info\) return 10;\s*CefMainArgs args\(instance\);\s*const int subprocess = CefExecuteProcess/);
        assert.doesNotMatch(host, /RequestChildVerification/);
    }
    assert.match(paths, /for \(const auto& path : \{supplied\.root, supplied\.cache, supplied\.scratch, supplied\.log\}\)\s*if \(!DriveAbsolute\(path\)\)/);
    assert.match(paths, /a\.find\(L'\\0'\) != std::wstring::npos \|\| b\.find\(L'\\0'\) != std::wstring::npos/);
});


test('native bootstrap preflight verifies environment and paths before a single receipt and rechecks afterward', async () => {
    const header = await readFile('apps/cef-smoke/protected_bootstrap.h', 'utf8');
    const impl = await readFile('apps/cef-smoke/protected_bootstrap.cc', 'utf8');
    assert.match(header, /!read_environment\(before\) \|\| !CheckEnvironment\(before\)[\s\S]*?!VerifyPaths\(registration\.product_root, registration\.volume_guid, before\.paths\)[\s\S]*?!receipt\(registration\.volume_id\)[\s\S]*?!read_environment\(after\) \|\| !CheckEnvironment\(after\)[\s\S]*?!VerifyPaths\(registration\.product_root, registration\.volume_guid, after\.paths\)/);
    assert.equal((header.match(/receipt\(registration\.volume_id\)/g) ?? []).length, 1);
    assert.match(impl, /CheckBootstrap\(registration, RequestChildVerification, ReadBootstrapEnvironment\)/);
    assert.match(impl, /!Absent\(L"CHROME_LOG_FILE"\)/);
    for (const file of ['smoke.cc', 'wpf.cc']) {
        const host = await readFile('apps/cef-smoke/' + file, 'utf8');
        assert.doesNotMatch(host, /VerifyRegisteredBootstrap|CheckBootstrap|protected_bootstrap\.h/);
    }
});


test('native product registration is fixed machine-only read-only and not connected to startup', async () => {
    const registration = await readFile('apps/cef-smoke/bootstrap_registration.cc', 'utf8');
    assert.match(registration, /REG_OPTION_OPEN_LINK,\s*KEY_READ \| KEY_WOW64_64KEY/);
    assert.match(registration, /software\.Open\(HKEY_LOCAL_MACHINE, L"SOFTWARE"\)[\s\S]*?veil\.Open\(software\.get\(\), L"VEIL"\)[\s\S]*?leaf\.Open\(veil\.get\(\), L"CefBootstrap"\)/);
    assert.match(registration, /!TrustedSid\(owner\)[\s\S]*?SE_DACL_PROTECTED[\s\S]*?!present \|\| !acl/);
    assert.match(registration, /ace->Mask & ~static_cast<ACCESS_MASK>\(KEY_READ\)/);
    assert.match(registration, /CompareFileTime\(&before, &after\) != 0/);
    assert.doesNotMatch(registration, /HKEY_CURRENT_USER|RegCreateKey|RegSetValue|GetEnvironmentVariable/);
    for (const file of ['smoke.cc', 'wpf.cc']) {
        const host = await readFile('apps/cef-smoke/' + file, 'utf8');
        assert.doesNotMatch(host, /LoadRegisteredBootstrap|bootstrap_registration\.h/);
    }
});


test('product-named CEF candidate is isolated and does not initialize CEF', async () => {
    const cmake = await readFile('apps/cef-smoke/CMakeLists.txt', 'utf8');
    const product = await readFile('apps/cef-smoke/product_candidate.cc', 'utf8');
    const packageBuild = await readFile('scripts/build-package.ps1', 'utf8');
    const packagePolicy = await readFile('scripts/package-common.ps1', 'utf8');
    assert.match(cmake, /add_library\(veil-cef-product SHARED EXCLUDE_FROM_ALL product_candidate\.cc\s+protected_bootstrap\.cc bootstrap_registration\.cc broker_verification\.cc product_module_policy\.cc\s+protected_initialization_settings\.cc\)/);
    assert.match(cmake, /RUNTIME_OUTPUT_DIRECTORY "\$\{CMAKE_BINARY_DIR\}\/product-candidate"/);
    assert.match(product, /RunProductPreflight\(allowed,\s*veil_cef::LoadRegisteredBootstrap, veil_cef::VerifyRegisteredBootstrap\)/);
    assert.match(product, /sandbox_info != nullptr[\s\S]*?command_line\[0\] == L'\\0'[\s\S]*?IsProcessInJob/);
    const preflight = await readFile('apps/cef-smoke/product_preflight.h', 'utf8');
    assert.match(preflight, /!invocation_allowed[\s\S]*?!load\(before\)[\s\S]*?verify\(before\) != BootstrapCheck::kPreflightVerified[\s\S]*?!load\(after\)[\s\S]*?before\.volume_id != after\.volume_id[\s\S]*?return ProductPreflightExit::kVerifiedButDisabled/);
    assert.equal((preflight.match(/verify\(before\)/g) ?? []).length, 1);
    assert.doesNotMatch(preflight, /CefInitialize\s*\(|CefExecuteProcess\s*\(/);
    assert.doesNotMatch(product, /CefInitialize\s*\(|CefExecuteProcess\s*\(|RequestChildVerification\s*\(/);
    assert.doesNotMatch(packageBuild, /veil-cef-product|BuildProductCandidate/);
    assert.match(packagePolicy, /\(Desktop\|Broker\)\//);
});


test('CEF containment primitive starts suspended and assigns a kill-on-close job before resume', async () => {
    const launch = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    const startup = await readFile('apps/desktop/Program.cs', 'utf8');
    assert.match(launch, /limits\.Basic\.LimitFlags = 0x00002000/);
    assert.match(launch, /const uint flags = 0x00000004 \| 0x00000400 \| 0x00080000/);
    assert.match(launch, /using var attributes = new JobAttributeList\(job, additionalJobs\)[\s\S]*?CreateProcessW\(image, command, IntPtr\.Zero, IntPtr\.Zero, false, flags[\s\S]*?IsProcessInJob\(process, job/);
    assert.match(launch, /UpdateProcThreadAttribute\(list, 0, new UIntPtr\(0x0002000D\)/);
    assert.doesNotMatch(launch, /AssignProcessToJobObject/);
    assert.match(launch, /!verified\(\) \|\| !IsProcessInJob\(process, job, out bool contained\) \|\| !contained \|\|\s*!HasImageLoadMitigations\(process\) \|\| ResumeThread\(thread\) != 1/);
    assert.doesNotMatch(startup, /CefContainedProcess/);
});


test('desktop stages a contained suspended child only under the existing owner lease', async () => {
    const session = await readFile('apps/desktop/ProtectedProfileSession.cs', 'utf8');
    const client = await readFile('apps/desktop/BrokerClient.cs', 'utf8');
    const startup = await readFile('apps/desktop/Program.cs', 'utf8');
    const stage = session.slice(session.indexOf('internal void StageSuspendedChild'), session.indexOf('private void RequireCurrentPreparation'));
    assert.match(stage, /RequireCurrentPreparation\(\);[\s\S]*?long nominated = child\.NominatedHandle;[\s\S]*?session\.broker\.StageCefChild\(nominated, session\.authenticatedVolumeGuid!\);[\s\S]*?RequireCurrentPreparation\(\);[\s\S]*?child\.NominatedHandle != nominated/);
    assert.match(stage, /catch\s*\{[\s\S]*?child\.Dispose\(\);[\s\S]*?throw;/);
    assert.doesNotMatch(stage, /ResumeAfterVerification|CreateSuspended/);
    assert.match(client, /CefStageWire\.WriteRequest\(writer, config\.VolumeId, handle, command\)/);
    assert.match(client, /if \(command is "StageCefChild" or "ConfirmCefPreflight"\) VerifyServer\(pipe\);/);
    assert.doesNotMatch(startup, /StageSuspendedChild|StageCefChild/);
});


test('broker retains pipe-observed process objects and compares actual handles before CEF staging and redemption', async () => {
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    assert.match(native, /CompareObjectHandles\(first, second\)/);
    assert.match(broker, /using var pipeClient = binding.Handle/);
    assert.match(broker, /if \(!Native.MatchesPipeClient\(pipe, pipeClient, client\)\)/);
    assert.match(broker, /!Native.SameLiveProcess\(stagingOwner, pipeClient\)/);
    assert.match(broker, /!Native.SameLiveProcess\(nominated, pipeClient\)/);
    assert.match(broker, /!Native.SameLiveProcess\(childHandle, pipeClient\)/);
    assert.match(broker, /if \(request.Command is BrokerCommand.Lock or BrokerCommand.Detach\)\s*ClearAuthenticatedOwner\(\);[\s\S]*?var status = backend.Status\(\)/);
});


test('disabled CEF coordinator binds profile and runtime leases before suspended preflight only', async () => {
    const coordinator = await readFile('apps/desktop/Engine/CefDisabledLaunchCoordinator.cs', 'utf8');
    const runner = await readFile('apps/desktop/Engine/CefDisabledPreflight.cs', 'utf8');
    const startup = await readFile('apps/desktop/Program.cs', 'utf8');
    assert.match(coordinator, /using var profile = session.AcquireCefPreparationLease\(\);[\s\S]*?using var runtime = profile.PrepareRegisteredRuntime\(\);[\s\S]*?using var child = CefContainedProcess.CreateSuspended\(runtime.ImagePath, minimal\);[\s\S]*?CefDisabledPreflight.Run\(child, \(\) => profile.StageSuspendedChild\(child\), Current,\s*\(\) => profile.ConfirmCompletedPreflight\(child\)\)/);
    assert.match(runner, /stage\(\);\s*elapsed.Start\(\);\s*child.ResumeAfterVerification\(Current\);[\s\S]*?child.ReadExitCode\(\);[\s\S]*?code != VerifiedButDisabled/);
    assert.match(runner, /finally\s*\{[\s\S]*?child.TerminateAndDrain\(5000\)/);
    assert.doesNotMatch(startup, /CefDisabledLaunchCoordinator|CefDisabledPreflight/);
});


test('disabled preflight requires broker confirmation of the same exited child', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const runner = await readFile('apps/desktop/Engine/CefDisabledPreflight.cs', 'utf8');
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    assert.match(broker, /completedPreflight = receipt;[\s\S]*?return Protocol.CefChildVerified/);
    assert.match(broker, /case BrokerCommand.ConfirmCefPreflight:[\s\S]*?completedPreflight = null;[\s\S]*?MatchesExitedNomination/);
    assert.match(native, /CompareObjectHandles\(child, retained\)[\s\S]*?GetExitCodeProcess\(child, out uint code\) && code == expectedExit/);
    assert.match(runner, /!child.WaitForNaturalJobExit\(250\) \|\| !Current\(\)[\s\S]*?confirm\(\);/);
});


test('disabled candidate requires creation-time image mitigations and restricts post-bootstrap module paths', async () => {
    const launch = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    const native = await readFile('apps/cef-smoke/product_module_policy.cc', 'utf8');
    const product = await readFile('apps/cef-smoke/product_candidate.cc', 'utf8');
    assert.match(launch, /const long imagePolicy = \(1L << 52\) \| \(1L << 56\) \| \(1L << 60\)/);
    assert.match(launch, /UpdateProcThreadAttribute\(list, 0, new UIntPtr\(0x00020007\)/);
    assert.match(launch, /GetProcessMitigationPolicy\(handle, 10, out uint flags, new UIntPtr\(4u\)\)/);
    assert.match(native, /SetDefaultDllDirectories\(LOAD_LIBRARY_SEARCH_APPLICATION_DIR \|\s*LOAD_LIBRARY_SEARCH_SYSTEM32\)/);
    assert.match(native, /CreateToolhelp32Snapshot\(TH32CS_SNAPMODULE \| TH32CS_SNAPMODULE32/);
    assert.match(product, /RestrictAndObserveLoadedModules\(\)[\s\S]*?RunProductPreflight/);
});


test('native disabled candidate checks image mapping type and backing filenames before and after receipt', async () => {
    const native = await readFile('apps/cef-smoke/product_module_policy.cc', 'utf8');
    const product = await readFile('apps/cef-smoke/product_candidate.cc', 'utf8');
    assert.match(native, /VirtualQuery\(module, &region, sizeof\(region\)\)[\s\S]*?region.Type != MEM_IMAGE/);
    assert.match(native, /GetMappedFileNameW\(GetCurrentProcess\(\), module, mapped, 32768\)/);
    assert.match(native, /GetFinalPathNameByHandleW\(file.get\(\), device, 32768,\s*FILE_NAME_NORMALIZED \| VOLUME_NAME_NT\)/);
    assert.match(native, /!Same\(std::wstring\(mapped, mapped_length\), std::wstring\(device, device_length\)\)/);
    assert.match(native, /ObserveImageBacking\(entry.hModule, module_path, local\)/);
    assert.match(product, /RunProductPreflight\(allowed,[\s\S]*?result == veil_cef::ProductPreflightExit::kVerifiedButDisabled &&\s*!veil_cef::RestrictAndObserveLoadedModules\(\)/);
});


test('broker independently checks the live CEF module snapshot against retained registered runtime files', async () => {
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const lease = await readFile('apps/desktop/Engine/CefRuntimeFileLease.cs', 'utf8');
    assert.match(preflight, /pending.RuntimeResource is not Veil.Desktop.CefRuntimeRegistrationLease runtime[\s\S]*?Native.SnapshotModulePaths\(process, child\)[\s\S]*?runtime.RequireObservedApplicationModules\(applicationModules\)[\s\S]*?Native.SnapshotModulePaths\(process, child\)[\s\S]*?before.SequenceEqual\(after/);
    assert.match(native, /SnapshotModulePaths[\s\S]*?ObserveProcess\(process\) != expected[\s\S]*?CreateToolhelp32Snapshot\(0x00000008 \| 0x00000010/);
    assert.match(lease, /RequireObservedApplicationModule[\s\S]*?RequireCanonicalSingleLink\(observed.SafeFileHandle, modulePath\)[\s\S]*?original.VolumeSerial != current.VolumeSerial[\s\S]*?original.IndexHigh != current.IndexHigh \|\| original.IndexLow != current.IndexLow/);
});


test('broker hashes runtime before the admission and reuses retained files in redemption', async () => {
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    const lease = await readFile('apps/desktop/Engine/CefRuntimeFileLease.cs', 'utf8');
    const verify = preflight.slice(preflight.indexOf('internal static bool VerifyRedemption'), preflight.indexOf('internal static CefPendingChild Stage'));
    const stage = preflight.slice(preflight.indexOf('internal static CefPendingChild Stage'));
    assert.doesNotMatch(verify, /CefRuntimeRegistrationLease.OpenInstalled/);
    assert.match(stage, /OpenInstalled\(serializedBrokerAccess: true\)[\s\S]*?new CefChildAdmission[\s\S]*?termination, image, runtime/);
    assert.match(lease, /!serializedBrokerAccess && Environment.CurrentManagedThreadId != ownerThread/);
    assert.match(lease, /foreach \(string modulePath in modulePaths\) CheckObservedApplicationModule/);
});


test('disabled preflight polls live authority and never confirms forced termination', async () => {
    const runner = await readFile('apps/desktop/Engine/CefDisabledPreflight.cs', 'utf8');
    const process = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    assert.match(runner, /while \(!child.WaitForExit\(0\)\)[\s\S]*?if \(!Current\(\)\)[\s\S]*?!child.ImageLoadMitigationsActive[\s\S]*?child.WaitForExit\(Math.Min\(250, remaining\)\)/);
    assert.match(process, /ForcedTerminationExit = 35/);
    assert.match(process, /TerminateJobObject\(job, ForcedTerminationExit\)/);
    // Stop at the separate rejected-creation recovery method. A natural
    // exit proof must not invoke any of the forced-cleanup helpers.
    const start = process.indexOf('internal bool WaitForNaturalJobExit');
    const end = process.indexOf('private bool TryDrainRejectedCreation', start);
    assert.ok(start >= 0 && end > start);
    const natural = process.slice(start, end);
    assert.match(natural, /while \(ActiveProcesses\(\) != 0\)[\s\S]*?return WaitForExit\(remaining\);/);
    assert.doesNotMatch(natural, /\b(?:TerminateJobObject|TerminateProcess|TryDrainRejectedCreation|TerminateAndDrain|Dispose)\s*\(/);
    assert.match(runner, /!child.WaitForNaturalJobExit\(250\) \|\| !Current\(\)[\s\S]*?confirm\(\);/);
});


test('broker monitors the exact staged or redeemed disabled candidate and revokes on lost authority', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    assert.match(preflight, /CefRuntimeRegistrationLease.OpenInstalled[\s\S]*?OpenCefTerminationHandle\(binding.Identity, binding.Handle\)[\s\S]*?new CefPendingChild/);
    assert.match(native, /OpenCefTerminationHandle[\s\S]*?!SameLiveProcess\(observed, handle\)[\s\S]*?ObserveProcess\(handle\) != child/);
    assert.match(native, /TerminateProcess\(child, 35\)[\s\S]*?WaitForSingleObject\(child, 2000\) == 0/);
    assert.match(broker, /MonitorPendingCefChildAsync\(CefPendingChild expected, object generation\)[\s\S]*?await serial.WaitAsync\(\)[\s\S]*?!ReferenceEquals\(pendingCefChild, expected\)[\s\S]*?CandidateAuthorityCurrent\(generation\)[\s\S]*?RevokePendingCefChild\(\)/);
    assert.match(broker, /MonitorCompletedPreflightAsync\(CompletedPreflight expected\)[\s\S]*?!ReferenceEquals\(completedPreflight, expected\)[\s\S]*?CandidateAuthorityCurrent\(expected.Generation\)[\s\S]*?RevokeCompletedPreflight\(\)/);
});


test('unconfirmed CEF exit retains the same process handle and blocks new admissions', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const pending = await readFile('apps/broker/Veil.VolumeBroker/CefPendingChild.cs', 'utf8');
    assert.match(pending, /DetachRevokedProcess\(\)[\s\S]*?admission.Revoke\(\);[\s\S]*?process = null;/);
    assert.match(broker, /StopPendingChild\(CefPendingChild pending\)[\s\S]*?pending.DetachRevokedProcess\(\)/);
    assert.match(broker, /StopOrRetain\(CandidateStop candidate\)[\s\S]*?if \(ReleaseCandidate\(candidate\)\) return;\s*RetainForRetry\(candidate\);[\s\S]*?unresolvedCefStops.Add\(candidate\)/);
    assert.match(broker, /private void RetryUnresolvedCefStops\(\)[\s\S]*?if \(!ReleaseCandidate\(candidate\)\) continue;\s*unresolvedCefStops.RemoveAt\(i\)/);
    assert.match(broker, /request.Command is BrokerCommand.Detach or BrokerCommand.Unlock or BrokerCommand.StageCefChild\)\s*return Protocol.Error\("CefExitUnconfirmed"\)/);
    assert.match(broker, /var resources = new CandidateStop\(receipt.Child, receipt.Job, receipt.Image, receipt.Runtime\);\s*if \(confirmed\)[\s\S]*?!ReleaseCandidate\(resources, exitAlreadyConfirmed: true\)[\s\S]*?RetainForRetry\(resources\);\s*code = "CefExitUnconfirmed";[\s\S]*?else StopOrRetain\(resources\)/);
});


test('future broker-owned disabled job is created internally and never accepts client job handles', async () => {
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    assert.match(job, /CreateDisabledCandidate\(\) => Create\(1\);/);
    assert.match(job, /#else\s*if \(activeLimit != 1\) throw new ArgumentOutOfRangeException\(nameof\(activeLimit\)\);\s*#endif/);
    assert.match(job, /CreateJobObjectW\(IntPtr.Zero, null\)[\s\S]*?limits.Basic.Flags = 0x00000008 \| 0x00002000;[\s\S]*?limits.Basic.ActiveLimit = activeLimit/);
    assert.match(job, /assignmentIssued = true;[\s\S]*?Native.DuplicateCefJobAssignment\(job, owner, observedOwner\)/);
    assert.match(native, /DuplicateJobHandle\(new IntPtr\(-1\), job, destination, out IntPtr remote, 0x0001 \| 0x0004, false, 0\)/);
    assert.match(job, /!IsProcessInJob\(ownerHandle, job, out bool ownerInside\) \|\| ownerInside[\s\S]*?!IsProcessInJob\(childHandle, job, out bool childInside\) \|\| !childInside[\s\S]*?state.TotalProcesses != 1 \|\| state.ActiveProcesses != 1/);
    assert.match(job, /TerminateJobObject\(job, 35\)[\s\S]*?while \(!HasNoActiveProcesses\(\)\)/);
    assert.doesNotMatch(broker, /IssueAssignmentHandle|DuplicateCefJobAssignment/);
});


test('broker can bind a desktop-contained child without exporting job assignment rights', async () => {
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const native = await readFile('apps/broker/Veil.VolumeBroker/Native.cs', 'utf8');
    const integration = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    const direct = job.slice(job.indexOf('internal void AssignVerifiedCandidate'), job.indexOf('internal void BindSingleCandidate'));
    assert.match(direct, /Accounting\(\).TotalProcesses != 0[\s\S]*?Native.OpenCefAssignmentHandle\(child, childHandle\)[\s\S]*?AssignProcessToJobObject\(job, assignment\)[\s\S]*?BindSingleCandidate/);
    assert.doesNotMatch(direct, /IssueAssignmentHandle|DuplicateCefJobAssignment/);
    assert.match(native, /OpenCefAssignmentHandle[\s\S]*?0x0100 \| 0x0001[\s\S]*?!SameLiveProcess\(observed, handle\)/);
    assert.match(integration, /CefContainedProcess.CreateSuspended\(image, environment\)[\s\S]*?brokerJob.AssignVerifiedCandidate[\s\S]*?candidate.ResumeAfterVerification[\s\S]*?if \(closeBrokerFirst\) brokerJob.Dispose\(\);\s*else candidate.Dispose\(\);[\s\S]*?observer.WaitForExit/);
});


test('production disabled candidate retains broker job through admission, receipt and failed stop', async () => {
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    assert.match(preflight, /job = CefBrokerOwnedJob.CreateDisabledCandidate\(\);\s*job.AssignVerifiedCandidate\(owner, ownerHandle, binding.Identity, termination\);[\s\S]*?termination, image, runtime, job/);
    assert.match(broker, /pending.JobResource is not CefBrokerOwnedJob job[\s\S]*?pending.DetachJob\(\)[\s\S]*?new CompletedPreflight\(generation, retained, job,/);
    assert.match(broker, /Native.MatchesExitedNomination[\s\S]*?!receipt.Job.HasNoActiveProcesses\(\)/);
    assert.match(broker, /candidate.Job.TerminateAndDrain\(2000\)[\s\S]*?Native.TerminateCefChildAndWait\(candidate.Child\)[\s\S]*?return drained && exited/);
});


test('nested CEF runtime experiment remains isolated from production admission', async () => {
    const fixture = await readFile('tests/cef/CefNestedSmoke/Program.cs', 'utf8');
    const runner = await readFile('scripts/test-cef-wpf.ps1', 'utf8');
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const workflow = await readFile('.github/workflows/veil-checks.yml', 'utf8');
    assert.match(fixture, /CefContainedProcess.CreateSuspended\(image, environment\)[\s\S]*?second.AssignRoot\(root\)[\s\S]*?child.ResumeAfterVerification/);
    assert.match(fixture, /s.Restricted && s.IntegrityRid <= 0x1000/);
    assert.match(fixture, /exit != 0 \|\| !outerEmpty \|\| !innerEmpty \|\| !rendererRestrictedLow/);
    assert.match(runner, /Invoke-Stage 'nestedWin32'[\s\S]*?Invoke-Bounded 'nested-win32'/);
    assert.match(workflow, /dotnet build tests\/cef\/CefNestedSmoke\/CefNestedSmoke.csproj -c Release/);
    const brokerProject = await readFile('apps/broker/Veil.VolumeBroker/Veil.VolumeBroker.csproj', 'utf8');
    const testProject = await readFile('tests/security/CefContainmentCheck/CefContainmentCheck.csproj', 'utf8');
    const integration = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    assert.match(job, /CreateDisabledCandidate\(\) => Create\(1\);/);
    assert.match(job, /#if VEIL_CEF_CONTAINMENT_TEST[\s\S]*?CreateMultiprocessFixture\(\) => Create\(2\);[\s\S]*?#endif/);
    assert.match(job, /ContainsVerifiedCandidate[\s\S]*?if \(activeLimit != 1 \|\| !bound/);
    assert.match(job, /ContainsFixturePair[\s\S]*?FixturePairRefusal[\s\S]*?Native.ObserveProcess\(descendantHandle\) != descendant[\s\S]*?IsProcessInJob\(descendantHandle, job,[\s\S]*?state.ActiveProcesses == 2 && state.TotalProcesses == 2/);
    assert.match(integration, /ContainsFixturePair\(binding.Identity, retained, identity, owner.SafeHandle\)/);
    assert.doesNotMatch(brokerProject, /VEIL_CEF_CONTAINMENT_TEST/);
    assert.match(testProject, /<DefineConstants>[^<]*VEIL_CEF_CONTAINMENT_TEST<\/DefineConstants>/);
    assert.doesNotMatch(startup, /CefNestedSmoke|FixtureJob/);
});


test('native CI publishes only a test runtime bundle and local runner verifies evidence', async () => {
    const packaging = await readFile('scripts/package-cef-nested-smoke.ps1', 'utf8');
    const runner = await readFile('scripts/run-cef-nested-bundle.ps1', 'utf8');
    const workflow = await readFile('.github/workflows/cef-native-build.yml', 'utf8');
    assert.match(packaging, /@\('veil-cef-smoke.exe', 'veil-cef-smoke.dll'\)/);
    assert.match(packaging, /scope = 'unsigned-nested-cef-smoke-only'/);
    assert.match(packaging, /runtimeStatus = 'NOT RUN'/);
    assert.match(workflow, /uses: actions\/upload-artifact@v4/);
    assert.match(runner, /Assert-Inventory[\s\S]*?Start-Process -FilePath \$program[\s\S]*?Assert-Inventory/);
    assert.match(runner, /productReleaseGates = 'NOT RUN'/);
});


test('failed initial broker job assignment transfers only verified handles into stop recovery', async () => {
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const stage = preflight.slice(preflight.indexOf('internal static CefPendingChild Stage'));
    assert.match(stage, /CefPinnedImage.OpenVerified[\s\S]*?runtime.RequireCurrent\(\)[\s\S]*?termination = Native.OpenCefTerminationHandle[\s\S]*?job.AssignVerifiedCandidate/);
    assert.match(stage, /catch\s*\{\s*try\s*\{\s*if \(termination is not null\)[\s\S]*?termination = null;\s*job = null;\s*image = null;\s*runtime = null;\s*stopRejected\(rejected, rejectedJob, rejectedImage, rejectedRuntime\)/);
    assert.match(broker, /CefChildPreflight.Stage[\s\S]*?\(child, job, image, runtime\) => StopOrRetain\(new CandidateStop\(child, job, image, runtime\)\)/);
    // Revocation may leave three independent blockers. All must be
    // checked again after the owner/volume refresh and before dispatch.
    const execute = broker.slice(broker.indexOf('private async Task<byte[]> Execute('),
        broker.indexOf('private static string? LockCode('));
    const afterRevocation = execute.slice(execute.indexOf('RevokeCompletedPreflight();'),
        execute.indexOf('switch (request.Command)'));
    assert.match(afterRevocation, /RevokeCompletedPreflight\(\);[\s\S]*?if \(unresolvedCefStops\.Count != 0 &&\s*request\.Command is BrokerCommand\.Detach or BrokerCommand\.Unlock or BrokerCommand\.StageCefChild\)\s*return Protocol\.Error\("CefExitUnconfirmed"\);\s*if \(diagnosticSession\.Occupied &&\s*request\.Command is BrokerCommand\.Lock or BrokerCommand\.Detach or BrokerCommand\.Unlock or BrokerCommand\.StageCefChild\)\s*return Protocol\.Error\("CefExitUnconfirmed"\);\s*if \(diagnosticRelock\.Pending && request\.Command != BrokerCommand\.Status\)\s*return Protocol\.Error\("LockRefused"\);\s*$/);
    assert.match(broker, /bool drained = Attempt\([\s\S]*?bool exited = Attempt\([\s\S]*?bool empty = Attempt\([\s\S]*?return drained && exited && empty/);
});


test('broker retains executable and runtime pins until confirmed candidate exit', async () => {
    const pending = await readFile('apps/broker/Veil.VolumeBroker/CefPendingChild.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const preflight = await readFile('apps/broker/Veil.VolumeBroker/CefChildPreflight.cs', 'utf8');
    assert.match(pending, /DetachFilePins\(\)[\s\S]*?admission.Revoke\(\);[\s\S]*?image = null;\s*runtime = null;/);
    assert.match(preflight, /stopRejected\(rejected, rejectedJob, rejectedImage, rejectedRuntime\)/);
    assert.match(broker, /var pins = pending.DetachFilePins\(\);\s*var receipt = new CompletedPreflight\(generation, retained, job, pins.Image, pins.Runtime,/);
    assert.match(broker, /StopPendingChild\(CefPendingChild pending\)[\s\S]*?pending.DetachFilePins\(\)[\s\S]*?StopOrRetain\(new CandidateStop\(child, job as CefBrokerOwnedJob, pins.Image, pins.Runtime\)\)/);
    const policy = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    assert.match(broker, /CefRetainedLifetime Lifetime \{ get; \} = new\(Runtime, Image, Job, Child\)/);
    assert.match(broker, /candidate\.Lifetime\.TryRelease\(\(\) => exitAlreadyConfirmed \|\| TryStopVerifiedChild\(candidate\)\)/);
    assert.match(policy, /if \(!exitConfirmed\)\s*\{\s*if \(!verifyExit\(\)\) return false;\s*exitConfirmed = true;\s*\}[\s\S]*?retained\[next\]\?\.Dispose\(\);\s*retained\[next\+\+\] = null;/);
    assert.match(nested, /lifetime = new CefRetainedLifetime\(registration, preparedRoot, peerJob, brokerJob, rootHandle\)/);
    assert.match(nested, /guard\.Revoke\(\);\s*if \(borrowedCreation is not null\)\s*return guard\.RevokeAndCheckDrain\(\(\) => borrowedCreation\.RevokeAndRelease\(milliseconds\)\);\s*return lifetime!\.TryRelease\(\(\) => guard\.RevokeAndCheckDrain\(\(\) => drain\(milliseconds\)\)\);/);
    const creation = await readFile('apps/broker/Veil.VolumeBroker/CefPreparedCreationLifetime.cs', 'utf8');
    assert.match(creation, /lifetime = new CefRetainedLifetime\(prerequisites, inner, outer\)/);
    assert.match(creation, /return lifetime\.TryRelease\(\(\) => TryDrainAll\(milliseconds,\s*custody\.TryDrainAndRelease, outer\.TerminateAndDrain, inner\.TerminateAndDrain,\s*\(\) => outer\.HasNoActiveProcesses\(\) && inner\.HasNoActiveProcesses\(\)\)\);/);
});

test('isolated CEF preflight entry is compile-time exclusive and never falls back to browsing', async () => {
    const project = await readFile('apps/desktop/Veil.Desktop.csproj', 'utf8');
    const program = await readFile('apps/desktop/Program.cs', 'utf8');
    const entry = await readFile('apps/desktop/Engine/CefProtectedPreflightEntry.cs', 'utf8');
    assert.match(project, /<PropertyGroup Condition="'\$\(Configuration\)' == 'CefPreflight'">[\s\S]*?VEIL_CEF_PREFLIGHT_ONLY/);
    assert.match(program, /#if VEIL_CEF_PREFLIGHT_ONLY[\s\S]*?return CefProtectedPreflightEntry\.Run\(args\);\s*#else/);
    assert.match(entry, /#if VEIL_CEF_PREFLIGHT_ONLY/);
    assert.match(entry, /bool prepareOnly = args is \["--cef-prepare-profile"\];\s*bool brokerProbe = args is \["--cef-broker-suspended-probe"\];\s*if \(\(!prepareOnly && !brokerProbe && args is not \["--cef-protected-preflight"\]\) \|\| !ExternalAddress\.Installed\)/);
    assert.match(entry, /ProfileGate\.RejectElevated\(\);[\s\S]*?new BrowserActivation\(\);[\s\S]*?!activation\.Primary/);
    assert.match(entry, /BrokerClientConfig\.Load\(BrokerClientConfig\.ProductPath, development: false\)/);
    assert.match(entry, /ProtectedProfileSession\.TryOpen[\s\S]*?ClearPassword\(password\);[\s\S]*?CefDisabledLaunchCoordinator\.RunPreflight/);
    assert.match(entry, /result = CefDisabledLaunchCoordinator\.RunPreflight\(session\);\s*\}\s*if \(result != expected \|\| notifications\.LockRequested\) return 1;\s*if \(!brokerProbe\) session\.CompleteCefPreflightCleanup\(\);[\s\S]*?session\?\.Dispose\(\);[\s\S]*?return !notifications\.LockRequested && result == expected\s*\? \(int\)expected : 1;/);
    assert.doesNotMatch(entry, /new BrowserWindow|CreateEngine\(|UnmanagedDevelopmentVolume|activation\.Forward|activation\.Start|GetEnvironmentVariable/);
});

test('isolated preflight observes session notifications independently of synchronous owner checks', async () => {
    const entry = await readFile('apps/desktop/Engine/CefProtectedPreflightEntry.cs', 'utf8');
    const pump = await readFile('apps/desktop/Engine/CefPreflightNotificationPump.cs', 'utf8');
    const notifications = await readFile('apps/desktop/ProtectedSessionNotifications.cs', 'utf8');
    assert.match(entry, /using var pump = new CefPreflightNotificationPump\(application\.Dispatcher,[\s\S]*?var notifications = pump\.Notifications;[\s\S]*?if \(notifications\.LockRequested\) return 1;[\s\S]*?ProtectedProfileSession\.TryOpen/);
    assert.match(pump, /#if VEIL_CEF_PREFLIGHT_ONLY/);
    assert.match(pump, /new Thread\([\s\S]*?new ProtectedSessionNotifications\([\s\S]*?owner\.BeginInvoke[\s\S]*?Dispatcher\.Run\(\)/);
    assert.match(pump, /thread\.SetApartmentState\(ApartmentState\.STA\);[\s\S]*?thread\.Start\(\)/);
    assert.match(pump, /finally[\s\S]*?notifications\?\.MarkUnavailable\(\);[\s\S]*?notifications\?\.Dispose\(\)/);
    assert.match(pump, /BeginInvokeShutdown[\s\S]*?thread\.Join/);
    assert.match(notifications, /Volatile\.Read\(ref lockRequested\) != 0/);
    assert.match(notifications, /Interlocked\.Exchange\(ref lockRequested, 1\) == 0[\s\S]*?Dispatcher\.BeginInvoke/);
    assert.doesNotMatch(pump, /Dispatcher\.Invoke\(|PushFrame|Thread\.Abort/);
});

test('preflight success requires explicit exact-volume cleanup before best-effort disposal', async () => {
    const session = await readFile('apps/desktop/ProtectedProfileSession.cs', 'utf8');
    const close = await readFile('apps/desktop/Engine/CefPreflightCleanup.cs', 'utf8');
    assert.match(session, /#if VEIL_CEF_PREFLIGHT_ONLY\s*[\s\S]*?internal void CompleteCefPreflightCleanup\(\)[\s\S]*?CefPreflightCleanup\.RequireClosed\(guid, attachedHere, broker\.QueryRegisteredVolume,\s*broker\.Lock, broker\.Detach\);[\s\S]*?disposed = true;/);
    assert.match(close, /RequireSame\(query\(\), expectedGuid\);\s*lockVolume\(\);\s*var locked = RequireSame\(query\(\), expectedGuid\);/);
    assert.match(close, /locked\.Unlocked \|\| !locked\.ReadyToUnlock/);
    assert.match(close, /if \(attachedHere\)[\s\S]*?detach\(\);[\s\S]*?query\(\) is not null/);
});


test('authenticated owner is monitored even without an active CEF candidate', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const unlock = broker.slice(broker.indexOf('case BrokerCommand.Unlock:'), broker.indexOf('case BrokerCommand.Lock when'));
    assert.match(unlock, /var authority = new CefAuthenticatedGeneration\(client, unlockOwner!\);\s*authenticatedGeneration = authority;\s*if \(!RefreshAuthenticatedOwner\(status\)\)[\s\S]*?else\s*_ = MonitorAuthenticatedGenerationAsync\(authority\);/);
    const monitor = broker.slice(broker.indexOf('private async Task MonitorAuthenticatedGenerationAsync('), broker.indexOf('private async Task MonitorCompletedPreflightAsync('));
    assert.match(monitor, /await Task\.Delay\(TimeSpan\.FromSeconds\(1\)\);\s*await serial\.WaitAsync\(\);[\s\S]*?if \(!ReferenceEquals\(authenticatedGeneration, expected\)\) return;\s*if \(!RefreshAuthenticatedOwner\(backend\.Status\(\)\)\) return;/);
    assert.match(monitor, /catch \(Exception error\)[\s\S]*?if \(ReferenceEquals\(authenticatedGeneration, expected\)\)\s*ClearAuthenticatedOwner\(\);[\s\S]*?return;[\s\S]*?finally \{ serial\.Release\(\); \}/);
    assert.doesNotMatch(monitor, /pendingCefChild|completedPreflight|backend\.(Lock|Unlock|Detach|Attach)\(/);
});


test('nested diagnostic lifetime requires an independently trusted initial root-only job pair', async () => {
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const initial = job.slice(job.indexOf('internal void RequireInitialNestedBinding('), job.indexOf('// P2-03 observation primitive'));
    assert.match(initial, /CompareJobObjects\(job, desktopJob\)/);
    assert.match(initial, /IsProcessInJob\(ownerHandle, job, out bool inBroker\) \|\| inBroker[\s\S]*?IsProcessInJob\(ownerHandle, desktopJob, out bool inDesktop\) \|\| inDesktop/);
    assert.match(initial, /brokerState\.ActiveProcesses != 1 \|\| brokerState\.TotalProcesses != 1 \|\|[\s\S]*?brokerState\.TotalTerminatedProcesses != 0[\s\S]*?desktopState\.ActiveProcesses != 1 \|\| desktopState\.TotalProcesses != 1 \|\|[\s\S]*?desktopState\.TotalTerminatedProcesses != 0/);
    assert.match(initial, /RequireContainmentLimits\(job, activeLimit\);\s*RequireContainmentLimits\(desktopJob, null\)/);
    assert.match(initial, /RequireInitialState\(\);\s*_ = ObserveNestedRetainedMembers\(desktopJob, \[\(root, rootHandle\)\]\);\s*RequireInitialState\(\)/);
    assert.match(initial, /const uint killOnClose = 0x00002000, breakaway = 0x00000800 \| 0x00001000;/);
    assert.match(initial, /\(flags & killOnClose\) == 0 \|\| \(flags & breakaway\) != 0/);
    assert.match(nested, /registration\.RequireCurrent\(\);\s*brokerJob\.RequireOrBindPreparedPrivatePair\(peerJob, owner, ownerHandle, root, rootHandle\);\s*registration\.RequireCurrent\(\);[\s\S]*?lifetime = new CefRetainedLifetime/);
    assert.doesNotMatch(broker, /IssueAssignmentHandle|DuplicateCefJobAssignment/);
});


test('private diagnostic pair binds the original broker-created Jobs and retained root once', async () => {
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    assert.match(job, /assignmentExported = true;[\s\S]*?return Native\.DuplicateCefJobAssignment/);
    assert.match(job, /assignmentExported \|\| peer\.assignmentExported \|\| activeLimit != peer\.activeLimit/);
    assert.match(job, /if \(privatePeer is not null \|\| peer\.privatePeer is not null\)[\s\S]*?RequireInitialNestedBinding\(peerHandle, owner, ownerHandle, root, rootHandle\);[\s\S]*?privatePeer = peer;\s*peer\.privatePeer = this;\s*privateRoot = peer\.privateRoot = rootHandle;/);
    assert.match(job, /!ReferenceEquals\(privatePeer, peer\) \|\| !ReferenceEquals\(peer\.privatePeer, this\) \|\|[\s\S]*?!ReferenceEquals\(privateRoot, rootHandle\) \|\| !ReferenceEquals\(peer\.privateRoot, rootHandle\)/);
    assert.match(job, /TerminateAndVerifyNestedDrain\(RequireBoundPrivatePeer\(peer, rootHandle\), rootHandle, milliseconds\)/);
    assert.match(nested, /brokerJob\.ObservePrivateNestedDiagnostics\(peerJob, root, rootHandle,/);
    assert.match(nested, /brokerJob\.TerminateAndVerifyPrivateDrain\(peerJob, rootHandle, milliseconds\)/);
    assert.doesNotMatch(nested, /SafeFileHandle desktopJob|ObserveCollectedNestedDiagnostics\(/);
    assert.doesNotMatch(broker, /RequirePrivateNestedBinding/);
});


test('broker retains diagnostic slot through revocation and refuses unsafe operations until release', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const policy = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    const startup = await readFile('apps/desktop/Program.cs', 'utf8');
    const registerStart = broker.indexOf('private CefDiagnosticActivationOutcome TryRegisterDiagnosticSession(');
    const registerEnd = broker.indexOf('private void ClearAuthenticatedOwner()');
    assert.ok(registerStart >= 0 && registerEnd > registerStart);
    const register = broker.slice(registerStart, registerEnd);
    assert.match(register, /diagnosticSession\.Occupied \|\| pendingCefChild is not null \|\|[\s\S]*?completedPreflight is not null \|\| unresolvedCefStops\.Count != 0 \|\|[\s\S]*?!CandidateAuthorityCurrent\(expectedGeneration\)/);
    assert.match(register, /CefPinnedImage\.OpenVerified[\s\S]*?registration\.RequireCurrent\(\);[\s\S]*?!CandidateAuthorityCurrent\(expectedGeneration\)[\s\S]*?new CefBoundDiagnosticSession[\s\S]*?diagnosticSession\.RegisterAndActivate\(expectedGeneration, session,/);
    assert.equal((broker.match(/TryRegisterDiagnosticSession\(/g) ?? []).length, 1, 'registration must remain disconnected from requests');
    assert.doesNotMatch(startup, /CefBoundDiagnosticSession|TryRegisterDiagnosticSession/);
    assert.match(broker, /CefOwnerRevocationCleanup.Run\(diagnosticSession\.Retire, RevokePendingCefChild,\s*RetryDiagnosticSession,[\s\S]*?EnsureCefStopRetry\(\)/);
    assert.equal((broker.match(/diagnosticSession\.Occupied &&\s*request\.Command is BrokerCommand\.Lock or BrokerCommand\.Detach or BrokerCommand\.Unlock or BrokerCommand\.StageCefChild/g) ?? []).length, 2);
    assert.match(broker, /RetryUnresolvedCefStops\(\);\s*if \(diagnosticSession\.Occupied\)\s*throw new InvalidOperationException\("CEF diagnostic lifetime remains unconfirmed; service cleanup refused\."\)/);
    assert.match(broker, /RetryUnresolvedCefStops\(\);\s*if \(diagnosticSession\.Occupied\)\s*throw new InvalidOperationException\("CEF diagnostic lifetime remains unconfirmed; crash relock deferred\."\)/);
    assert.match(broker, /bool pending = unresolvedCefStops\.Count != 0 \|\| diagnosticSession\.CleanupPending \|\| diagnosticRelock\.Pending;\s*continueRetry = CefStopRetryPolicy\.ShouldContinue\(attempt, 30, pending, failure\);/);
    assert.match(policy, /return attempt < maximumAttempts - 1 && \(pending \|\| failure is not null\);/);
    assert.match(policy, /if \(Occupied \|\| releasing \|\| activating\) return false;[\s\S]*?session = candidate;/);
    assert.match(policy, /retired = true;\s*session\.Revoke\(\);/);
    assert.match(policy, /if \(!session\.RevokeAndRelease\(milliseconds\)\) return false;\s*session = null;/);
});


test('registered diagnostic session requires installed signed baseline and retires on observation failure', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const verifier = await readFile('apps/broker/Veil.VolumeBroker/CefSystemModuleVerifier.cs', 'utf8');
    const register = broker.slice(broker.indexOf('private CefDiagnosticActivationOutcome TryRegisterDiagnosticSession('),
        broker.indexOf('private void ClearAuthenticatedOwner()'));
    assert.match(register, /ReadOnlySpan<byte> baselinePayload, ReadOnlySpan<byte> baselineSignature/);
    assert.match(register, /CefSignedSystemModuleBaseline\.VerifyInstalledTrust\(baselinePayload, baselineSignature\);\s*systemVerifier\.RequireBoundTrust\(\);[\s\S]*?new CefBoundDiagnosticSession[\s\S]*?\}, systemVerifier, preparedRoot\);[\s\S]*?diagnosticSession\.RegisterAndActivate/);
    assert.doesNotMatch(nested, /CefSystemModuleVerifier\?|systemVerifier\s+is null/);
    assert.match(nested, /systemVerifier\.RequireModules\);\s*systemVerifier\.RequireBoundTrust\(\);\s*if \(result\.UnverifiedSystemModules != 0\)/);
    assert.match(nested, /readContext = \(\) =>\s*\{\s*systemVerifier\.RequireBoundTrust\(\);/);
    assert.match(verifier, /if \(requireHostCurrent is null\)\s*throw new UnauthorizedAccessException[\s\S]*?requireHostCurrent\(\);/);
    const monitor = broker.slice(broker.indexOf('private async Task MonitorAuthenticatedGenerationAsync('),
        broker.indexOf('private async Task MonitorCompletedPreflightAsync('));
    assert.match(monitor, /diagnosticSession\.ObserveCurrent\(unlockOwner, candidate =>[\s\S]*?candidate is not CefBoundDiagnosticSession session[\s\S]*?_ = session\.Observe\(\);[\s\S]*?catch \(Exception error\)[\s\S]*?ClearAuthenticatedOwner\(\);/);
    assert.equal((broker.match(/TryRegisterDiagnosticSession\(/g) ?? []).length, 1);
});


test('diagnostic retirement latches relock until drain and exact non-forced lock verification', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const policy = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    const clear = broker.slice(broker.indexOf('private void ClearAuthenticatedOwner()'), broker.indexOf('private void RevokePendingCefChild()'));
    assert.match(clear, /if \(diagnosticSession\.Occupied\)\s*diagnosticRelock\.Request\(unlockOwner\);[\s\S]*?CefOwnerRevocationCleanup.Run\(diagnosticSession\.Retire,/);
    assert.match(clear, /unresolvedCefStops\.Count != 0 \|\| diagnosticSession\.CleanupPending \|\| diagnosticRelock\.Pending\)\s*EnsureCefStopRetry/);
    const relock = broker.slice(broker.indexOf('private bool TryCompleteDiagnosticRelock()'), broker.indexOf('private async Task RetryCefStopsAsync()'));
    assert.match(relock, /authenticatedGeneration is null && !diagnosticSession\.Occupied &&\s*unresolvedCefStops\.Count == 0 && pendingCefChild is null && completedPreflight is null/);
    assert.match(relock, /if \(!Registered\(status\)\) return false;\s*if \(status\.Unlocked && backend\.Lock\(\) != LockResult\.Locked\) return false;\s*status = backend\.Status\(\);\s*return Registered\(status\) && !status\.Unlocked && status\.ReadyToUnlock/);
    assert.match(broker, /CefStopRetryPolicy.TryAttempt\(RetryUnresolvedCefStops,\s*\(\) => TryCompleteDiagnosticRelock\(\)\);[\s\S]*?diagnosticRelock\.Pending;[\s\S]*?CefStopRetryPolicy.ShouldContinue\(attempt, 30, pending, failure\)/);
    assert.match(broker, /diagnosticRelock\.Pending && request\.Command != BrokerCommand\.Status\)\s*return Protocol\.Error\("LockRefused"\);/);
    assert.match(policy, /!ReferenceEquals\(generation, currentGeneration\) \|\| !cleanupConfirmed\)\s*return false;\s*if \(!lockAndVerify\(\)\) return false;\s*Pending = false/);
});


test('atomic private Job pair is fixture-only and both handles are retained at creation', async () => {
    const launch = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    assert.match(launch, /CreateSuspendedCore\(image, environment, null\);[\s\S]*?#if VEIL_CEF_CONTAINMENT_TEST[\s\S]*?CreateSuspendedWithFixtureJobs[\s\S]*?CreateSuspendedCore\(image, environment, \[outer, inner\]\);[\s\S]*?#endif/);
    assert.match(launch, /jobs = additionalJobs is null \? \[job\] : \[job, \.\. additionalJobs\];[\s\S]*?item\.DangerousAddRef[\s\S]*?Marshal\.WriteIntPtr\(value, i \* IntPtr\.Size, jobs\[i\]\.DangerousGetHandle\(\)\)/);
    assert.match(launch, /foreach \(var extra in additionalJobs\)\s*if \(!IsProcessInJob\(process, extra, out bool nested\) \|\| !nested\)/);
    assert.match(job, /#if VEIL_CEF_CONTAINMENT_TEST[\s\S]*?CreateAtomicPairFixture[\s\S]*?assignmentIssued = peer\.assignmentIssued = true;\s*return Veil\.Desktop\.CefContainedProcess\.CreateSuspendedWithFixtureJobs\(image, environment, job, peer\.job\);[\s\S]*?#endif/);
    assert.match(fixture, /mode == 6[\s\S]*?CreateAtomicPairFixture\(privatePeer!, image, environment\)[\s\S]*?brokerJob\.BindSingleCandidate[\s\S]*?privatePeer!\.BindSingleCandidate[\s\S]*?RequirePrivateNestedBinding[\s\S]*?candidate\.ResumeAfterVerification/);
});


test('internal diagnostic root and owner require separate standard-user tokens and same logon', async () => {
    const observer = await readFile('apps/broker/Veil.VolumeBroker/CefProcessTokenObserver.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    assert.match(observer, /ReadVariable\(token, 2\); \/\/ TokenGroups/);
    assert.match(observer, /ReadVariable\(token, 3\); \/\/ TokenPrivileges/);
    assert.match(observer, /snapshot\.AdministratorGroupPresent \|\|\s*!snapshot\.OnlyStandardPrivileges/);
    assert.match(observer, /owner\.AuthenticationId != root\.AuthenticationId/);
    assert.match(nested, /ObserveLaunchToken\(ownerHandle, owner\);[\s\S]*?ObserveLaunchToken\(rootHandle, root\);[\s\S]*?CefLaunchTokenPolicy\.RequirePair\(ownerToken, rootToken\);[\s\S]*?RequireOrBindPreparedPrivatePair/);
    assert.match(nested, /CefLaunchTokenPolicy\.RequireSame\(ownerToken, CefProcessTokenObserver\.ObserveLaunchToken\(ownerHandle, owner\)\);[\s\S]*?CefLaunchTokenPolicy\.RequireSame\(rootToken, CefProcessTokenObserver\.ObserveLaunchToken\(rootHandle, root\)\);/);
});


test('launch token lease duplicates only verified retained standard-user primary token', async () => {
    const observer = await readFile('apps/broker/Veil.VolumeBroker/CefProcessTokenObserver.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const lease = observer.slice(observer.indexOf('internal sealed class LaunchTokenLease'),
        observer.indexOf('private static SafeAccessTokenHandle OpenQueryToken'));
    assert.match(lease, /ObserveLaunchToken\(owner, expected\);\s*CefLaunchTokenPolicy\.RequireStandard\(observed\);\s*using var source = OpenQueryToken\(owner, 0x0008 \| 0x0002\)/);
    assert.match(lease, /DuplicateTokenEx\(source, 0x000B, IntPtr.Zero, 2, 1, out var copy\)[\s\S]*?RequireDuplicate\(observed, snapshot\)[\s\S]*?RequireSame\(observed, ObserveLaunchToken\(owner, expected\)\)[\s\S]*?return new LaunchTokenLease/);
    assert.match(lease, /catch \{ copy\.Dispose\(\); throw; \}/);
    assert.match(lease, /ObjectDisposedException\.ThrowIf\(disposed, this\);[\s\S]*?RequireSame\(sourceSnapshot, ObserveLaunchToken[\s\S]*?RequireSame\(duplicateSnapshot,/);
    assert.doesNotMatch(lease, /CreateProcessAsUser|AdjustTokenPrivileges|SetTokenInformation|WindowsIdentity\.GetCurrent|GetLinkedToken/);
    assert.doesNotMatch(broker, /LaunchTokenLease|DuplicateTokenEx/);
});


test('user-token atomic creation remains containment fixture only with no fallback on failure', async () => {
    const launch = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    const token = await readFile('apps/broker/Veil.VolumeBroker/CefProcessTokenObserver.cs', 'utf8');
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    assert.match(launch, /#if VEIL_CEF_CONTAINMENT_TEST \|\| VEIL_CEF_BROKER\s*if \(userToken is not null\)[\s\S]*?CreateProcessAsUserW\(userToken, image, command, IntPtr.Zero, IntPtr.Zero, false, flags,[\s\S]*?throw Failure\("CreateProcessAsUser"\);\s*\}\s*else\s*#endif\s*if \(!CreateProcessW/);
    assert.match(token, /#if VEIL_CEF_CONTAINMENT_TEST[\s\S]*?RequireCurrent\(\);\s*var child = Veil\.Desktop\.CefContainedProcess\.CreateSuspendedWithUserFixtureJobs[\s\S]*?RequireFixtureRoot\(borrowed, Native\.ObserveProcess\(borrowed\)\);[\s\S]*?catch \{ child\.Dispose\(\); throw; \}/);
    assert.match(job, /lease\.RequireCurrent\(\);\s*assignmentIssued = peer\.assignmentIssued = true;\s*return lease\.CreateUserFixture/);
    assert.match(fixture, /VEIL_TEST_STANDARD_USER_CREATION[\s\S]*?CreateAtomicUserPairFixture[\s\S]*?RequirePrivateNestedBinding[\s\S]*?ResumeAfterVerification[\s\S]*?lease\.RequireFixtureRoot[\s\S]*?TerminateAndVerifyPrivateDrain/);
});


test('prepared diagnostic lifetime is retained before activation and retired on ambiguous resume', async () => {
    const policy = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    assert.match(policy, /if \(!TryRegister\(expectedGeneration, candidate\)\)\s*return CefDiagnosticActivationOutcome\.Refused;\s*activating = true;[\s\S]*?activate\(\);[\s\S]*?if \(retired\)[\s\S]*?committed = true/);
    assert.match(policy, /failure = error;\s*return CefDiagnosticActivationOutcome\.Retired;[\s\S]*?try \{ if \(!committed\) Retire\(\); \}\s*finally \{ activating = false; \}/);
    assert.match(policy, /!retired && !activating && !observing && ReferenceEquals\(generation, expectedGeneration\)/);
    assert.match(policy, /if \(!retired \|\| releasing \|\| activating \|\| observing\) return false/);
    assert.match(fixture, /slot\.RegisterAndActivate\(generation, session,[\s\S]*?candidate\.ResumeRetainedRoot\(retained\)[\s\S]*?Synthetic lost resume confirmation[\s\S]*?slot\.TryReleaseRetired\(5000\)/);
    assert.equal((broker.match(/diagnosticSession\.RegisterAndActivate\(/g) ?? []).length, 2);
});


test('private broker activation transfers ownership before resume and latches relock on failure', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const reg = broker.slice(broker.indexOf('private CefDiagnosticActivationOutcome TryRegisterDiagnosticSession('),
        broker.indexOf('private void ClearAuthenticatedOwner()'));
    assert.match(reg, /ownershipTransferred = false;[\s\S]*?return CefDiagnosticActivationOutcome\.Refused;[\s\S]*?diagnosticSession\.RegisterAndActivate\(expectedGeneration, session, \(\) =>\s*\{\s*accepted = true;\s*session\.ActivatePreparedRoot\(\);/);
    assert.match(reg, /finally\s*\{\s*ownershipTransferred = accepted;\s*if \(accepted && !activated\)[\s\S]*?diagnosticRelock\.Request\(expectedGeneration\);\s*try \{ ClearAuthenticatedOwner\(\); \}\s*finally \{ EnsureCefStopRetry\(\); \}/);
    assert.match(nested, /activationAttempted = true;\s*_ = guard\.Observe\(readContext, \(\) =>\s*\{\s*requirePreparedRoot\(\);\s*preparedRoot\.ResumeRetainedRoot\(retainedRoot\);[\s\S]*?activated = true;/);
    assert.match(nested, /if \(!activated\)\s*throw new UnauthorizedAccessException[\s\S]*?return guard\.Observe\(readContext, observe\)/);
    assert.match(job, /RequirePreparedPrivateRoot[\s\S]*?RequireBoundPrivatePeer\(peer, rootHandle\)[\s\S]*?privateRootIdentity != root[\s\S]*?RequireInitialNestedBinding\(peerHandle, owner, ownerHandle, root, rootHandle\)/);
    assert.equal((broker.match(/TryRegisterDiagnosticSession\(/g) ?? []).length, 1);
});


test('internal prepared launch retains exact creation handles through three-job exit proof', async () => {
    const launch = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    assert.match(launch, /!CompareRootHandles\(process, expectedRoot\)[\s\S]*?GetProcessIdOfThread\(thread\) != pid/);
    const resume = launch.slice(launch.indexOf('internal void ResumeRetainedRoot('), launch.indexOf('internal bool WaitForExit('));
    assert.match(resume, /RequireRetainedPreparedRoot\(expectedRoot\);\s*retainedResumeAttempted = true;[\s\S]*?ResumeThread\(thread\) != 1/);
    assert.doesNotMatch(resume, /Dispose\(|TerminateProcess|Func<|Action/);
    assert.match(nested, /preparedRoot\.RequireRetainedPreparedRoot\(rootHandle\);[\s\S]*?retainedRoot = rootHandle;/);
    assert.match(nested, /TerminateAndVerifyPrivateDrain\(peerJob, rootHandle, milliseconds\)\) return false;[\s\S]*?preparedRoot\.WaitForNaturalJobExit\(remaining\);[\s\S]*?new CefRetainedLifetime\(registration, preparedRoot, peerJob, brokerJob, rootHandle\)/);
    assert.doesNotMatch(broker, /Action resumePreparedRoot|CefContainedProcess\.CreateSuspended/);
});


test('broker pre-creation preparation independently pins executable, runtime, trust and owner', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefPinnedImage.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    assert.match(prep, /if \(!authorityCurrent\(\)\)[\s\S]*?OpenInstalled\(serializedBrokerAccess: true\)[\s\S]*?OpenVerifiedBeforeCreation\(registeredPath, digest\)[\s\S]*?VerifyInstalledTrust\(baselinePayload, baselineSignature\)[\s\S]*?verifier\.RequireBoundTrust\(\);[\s\S]*?if \(!authorityCurrent\(\)\)[\s\S]*?LaunchTokenLease\.Acquire\(ownerHandle, owner\)[\s\S]*?prepared\.RequireCurrent\(\);/);
    assert.match(prep, /catch\s*\{[\s\S]*?token\?\.Dispose\(\);[\s\S]*?image\?\.Dispose\(\);[\s\S]*?registration\?\.Dispose\(\);/);
    assert.match(prep, /internal void RequireCurrent\(\)[\s\S]*?if \(!authorityCurrent\(\)\)[\s\S]*?registration\.RequireCurrent\(\);[\s\S]*?RequireRetainedBeforeCreation\(image, path, digest\);[\s\S]*?verifier\.RequireBoundTrust\(\);[\s\S]*?token\.RequireCurrent\(\);[\s\S]*?if \(!authorityCurrent\(\)\)/);
    assert.match(pin, /RequireRegisteredPath\(registeredPath\);\s*AdminOnlyFile\.RequireProtectedExecutable\(registeredPath\);[\s\S]*?FileShare\.Read\);[\s\S]*?RequireRetainedBeforeCreation\(image, registeredPath, digest\)/);
    assert.match(pin, /var image = OpenVerifiedBeforeCreation\(registeredPath, digest\);[\s\S]*?!Native\.MatchesLiveProcessImage\(process, child\)/);
    const entry = broker.slice(broker.indexOf('private CefBrokerLaunchPreparation PrepareDiagnosticLaunch('), broker.indexOf('private CefDiagnosticActivationOutcome TryRegisterDiagnosticSession('));
    assert.match(entry, /!diagnosticRelock\.Pending && !config\.Development &&[\s\S]*?pendingCefChild is null && completedPreflight is null && unresolvedCefStops\.Count == 0 &&[\s\S]*?CandidateAuthorityCurrent\(expectedGeneration\);[\s\S]*?if \(diagnosticSession\.Occupied \|\| !AuthorityCurrent\(\)/);
    assert.match(entry, /config\.CefChildImage is not \{ \} image[\s\S]*?config\.CefChildSha256 is not \{ \} digest[\s\S]*?CefBrokerLaunchPreparation\.Open\(image, digest, owner, ownerHandle,\s*AuthorityCurrent, baselinePayload, baselineSignature,\s*config.VolumeId, config.VolumeGuid, config.PipeName, backend.Status\)/);
    assert.equal((broker.match(/PrepareDiagnosticLaunch\(/g) ?? []).length, 2);
    assert.doesNotMatch(protocol, /PrepareDiagnosticLaunch|CefBrokerLaunchPreparation/);
    assert.doesNotMatch(prep, /CreateProcessAsUser|CreateSuspended|ResumeThread|ResumeRetainedRoot/);
});


test('retained creation captures original handles before verification and never drops them on refusal', async () => {
    const launch = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const project = await readFile('apps/broker/Veil.VolumeBroker/Veil.VolumeBroker.csproj', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    assert.match(project, /<DefineConstants>\$\(DefineConstants\);VEIL_CEF_BROKER<\/DefineConstants>/);
    assert.match(launch, /#if VEIL_CEF_BROKER[\s\S]*?CreateRetainedUserRoot[\s\S]*?token\.IsInvalid \|\| token\.IsClosed[\s\S]*?CreateSuspendedCore\(image, environment, \[outer, inner\], token, custody\);[\s\S]*?#endif/);
    assert.match(launch, /process = new SafeProcessHandle\(IntPtr.Zero, ownsHandle: true\);\s*thread = new SafeFileHandle\(IntPtr.Zero, ownsHandle: true\);\s*var prepared = new CefContainedProcess\(job, process, thread, custody is null \? null : additionalJobs\);[\s\S]*?CreateProcessW\(image, command,[\s\S]*?Marshal\.InitHandle\(process, info.Process\);\s*Marshal\.InitHandle\(thread, info.Thread\);\s*captured = prepared;\s*custody\?\.Capture\(captured\);[\s\S]*?IsProcessInJob\(process, job[\s\S]*?captured\.creationVerified = true;/);
    assert.match(launch, /custody\.Reject\(\);[\s\S]*?if \(captured is not null\) throw;[\s\S]*?TerminateProcess\(process, ForcedTerminationExit\)/);
    assert.match(launch, /if \(rejected \|\| transferred \|\| validating \|\| retained is not \{ creationVerified: true \} child\)/);
    assert.match(launch, /if \(transferred \|\| validating\) return false;[\s\S]*?rejected = true;[\s\S]*?if \(!child\.TryDrainRejectedCreation\(milliseconds\)\) return false;\s*child\.Dispose\(\);\s*retained = null;/);
    assert.match(launch, /if \(ActiveProcesses\(\) != 0\) _ = TerminateJobObject\(job, ForcedTerminationExit\);[\s\S]*?foreach \(var peer in retainedPeerJobs\)[\s\S]*?if \(peer.IsClosed \|\| peer.IsInvalid\) return false;[\s\S]*?if \(ActiveProcesses\(peer\) != 0\) _ = TerminateJobObject\(peer, ForcedTerminationExit\);[\s\S]*?if \(!WaitForExit\(0\)\) _ = TerminateProcess\(process, ForcedTerminationExit\);[\s\S]*?return WaitForRetainedCreationExit\(remaining\);/);
    const proof = launch.slice(launch.indexOf('internal bool WaitForRetainedCreationExit'), launch.indexOf('internal void TerminateAndDrain'));
    assert.match(proof, /if \(retainedPeerJobs.Length != 2\) return false;[\s\S]*?if \(retainedPeerJobs.Any\(peer => peer.IsClosed \|\| peer.IsInvalid\)\) return false;[\s\S]*?if \(ActiveProcesses\(\) == 0 && retainedPeerJobs.All\(peer => ActiveProcesses\(peer\) == 0\) &&\s*WaitForExit\(0\)\)\s*return true;[\s\S]*?if \(remaining == 0\) return false;/);
    assert.doesNotMatch(proof, /\b(?:TerminateJobObject|TerminateProcess|Dispose)\s*\(/);
    assert.match(fixture, /CreateAtomicRetainedPairFixture[\s\S]*?custody\.HasRetainedProcess[\s\S]*?custody\.TryDrainAndRelease\(5000\)[\s\S]*?outer\.HasNoActiveProcesses\(\) && inner\.HasNoActiveProcesses\(\)/);
    assert.doesNotMatch(broker, /CreateRetainedUserRoot|CreateRetainedFixture/);
});


test('prepared broker candidate creation is single-root, token-bound and remains disconnected from activation', async () => {
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    const observer = await readFile('apps/broker/Veil.VolumeBroker/CefProcessTokenObserver.cs', 'utf8');
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const launch = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    assert.match(prep, /creationAttempted = true;\s*RequireCurrent\(\);\s*var environment = profile.GetEnvironmentSnapshot\(token\);[\s\S]*?RequireCurrent\(\);\s*outer\.CreateRetainedDiagnosticPair\(inner, token, path, environment, custody\);\s*custody\.VerifyRetainedRoot\(root =>[\s\S]*?token\.RequireCreatedRoot\(root, path\);[\s\S]*?CefPinnedImage\.OpenVerified\(identity, root, path, digest\);\s*RequireCurrent\(\);/);
    assert.doesNotMatch(prep, /GetEnvironmentVariables|TakeVerified|ResumeRetainedRoot|ResumeThread/);
    assert.match(job, /#if VEIL_CEF_BROKER[\s\S]*?CreateRetainedDiagnosticPair[\s\S]*?activeLimit != 1 \|\| peer.activeLimit != 1[\s\S]*?assignmentExported \|\| peer.assignmentExported[\s\S]*?RequireContainmentLimits\(job, activeLimit\);[\s\S]*?lease\.RequireCurrent\(\);\s*assignmentIssued = peer.assignmentIssued = true;\s*lease\.CreateRetainedDiagnosticRoot/);
    assert.match(observer, /#if VEIL_CEF_BROKER[\s\S]*?CreateRetainedDiagnosticRoot[\s\S]*?RequireCurrent\(\);\s*Veil\.Desktop\.CefContainedProcess\.CreateRetainedUserRoot\(image, environment, outer, inner,\s*duplicate, custody\);[\s\S]*?RequireCreatedRoot[\s\S]*?ObserveLaunchToken\(root, identity\);\s*CefLaunchTokenPolicy\.RequirePair\(sourceSnapshot, observed\);[\s\S]*?RequireCurrent\(\);/);
    assert.match(launch, /validating = true;\s*try\s*\{\s*child\.RequireRetainedPreparedRoot\(child.process\);\s*verify\(child.process\);\s*child\.RequireRetainedPreparedRoot\(child.process\);\s*\}\s*catch\s*\{\s*rejected = true;\s*throw;\s*\}\s*finally \{ validating = false; \}/);
    assert.match(fixture, /custody\.VerifyRetainedRoot\(root =>[\s\S]*?Reject\(\(\) => custody\.TakeVerified\(\)\);[\s\S]*?!custody\.TryDrainAndRelease\(0\)[\s\S]*?Synthetic post-create token refusal[\s\S]*?custody\.HasRetainedProcess[\s\S]*?custody\.TryDrainAndRelease\(5000\)/);
    assert.equal((broker.match(/preparation\.CreateDiagnosticCandidate/g) ?? []).length, 1);
    assert.doesNotMatch(broker, /CreateRetainedDiagnosticPair/);
});


test('disconnected creation probe owns the slot before native creation and always retires before relock', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const life = await readFile('apps/broker/Veil.VolumeBroker/CefPreparedCreationLifetime.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    const probe = broker.slice(broker.indexOf('private void RunDiagnosticCreationProbe('), broker.indexOf('private CefDiagnosticActivationOutcome TryRegisterDiagnosticSession('));
    assert.match(broker, /CefDiagnosticSessionSlot<ICefDiagnosticLifetime> diagnosticSession/);
    assert.match(probe, /preparation = PrepareDiagnosticLaunch[\s\S]*?outer = CefBrokerOwnedJob\.CreateDisabledCandidate\(\);\s*inner = CefBrokerOwnedJob\.CreateDisabledCandidate\(\);[\s\S]*?new CefPreparedCreationLifetime[\s\S]*?diagnosticSession\.RegisterAndActivate\(expectedGeneration, probe, \(\) =>\s*\{\s*accepted = true;[^\n]*\s*probe\.Create\(\);/);
    assert.match(probe, /finally\s*\{[\s\S]*?try\s*\{\s*if \(!accepted\)[\s\S]*?try \{ inner\?\.Dispose\(\); \}[\s\S]*?try \{ outer\?\.Dispose\(\); \}\s*finally \{ preparation\?\.Dispose\(\); \}[\s\S]*?finally\s*\{[\s\S]*?if \(diagnosticRelock\.RequestIfCurrent\(expectedGeneration, unlockOwner\)\)\s*\{\s*try \{ ClearAuthenticatedOwner\(\); \}\s*finally \{ EnsureCefStopRetry\(\); \}/);
    assert.match(life, /return lifetime\.TryRelease\(\(\) => TryDrainAll\(milliseconds,\s*custody\.TryDrainAndRelease, outer\.TerminateAndDrain, inner\.TerminateAndDrain,\s*\(\) => outer\.HasNoActiveProcesses\(\) && inner\.HasNoActiveProcesses\(\)\)\);/);
    assert.match(life, /lifetime = new CefRetainedLifetime\(prerequisites, inner, outer\)/);
    assert.doesNotMatch(life, /ResumeRetainedRoot|ResumeAfterVerification|TakeVerified/);
    assert.doesNotMatch(probe, /ResumeRetainedRoot|TryRegisterDiagnosticSession\(/);
    assert.equal((broker.match(/RunDiagnosticCreationProbe\(/g) ?? []).length, 2);
    assert.doesNotMatch(protocol, /RunDiagnosticCreationProbe|CefPreparedCreationLifetime/);
});


test('native creation adopts handles into preallocated owners before allocating or freeing environment', async () => {
    const launch = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    const core = launch.slice(launch.indexOf('private static CefContainedProcess CreateSuspendedCore('),
        launch.indexOf('internal static string BuildEnvironmentBlock('));
    const allocated = core.indexOf('var prepared = new CefContainedProcess(');
    const create = core.indexOf('if (!CreateProcessW(');
    const adopt = core.indexOf('Marshal.InitHandle(process, info.Process);');
    const publish = core.indexOf('custody?.Capture(captured);');
    const free = core.indexOf('finally { Marshal.FreeHGlobal(env); }');
    assert.ok(allocated >= 0 && create > allocated && adopt > create && publish > adopt && free > publish);
    assert.doesNotMatch(core.slice(adopt, publish), /\bnew\s+|Dispose\(|FreeHGlobal\(/);
    assert.match(core, /custody\?\.Capture\(captured\);\s*#if VEIL_CEF_CONTAINMENT_TEST\s*if \(injectAllocationFailure\)\s*throw new OutOfMemoryException/);
    assert.match(fixture, /injectFailure: false, injectAllocationFailure: true\);[\s\S]*?catch \(OutOfMemoryException error\)[\s\S]*?custody\.HasRetainedProcess[\s\S]*?Reject\(\(\) => custody\.TakeVerified\(\)\);[\s\S]*?custody\.TryDrainAndRelease\(5000\)/);
});


test('prepared creation binds private Jobs to original custody handle before session construction', async () => {
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    const job = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const session = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    assert.match(prep, /custody\.VerifyRetainedRoot\(root =>[\s\S]*?token\.RequireCreatedRoot\(root, path\);[\s\S]*?RequireCurrent\(\);[\s\S]*?outer\.BindPreparedPrivatePair\(inner, owner, ownerHandle, identity, root\);[\s\S]*?_ = profile.GetEnvironmentSnapshot\(token\);\s*RequireCurrent\(\);\s*outer\.RequirePreparedPrivateRoot\(inner, owner, ownerHandle, identity, root\);/);
    assert.match(job, /internal void BindPreparedPrivatePair[\s\S]*?assignmentExported \|\| peer.assignmentExported \|\| bound \|\| peer.bound[\s\S]*?privatePeer is not null \|\| peer.privatePeer is not null[\s\S]*?RequireContainmentLimits\(job, activeLimit\);\s*RequireContainmentLimits\(peer.job, peer.activeLimit\);\s*BindSingleCandidate\(owner, ownerHandle, root, rootHandle\);\s*peer\.BindSingleCandidate\(owner, ownerHandle, root, rootHandle\);\s*RequirePrivateNestedBinding\(peer, owner, ownerHandle, root, rootHandle\);\s*RequirePreparedPrivateRoot\(peer, owner, ownerHandle, root, rootHandle\);/);
    assert.match(job, /internal void RequireOrBindPreparedPrivatePair[\s\S]*?if \(privatePeer is not null \|\| peer.privatePeer is not null\)\s*RequirePreparedPrivateRoot\(peer, owner, ownerHandle, root, rootHandle\);\s*else\s*RequirePrivateNestedBinding\(peer, owner, ownerHandle, root, rootHandle\);/);
    assert.match(job, /!ReferenceEquals\(privateRoot, rootHandle\) \|\| !ReferenceEquals\(peer.privateRoot, rootHandle\)/);
    assert.match(session, /brokerJob\.RequireOrBindPreparedPrivatePair\(peerJob, owner, ownerHandle, root, rootHandle\);/);
    assert.match(fixture, /outer\.BindPreparedPrivatePair\(inner, ownerIdentity,[\s\S]*?outer\.RequireOrBindPreparedPrivatePair\(inner, ownerIdentity, owner.SafeHandle, identity, root\);[\s\S]*?aliasRefused[\s\S]*?custody\.TryDrainAndRelease\(5000\)/);
    assert.doesNotMatch(prep, /TakeVerified|ResumeRetainedRoot/);
});


test('prepared successor replaces occupied slot without transferring raw resource disposal ownership', async () => {
    const slot = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    const life = await readFile('apps/broker/Veil.VolumeBroker/CefPreparedCreationLifetime.cs', 'utf8');
    const session = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    assert.match(slot, /internal bool ReplacePrepared[\s\S]*?!ReferenceEquals\(generation, expectedGeneration\) \|\| !ReferenceEquals\(session, expected\)[\s\S]*?activating = true;[\s\S]*?var successor = build\(\);[\s\S]*?successor is null \|\| ReferenceEquals\(successor, expected\) \|\| retired[\s\S]*?session = successor;\s*committed = true;[\s\S]*?finally\s*\{\s*try \{ if \(!committed\) Retire\(\); \}\s*finally \{ activating = false; \}/);
    assert.match(life, /if \(!created \|\| retired \|\| creating \|\| handoffAttempted\)[\s\S]*?handoffAttempted = true;\s*creating = true;[\s\S]*?custody\.BuildRetainedSession[\s\S]*?if \(retired\)[\s\S]*?return successor;[\s\S]*?catch\s*\{\s*retired = true;/);
    assert.match(prep, /creation\.PrepareSuccessor[\s\S]*?outer\.RequirePreparedPrivateRoot[\s\S]*?new CefBoundDiagnosticSession[\s\S]*?borrowedCreation: creation\);\s*session\.RequirePreparedForHandoff\(\);/);
    assert.match(session, /if \(borrowedCreation is null\)\s*lifetime = new CefRetainedLifetime/);
    assert.match(session, /if \(borrowedCreation is not null\)\s*return guard\.RevokeAndCheckDrain\(\(\) => borrowedCreation\.RevokeAndRelease\(milliseconds\)\);/);
    assert.match(session, /if \(borrowedCreation is not null\)\s*throw new UnauthorizedAccessException\("CEF prepared successor activation is disabled\."\);/);
    assert.match(broker, /diagnosticSession\.ReplacePrepared\(expectedGeneration, probe,\s*\(\) => preparation\.PrepareBoundSession\(expectedGeneration, probe\), out var handoffFailure\)/);
    assert.match(fixture, /slot\.ReplacePrepared\(generation, probe,[\s\S]*?Synthetic successor construction refusal[\s\S]*?BorrowingFixtureSuccessor\(probe\)[\s\S]*?slot\.TryReleaseRetired\(5000\)/);
});


test('broker protected environment plan is explicit and disconnected from native creation', async () => {
    const plan = await readFile('apps/desktop/Engine/CefChildEnvironmentPlan.cs', 'utf8');
    const preparation = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const project = await readFile('apps/broker/Veil.VolumeBroker/Veil.VolumeBroker.csproj', 'utf8');
    const start = plan.indexOf('internal static IReadOnlyDictionary<string, string?> BuildBrokerProtected(');
    const end = plan.indexOf('internal static IReadOnlyDictionary<string, string?> Build(', start);
    assert.ok(start >= 0 && end > start);
    const explicit = plan.slice(start, end);
    assert.match(explicit, /RequireCanonicalLocalDirectory\(registeredProductRoot\);\s*RequireCanonicalLocalDirectory\(windowsDirectory\);[\s\S]*?Overlaps\(registeredProductRoot, windowsDirectory\)/);
    assert.match(explicit, /\["SystemRoot"\] = windowsDirectory,\s*\["WINDIR"\] = windowsDirectory,\s*\["TEMP"\] = layout.Scratch,\s*\["TMP"\] = layout.Scratch,\s*\[RootKey\] = layout.EngineRoot,\s*\[CacheKey\] = layout.Cache,\s*\[LogKey\] = layout.LogFile,/);
    assert.doesNotMatch(explicit, /GetEnvironmentVariable|SetEnvironmentVariable|Directory\.CreateDirectory|Process\.Start/);
    assert.match(project, /CefProfileLayout\.cs[\s\S]*?CefChildEnvironmentPlan\.cs/);
    assert.doesNotMatch(preparation + broker, /BuildBrokerProtected\(/);
});


test('disconnected broker directory pin verifies handle-derived volume and live protected state', async () => {
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedDirectoryLease.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    assert.match(pin, /!status.Attached \|\| !status.Unlocked \|\|[\s\S]*?!status.FullyEncrypted \|\| !status.ProtectionOn \|\| !status.ProtectorPresent/);
    assert.match(pin, /RequireRegisteredProtection\(registeredVolume, readStatus\(\)\);\s*var handle = OpenDirectory\(registeredPath\);[\s\S]*?RequireDirectory\(handle, registeredPath, registeredVolume\);\s*RequireRegisteredProtection\(registeredVolume, readStatus\(\)\);/);
    assert.match(pin, /CreateFileW\(path, 0x80, FileShare.ReadWrite, IntPtr.Zero,\s*FileMode.Open, 0x02000000 \| 0x00200000/);
    assert.match(pin, /FileAttributes.ReparsePoint[\s\S]*?FinalPath\(handle, 0\)[\s\S]*?FinalPath\(handle, 1\)[\s\S]*?string.Equals\(guid, volume \+ relative, StringComparison.OrdinalIgnoreCase\)/);
    assert.match(pin, /RequireRegisteredProtection\(volume, readStatus\(\)\);\s*RequireDirectory\(handle, path, volume\);\s*using var current = OpenDirectory\(path\);[\s\S]*?original.IndexHigh != observed.IndexHigh \|\| original.IndexLow != observed.IndexLow[\s\S]*?RequireRegisteredProtection\(volume, readStatus\(\)\);/);
    assert.doesNotMatch(pin, /Directory\.CreateDirectory|File\.Delete|Process\.Start/);
    assert.doesNotMatch(broker + prep, /CefProtectedDirectoryLease/);
});


test('protected profile lease pins ancestors and all fixed directories before exposing environment', async () => {
    const profile = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedProfileLease.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    assert.match(profile, /RequireAuthority\(registeredVolume, readStatus, authorityCurrent\);\s*var environment = CefChildEnvironmentPlan.BuildBrokerProtected[\s\S]*?ancestors.Push\(path\);[\s\S]*?foreach \(string path in ancestors\)[\s\S]*?pins.Add\(CefProtectedDirectoryLease.Open\(path, registeredVolume, readStatus\)\);[\s\S]*?foreach \(string path in layout.Directories\)[\s\S]*?pins.Add\(CefProtectedDirectoryLease.Open\(path, registeredVolume, readStatus\)\);[\s\S]*?lease.RequireCurrent\(\);\s*return lease;/);
    assert.match(profile, /catch\s*\{\s*\/\/[^\n]*\s*\/\/[^\n]*\s*try \{ log\?\.Dispose\(\); \}\s*finally\s*\{\s*try\s*\{\s*for \(int i = pins.Count - 1; i >= 0; --i\) pins\[i\].Dispose\(\);\s*\}\s*finally \{ exclusive\?\.Dispose\(\); \}\s*\}\s*throw;/);
    assert.match(profile, /if \(!authorityCurrent\(\)\)[\s\S]*?CefProtectedDirectoryLease.RequireRegisteredProtection\(volume, readStatus\(\)\);[\s\S]*?if \(!authorityCurrent\(\)\)/);
    assert.match(profile, /if \(retired\)[\s\S]*?RequireAuthority\(volume, readStatus, authorityCurrent\);\s*foreach \(var directory in directories\) directory.RequireCurrent\(readStatus\);\s*profileLock\?\.RequireCurrent\(readStatus\);\s*RequireLog\(\);\s*InspectExistingTree\(\);\s*RequireLog\(\);\s*profileLock\?\.RequireCurrent\(readStatus\);\s*RequireAuthority\(volume, readStatus, authorityCurrent\);[\s\S]*?catch\s*\{\s*retired = true;\s*throw;/);
    assert.match(profile, /try \{ _ = File.GetAttributes\(path\); return true; \}\s*catch \(FileNotFoundException\) \{ return false; \}/);
    assert.match(profile, /if \(log is not null\)\s*log.RequireCurrent\(readStatus\);\s*else if \(LogExists\(layout.LogFile\)\)\s*throw new UnauthorizedAccessException/);
    assert.match(profile, /VerifiedEnvironment\(\)\s*\{\s*RequireCurrent\(\);\s*return environment;/);
    assert.doesNotMatch(profile, /Directory\.CreateDirectory|File\.Write|Process\.Start|ResumeRetainedRoot/);
    assert.doesNotMatch(broker + prep, /CefProtectedProfileLease|VerifiedEnvironment\(/);
});


test('existing CEF log is pinned and descendant snapshot rejects redirected or multiply-linked files', async () => {
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedDirectoryLease.cs', 'utf8');
    const profile = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedProfileLease.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(pin, /internal static RegularFilePin OpenRegularFile[\s\S]*?var handle = OpenDirectory\(path\);[\s\S]*?RequireRegularFile\(handle, path, volume\);/);
    assert.match(pin, /private static void RequireRegularFile[\s\S]*?FileAttributes.Directory \| \(uint\)FileAttributes.ReparsePoint[\s\S]*?info.Links != 1[\s\S]*?FinalPath\(handle, 0\)[\s\S]*?FinalPath\(handle, 1\)/);
    assert.match(pin, /internal sealed class RegularFilePin[\s\S]*?RequireRegularFile\(handle, path, volume\);[\s\S]*?RequireRegularFile\(current, path, volume\);[\s\S]*?original.IndexHigh != observed.IndexHigh \|\| original.IndexLow != observed.IndexLow/);
    assert.match(profile, /if \(LogExists\(layout.LogFile\)\)\s*log = CefProtectedDirectoryLease.OpenRegularFile/);
    assert.match(profile, /AttributesToSkip = 0,\s*IgnoreInaccessible = false,\s*RecurseSubdirectories = false/);
    assert.match(profile, /if \(depth > 64\)[\s\S]*?using var directory = CefProtectedDirectoryLease.Open[\s\S]*?if \(\+\+visited > 100000\)[\s\S]*?FileAttributes.ReparsePoint[\s\S]*?Inspect\(entry, depth \+ 1\);[\s\S]*?using var file = CefProtectedDirectoryLease.OpenRegularFile/);
    assert.match(fixture, /CreateHardLinkW\(alias, payload, IntPtr.Zero\)[\s\S]*?Reject\(\(\) => profile.RequireCurrent\(\)\);[\s\S]*?CreateHardLinkW\(alias, profileLayout.LogFile, IntPtr.Zero\)/);
    assert.doesNotMatch(profile, /File\.Write|Directory\.CreateDirectory|Process\.Start/);
});


test('broker profile authority reads fixed installed client registration and pins it through verification', async () => {
    const registered = await readFile('apps/broker/Veil.VolumeBroker/CefRegisteredProfileLease.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    assert.match(registered, /Environment.SpecialFolder.ProgramFiles[\s\S]*?"VEIL", "Broker", "Veil.VolumeBroker.exe"[\s\S]*?Environment.ProcessPath, expectedBroker/);
    assert.match(registered, /Environment.SpecialFolder.CommonApplicationData[\s\S]*?"VEIL", "client.json"[\s\S]*?AdminOnlyFile.RequireProtectedExecutable\(path\);\s*var registration = new FileStream\(path, FileMode.Open, FileAccess.Read, FileShare.Read\)/);
    assert.match(registered, /byte\[\] original = ReadPinned\(registration, path\);\s*string root = Parse\(original, volumeId, pipeName, expectedBroker, windows\);[\s\S]*?profile = CefProtectedProfileLease.OpenExclusive\(root, volumeGuid, windows, readStatus, authorityCurrent\)/);
    assert.match(registered, /!remaining.Remove\(property.Name\)[\s\S]*?TextEquals\(root.GetProperty\("volumeId"\), volumeId, StringComparison.Ordinal\)[\s\S]*?TextEquals\(root.GetProperty\("pipeName"\), pipeName, StringComparison.Ordinal\)[\s\S]*?TextEquals\(root.GetProperty\("brokerImage"\), expectedBroker, StringComparison.OrdinalIgnoreCase\)[\s\S]*?JsonValueKind.True[\s\S]*?JsonValueKind.False/);
    assert.match(registered, /AdminOnlyFile.RequireProtectedExecutable\(path\);\s*Native.RequireCanonicalSingleLinkImage\(file.SafeFileHandle, path\);[\s\S]*?file.ReadExactly\(bytes\);[\s\S]*?AdminOnlyFile.RequireProtectedExecutable\(path\);\s*Native.RequireCanonicalSingleLinkImage\(file.SafeFileHandle, path\);/);
    assert.match(registered, /ReadPinned\(registration, registrationPath\).AsSpan\(\).SequenceEqual\(original\)[\s\S]*?profile.RequireCurrent\(\);[\s\S]*?ReadPinned\(registration, registrationPath\).AsSpan\(\).SequenceEqual\(original\)[\s\S]*?retired = true;/);
    const preparation = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    assert.match(broker, /CandidateAuthorityCurrent\(expectedGeneration\)[\s\S]*?CefBrokerLaunchPreparation.Open\(image, digest, owner, ownerHandle,\s*AuthorityCurrent, baselinePayload, baselineSignature,\s*config.VolumeId, config.VolumeGuid, config.PipeName, backend.Status\)/);
    assert.match(preparation, /profile = CefRegisteredProfileLease.OpenInstalled\(volumeId, volumeGuid, pipeName,\s*readStatus, authorityCurrent\)/);
    assert.doesNotMatch(broker, /PrepareRegisteredProfile\(/);
    assert.doesNotMatch(protocol, /PrepareRegisteredProfile|CefRegisteredProfileLease/);
    assert.doesNotMatch(registered, /GetEnvironmentVariable|Process\.Start|CreateSuspended|ResumeRetainedRoot/);
});


test('installed broker acquires existing exclusive profile lock without takeover or mutation', async () => {
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedDirectoryLease.cs', 'utf8');
    const profile = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedProfileLease.cs', 'utf8');
    const registration = await readFile('apps/broker/Veil.VolumeBroker/CefRegisteredProfileLease.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(pin, /OpenExclusiveProfileLock\(string productRoot,[\s\S]*?Path.Combine\(productRoot, "profile.lock"\);[\s\S]*?CreateFileW\(path, 0xC0000000, FileShare.None, IntPtr.Zero,\s*FileMode.Open, 0x00200000, IntPtr.Zero\);[\s\S]*?RequireRegularFile\(handle, path, volume\);[\s\S]*?return new ExclusiveProfileLock/);
    const owner = pin.slice(pin.indexOf('internal sealed class ExclusiveProfileLock'), pin.indexOf('private static string FinalPath'));
    assert.match(owner, /handle.IsClosed \|\| handle.IsInvalid[\s\S]*?RequireRegisteredProtection\(volume, readStatus\(\)\);\s*RequireRegularFile\(handle, path, volume\);\s*RequireRegisteredProtection\(volume, readStatus\(\)\);/);
    assert.doesNotMatch(owner, /CreateFileW|OpenDirectory|OpenRegularFile/);
    assert.match(profile, /AcquireExclusiveLock\(\)\s*\{\s*RequireCurrent\(\);[\s\S]*?profileLock = CefProtectedDirectoryLease.OpenExclusiveProfileLock\(\s*layout.ProductRoot, volume, readStatus\);\s*RequireCurrent\(\);[\s\S]*?retired = true;/);
    assert.match(profile, /retired = disposed = true;[\s\S]*?try \{ log\?\.Dispose\(\); \}\s*finally\s*\{\s*try\s*\{\s*for \(int i = directories.Count - 1; i >= 0; --i\) directories\[i\].Dispose\(\);\s*\}\s*finally \{ profileLock\?\.Dispose\(\); \}/);
    assert.match(registration, /profile = CefProtectedProfileLease.OpenExclusive\(root, volumeGuid, windows, readStatus, authorityCurrent\);\s*var lease = new CefRegisteredProfileLease/);
    assert.match(fixture, /missingLock.AcquireExclusiveLock\(\)[\s\S]*?desktopLock = new FileStream\(profileLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None\)[\s\S]*?owned.AcquireExclusiveLock\(\);[\s\S]*?CreateHardLinkW\(lockAlias, profileLockPath, IntPtr.Zero\)/);
});


test('registered environment requires the broker exclusive lock and separate process contention retires preparation', async () => {
    const profile = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedProfileLease.cs', 'utf8');
    const registration = await readFile('apps/broker/Veil.VolumeBroker/CefRegisteredProfileLease.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(profile, /VerifiedExclusiveEnvironment\(\)\s*\{\s*ObjectDisposedException.ThrowIf\(disposed, this\);\s*if \(profileLock is null\)\s*throw new UnauthorizedAccessException[\s\S]*?return VerifiedEnvironment\(\);/);
    assert.match(registration, /GetEnvironmentSnapshot\(\s*CefProcessTokenObserver.LaunchTokenLease token\)\s*\{[\s\S]*?RequireCurrent\(\);\s*try\s*\{\s*token.RequireProfileAccess\(profile\);\s*var environment = profile.VerifiedExclusiveEnvironment\(\);\s*RequireCurrent\(\);\s*token.RequireCurrent\(\);/);
    assert.match(fixture, /string lockAlias = Path.Combine\(profileFixture, "profile-lock-alias"\);\s*Check\(CreateHardLinkW\(lockAlias, profileLockPath, IntPtr.Zero\)/);
    assert.match(fixture, /args is \["--profile-lock-fixture", var lockPath\][\s\S]*?new FileStream\(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None\)/);
    assert.match(fixture, /ArgumentList = \{ "--profile-lock-fixture", profileLockPath \}[\s\S]*?Reject\(\(\) => refused.AcquireExclusiveLock\(\)\);[\s\S]*?lockOwner.WaitForExit\(10000\)[\s\S]*?Reject\(\(\) => refused.AcquireExclusiveLock\(\)\);[\s\S]*?fresh.AcquireExclusiveLock\(\);[\s\S]*?fresh.VerifiedExclusiveEnvironment\(\)/);
});


test('registered CEF environment requires actual user-token filesystem access without service fallback', async () => {
    const observer = await readFile('apps/broker/Veil.VolumeBroker/CefProcessTokenObserver.cs', 'utf8');
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedDirectoryLease.cs', 'utf8');
    const profile = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedProfileLease.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(observer, /RequireProfileAccess\(CefProtectedProfileLease profile\)[\s\S]*?RequireCurrent\(\);\s*if \(!DuplicateTokenEx\(duplicate, 0x000C, IntPtr.Zero, 2, 2, out var access\)\)[\s\S]*?ReadTokenSnapshot\(access, sourceSnapshot.Token.Identity, true, impersonation: true\);[\s\S]*?CefLaunchTokenPolicy.RequireDuplicate\(duplicateSnapshot, snapshot\);\s*RequireCurrent\(\);\s*profile.RequireUserAccess\(access\);[\s\S]*?CefLaunchTokenPolicy.RequireSame\(snapshot,[\s\S]*?RequireCurrent\(\);/);
    assert.match(observer, /before.TokenType != \(impersonation \? 2 : 1\)[\s\S]*?before.ImpersonationLevel != 2/);
    assert.match(pin, /OpenAccess\(path, writable \? 0xA7u : 0xA0u\);\s*RequireDirectory\(observed, path, volume\);\s*RequireSameIdentity\(handle, observed\);/);
    assert.match(pin, /CreateFileW\(path, rights, FileShare.ReadWrite, IntPtr.Zero,\s*FileMode.Open, 0x02000000 \| 0x00200000/);
    assert.match(pin, /OpenAccess\(path, 0x87\);\s*RequireRegularFile\(observed, path, volume\);\s*RequireSameIdentity\(handle, observed\);/);
    // Scope this check to the outer profile method, not the library inspection's
    // similarly named method. Both impersonation scopes have independent contracts.
    const methodStart = profile.indexOf('\n    internal void RequireUserAccess(SafeAccessTokenHandle access)');
    assert.ok(methodStart >= 0, 'outer profile access method missing');
    const profileMethod = profile.slice(methodStart, profile.indexOf('\n    public void Dispose()', methodStart));
    const impersonationStart = profileMethod.indexOf('WindowsIdentity.RunImpersonated(access,');
    assert.ok(impersonationStart >= 0, 'outer profile impersonation missing');
    const scope = profileMethod.slice(impersonationStart, profileMethod.indexOf('});', impersonationStart) + 3);
    assert.match(scope, /directories\[i\].RequireUserAccess\(i >= writableStart\);[\s\S]*?log\?\.RequireUserAccess\(\);/);
    assert.doesNotMatch(scope, /readStatus|authorityCurrent|RequireCurrent\(|File\.Write|CreateDirectory/);
    assert.match(profile, /RequireUserAccess\(SafeAccessTokenHandle access\)[\s\S]*?RequireCurrent\(\);[\s\S]*?profileLock is null[\s\S]*?WindowsIdentity.RunImpersonated[\s\S]*?RequireCurrent\(\);[\s\S]*?catch\s*\{\s*retired = true;/);
    assert.match(fixture, /FileSystemRights.CreateFiles,\s*AccessControlType.Deny[\s\S]*?scratch.SetAccessControl\(deniedAcl\);\s*Reject\(\(\) => deniedProfile.RequireUserAccess\(fixtureUser.AccessToken\)\);[\s\S]*?scratch.SetAccessControl\(originalAcl\)/);
});


test('suspended broker creation owns the registered profile and uses only its token-verified environment', async () => {
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const lifetime = await readFile('apps/broker/Veil.VolumeBroker/CefPreparedCreationLifetime.cs', 'utf8');
    const session = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(prep, /private readonly CefRegisteredProfileLease profile;/);
    assert.match(prep, /token = CefProcessTokenObserver.LaunchTokenLease.Acquire\(ownerHandle, owner\);[\s\S]*?profile = CefRegisteredProfileLease.OpenInstalled\(volumeId, volumeGuid, pipeName,\s*readStatus, authorityCurrent\);[\s\S]*?registration, image, token, verifier, owner, ownerHandle, profile\);\s*prepared.RequireCurrent\(\);/);
    assert.match(prep, /catch\s*\{[\s\S]*?try \{ profile\?\.Dispose\(\); \}[\s\S]*?try \{ token\?\.Dispose\(\); \}[\s\S]*?registration\?\.Dispose\(\)/);
    assert.match(prep, /token.RequireCurrent\(\);\s*profile.RequireCurrent\(\);\s*token.RequireCurrent\(\);\s*if \(!authorityCurrent\(\)\)/);
    assert.match(prep, /creationAttempted = true;\s*RequireCurrent\(\);\s*var environment = profile.GetEnvironmentSnapshot\(token\);[\s\S]*?RequireCurrent\(\);\s*outer.CreateRetainedDiagnosticPair\(inner, token, path, environment, custody\);/);
    assert.equal((prep.match(/profile.GetEnvironmentSnapshot\(token\)/g) ?? []).length, 4);
    assert.match(prep, /outer.RequirePreparedPrivateRoot\(inner, owner, ownerHandle, identity, root\);\s*RequireCurrent\(\);\s*\}\);/);
    assert.match(prep, /session.RequirePreparedForHandoff\(\);[\s\S]*?_ = profile.GetEnvironmentSnapshot\(token\);\s*RequireCurrent\(\);\s*outer.RequirePreparedPrivateRoot\(inner, owner, ownerHandle, identity, root\);\s*RequireCurrent\(\);\s*return session;/);
    assert.doesNotMatch(prep, /GetEnvironmentVariables|new Dictionary<string, string\?>|ResumeThread|ResumeRetainedRoot|TakeVerified/);
    assert.match(prep, /public void Dispose\(\)[\s\S]*?try \{ profile.Dispose\(\); \}[\s\S]*?try \{ token.Dispose\(\); \}[\s\S]*?registration.Dispose\(\)/);
    assert.match(broker, /new CefPreparedCreationLifetime\(outer, inner, preparation,\s*preparation.CreateDiagnosticCandidate\);[\s\S]*?diagnosticSession.RegisterAndActivate/);
    assert.match(lifetime, /lifetime = new CefRetainedLifetime\(prerequisites, inner, outer\)/);
    assert.match(lifetime, /return lifetime\.TryRelease\(\(\) => TryDrainAll\(milliseconds,\s*custody\.TryDrainAndRelease, outer\.TerminateAndDrain, inner\.TerminateAndDrain,\s*\(\) => outer\.HasNoActiveProcesses\(\) && inner\.HasNoActiveProcesses\(\)\)\);/);
    assert.match(session, /if \(borrowedCreation is not null\)\s*throw new UnauthorizedAccessException\("CEF prepared successor activation is disabled\."\);/);
    assert.equal((broker.match(/RunDiagnosticCreationProbe\(/g) ?? []).length, 2);
    assert.match(fixture, /var retained = new CefRetainedLifetime\(retainedProfile\);\s*Check\(!retained.TryRelease\(\(\) => false\)[\s\S]*?Reject\(\(\) => competing.AcquireExclusiveLock\(\)\);[\s\S]*?retained.TryRelease\(\(\) => true\)[\s\S]*?next.AcquireExclusiveLock\(\);/);
});


test('prepared drain independently attempts every private stop before releasing protected prerequisites', async () => {
    const life = await readFile('apps/broker/Veil.VolumeBroker/CefPreparedCreationLifetime.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    assert.match(life, /try \{ return drain\(Remaining\(\)\); \}[\s\S]*?failure \?\?= error;\s*return false;/);
    assert.match(life, /bool custodyDrained = Attempt\(drainCustody\);\s*bool outerDrained = Attempt\(drainOuter\);\s*bool innerDrained = Attempt\(drainInner\);\s*if \(failure is not null\)[\s\S]*?return custodyDrained && outerDrained && innerDrained && verifyEmpty\(\);/);
    assert.match(fixture, /foreach \(int failedStage in new\[\] \{ 0, 1, 2, 3 \}\)[\s\S]*?retained.TryRelease\(Drain\);[\s\S]*?!released && pin.CanRead[\s\S]*?retried = true;\s*Check\(retained.TryRelease\(Drain\) && !pin.CanRead/);
});


test('broker retry worker preserves relock obligations and resets its flag under serial', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const policy = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    const worker = broker.slice(broker.indexOf('private async Task RetryCefStopsAsync()'), broker.indexOf('private bool RefreshAuthenticatedOwner('));
    assert.match(worker, /await serial.WaitAsync\(\);\s*bool continueRetry = false;\s*try[\s\S]*?CefStopRetryPolicy.TryAttempt\(RetryUnresolvedCefStops,[\s\S]*?AttemptUnconfirmed[\s\S]*?CefStopRetryPolicy.ShouldContinue\(attempt, 30, pending, failure\)/);
    assert.match(worker, /finally\s*\{[\s\S]*?if \(!continueRetry\) cefStopRetryRunning = false;\s*serial.Release\(\);/);
    assert.match(policy, /internal static Exception\? TryAttempt\(Action cleanup, Action relock\)[\s\S]*?try\s*\{\s*cleanup\(\);\s*relock\(\);\s*return null;\s*\}\s*catch \(Exception error\) when \(error is not OutOfMemoryException\)\s*\{\s*return error;/);
    assert.match(policy, /return attempt < maximumAttempts - 1 && \(pending \|\| failure is not null\);/);
    assert.match(fixture, /foreach \(int failedPhase in new\[\] \{ 0, 1 \}\)[\s\S]*?ReferenceEquals\(failure, injected\) && obligation.Pending[\s\S]*?CefStopRetryPolicy.ShouldContinue\(29, 30, obligation.Pending, failure\)[\s\S]*?retried = true;[\s\S]*?!obligation.Pending/);
});


test('owner revocation attempts all independent cleanup phases and preserves the first recoverable failure', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const policy = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    const clear = broker.slice(broker.indexOf('private void ClearAuthenticatedOwner()'), broker.indexOf('private void RevokePendingCefChild()'));
    assert.match(clear, /diagnosticRelock\.Request\(unlockOwner\);[\s\S]*?authenticatedGeneration = null;\s*authenticatedOwner = null;\s*authenticatedOwnerHandle = null;\s*CefOwnerRevocationCleanup.Run\(diagnosticSession\.Retire, RevokePendingCefChild,\s*RetryDiagnosticSession,/);
    assert.match(clear, /if \(unresolvedCefStops\.Count != 0 \|\| diagnosticSession\.CleanupPending \|\| diagnosticRelock\.Pending\)\s*EnsureCefStopRetry\(\);/);
    assert.match(policy, /internal static class CefOwnerRevocationCleanup[\s\S]*?catch \(Exception error\) when \(error is not OutOfMemoryException\)[\s\S]*?failure \?\?= error;[\s\S]*?Attempt\(retire\);\s*Attempt\(revokePending\);\s*Attempt\(retry\);\s*Attempt\(schedule\);\s*if \(failure is not null\)\s*System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture\(failure\).Throw\(\);/);
    assert.match(fixture, /foreach \(int failedPhase in new\[\] \{ 0, 1, 2, 3, 4 \}\)[\s\S]*?phases.SequenceEqual\(new\[\] \{ 0, 1, 2, 3 \}\)[\s\S]*?slot.CleanupPending && slot.Current\(generation\) is null[\s\S]*?candidate.AllowRelease = true;[\s\S]*?slot.TryReleaseRetired\(0\) && obligation.TryComplete/);
});


test('installed broker takes existing exclusive profile lock before inspecting CEF descendants', async () => {
    const profile = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedProfileLease.cs', 'utf8');
    const registration = await readFile('apps/broker/Veil.VolumeBroker/CefRegisteredProfileLease.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(profile, /internal static CefProtectedProfileLease OpenExclusive[\s\S]*?OpenCore\(registeredProductRoot, registeredVolume, windowsDirectory, readStatus, authorityCurrent, true\)/);
    assert.match(profile, /foreach \(string path in ancestors\)[\s\S]*?if \(acquireExclusive\)[\s\S]*?exclusive = CefProtectedDirectoryLease.OpenExclusiveProfileLock\(\s*layout.ProductRoot, registeredVolume, readStatus\);[\s\S]*?foreach \(string path in layout.Directories\)[\s\S]*?if \(LogExists\(layout.LogFile\)\)[\s\S]*?lease.profileLock = exclusive;\s*lease.RequireCurrent\(\);\s*return lease;/);
    assert.match(profile, /catch\s*\{[\s\S]*?try \{ log\?\.Dispose\(\); \}[\s\S]*?finally \{ exclusive\?\.Dispose\(\); \}[\s\S]*?throw;/);
    assert.match(registration, /profile = CefProtectedProfileLease.OpenExclusive\(root, volumeGuid, windows, readStatus, authorityCurrent\);/);
    assert.doesNotMatch(registration, /profile.AcquireExclusiveLock\(\)/);
    assert.match(fixture, /Directory.Delete\(profileLayout.Cache\);\s*using \(var busy = new FileStream[\s\S]*?error.NativeErrorCode == 32[\s\S]*?Check\(incompleteRefused && !Directory.Exists\(profileLayout.Cache\)[\s\S]*?using \(var recovered = new FileStream[\s\S]*?Directory.CreateDirectory\(profileLayout.Cache\);[\s\S]*?first.VerifiedExclusiveEnvironment\(\).Count == 7/);
});


test('isolated profile provisioning is terminal and cannot count as candidate verification', async () => {
    const entry = await readFile('apps/desktop/Engine/CefProtectedPreflightEntry.cs', 'utf8');
    const session = await readFile('apps/desktop/ProtectedProfileSession.cs', 'utf8');
    const program = await readFile('apps/desktop/Program.cs', 'utf8');
    const provision = session.slice(session.indexOf('internal void PrepareLayoutOnly()'), session.indexOf('internal IReadOnlyDictionary<string, string?> PrepareChildEnvironment()'));
    assert.match(session, /#if VEIL_CEF_PREFLIGHT_ONLY\s*\/\/[^\n]*\s*\/\/[^\n]*\s*internal void PrepareLayoutOnly\(\)\s*\{\s*RequireCurrentPreparation\(\);\s*_ = CefProtectedProfilePreparation\.Prepare\(ProductRoot, session\.broker, StillAuthorized\);\s*RequireCurrentPreparation\(\);\s*\}\s*#endif/);
    assert.doesNotMatch(provision, /PrepareRegisteredRuntime|StageSuspendedChild|GetEnvironmentVariables|CreateSuspended|Resume/);
    assert.match(entry, /internal const uint ProfilePreparedOnly = 30;[\s\S]*?uint expected = brokerProbe \? SuspendedProbeVerified :\s*prepareOnly \? ProfilePreparedOnly : CefDisabledPreflight\.VerifiedButDisabled;/);
    assert.match(entry, /if \(prepareOnly\)\s*\{[\s\S]*?using \(var profile = session\.AcquireCefPreparationLease\(\)\)\s*profile\.PrepareLayoutOnly\(\);\s*result = ProfilePreparedOnly;\s*\}\s*else\s*\{\s*result = CefDisabledLaunchCoordinator\.RunPreflight\(session\);\s*\}/);
    assert.match(entry, /if \(result != expected \|\| notifications\.LockRequested\) return 1;\s*if \(!brokerProbe\) session\.CompleteCefPreflightCleanup\(\);[\s\S]*?return !notifications\.LockRequested && result == expected\s*\? \(int\)expected : 1;/);
    assert.doesNotMatch(program, /--cef-prepare-profile|PrepareLayoutOnly|ProfilePreparedOnly/);
});


test('broker verifies fresh user profile access before accepting a creation slot', async () => {
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    const open = prep.slice(prep.indexOf('internal static CefBrokerLaunchPreparation Open('), prep.indexOf('internal void RequireCurrent()'));
    assert.match(open, /token = CefProcessTokenObserver.LaunchTokenLease.Acquire\(ownerHandle, owner\);[\s\S]*?profile = CefRegisteredProfileLease.OpenInstalled[\s\S]*?prepared.RequireCurrent\(\);[\s\S]*?_ = profile.GetEnvironmentSnapshot\(token\);\s*prepared.RequireCurrent\(\);\s*return prepared;/);
    assert.match(open, /catch\s*\{[\s\S]*?try \{ profile\?\.Dispose\(\); \}[\s\S]*?try \{ token\?\.Dispose\(\); \}[\s\S]*?try \{ image\?\.Dispose\(\); \}[\s\S]*?finally \{ registration\?\.Dispose\(\); \}/);
    assert.match(broker, /preparation = PrepareDiagnosticLaunch\(expectedGeneration, baselinePayload, baselineSignature\);\s*outer = CefBrokerOwnedJob.CreateDisabledCandidate\(\);\s*inner = CefBrokerOwnedJob.CreateDisabledCandidate\(\);[\s\S]*?diagnosticSession.RegisterAndActivate/);
    assert.match(fixture, /using \(var retiredProfile = CefProtectedProfileLease.OpenExclusive[\s\S]*?authority = false;\s*Reject\(\(\) => retiredProfile.VerifiedExclusiveEnvironment\(\)\);\s*authority = true;\s*Reject\(\(\) => retiredProfile.VerifiedExclusiveEnvironment\(\)\);[\s\S]*?using var refused = CefProtectedProfileLease.OpenExclusive[\s\S]*?using \(var freshProfile = CefProtectedProfileLease.OpenExclusive[\s\S]*?freshProfile.VerifiedExclusiveEnvironment\(\).Count == 7/);
});


test('terminal broker probe latches relock after pre-creation refusal without touching stale owners', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const policy = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    const fixture = await readFile('tests/security/CefContainmentCheck/Program.cs', 'utf8');
    const probe = broker.slice(broker.indexOf('private void RunDiagnosticCreationProbe('), broker.indexOf('private CefDiagnosticActivationOutcome TryRegisterDiagnosticSession('));
    assert.match(probe, /if \(config.Development \|\| diagnosticRelock\.Pending \|\| diagnosticSession\.Occupied \|\|[\s\S]*?authenticatedGeneration is null \|\|\s*!ReferenceEquals\(unlockOwner, expectedGeneration\)\)\s*throw new UnauthorizedAccessException[\s\S]*?preparation = PrepareDiagnosticLaunch/);
    assert.match(probe, /if \(!accepted\)[\s\S]*?finally \{ preparation\?\.Dispose\(\); \}[\s\S]*?finally\s*\{[\s\S]*?if \(diagnosticRelock\.RequestIfCurrent\(expectedGeneration, unlockOwner\)\)\s*\{\s*try \{ ClearAuthenticatedOwner\(\); \}\s*finally \{ EnsureCefStopRetry\(\); \}/);
    assert.match(policy, /internal bool RequestIfCurrent\(object expectedGeneration, object\? currentGeneration\)\s*\{[\s\S]*?if \(!ReferenceEquals\(expectedGeneration, currentGeneration\)\) return false;\s*Request\(expectedGeneration\);\s*return ReferenceEquals\(generation, expectedGeneration\);/);
    assert.match(fixture, /!terminalRelock\.RequestIfCurrent\(staleGeneration, currentGeneration\)[\s\S]*?terminalRelock\.RequestIfCurrent\(currentGeneration, currentGeneration\)[\s\S]*?!terminalRelock\.RequestIfCurrent\(staleGeneration, staleGeneration\)[\s\S]*?lockAttempts == 0[\s\S]*?terminalRelock.Pending && lockAttempts == 1[\s\S]*?!terminalRelock.Pending && lockAttempts == 2/);
});


test('terminal diagnostic obtains only fixed installed baseline input before independent trust verification', async () => {
    const signed = await readFile('apps/broker/Veil.VolumeBroker/CefSignedSystemModuleBaseline.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const prep = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerLaunchPreparation.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/CefSignedSystemModuleBaselineTests.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    assert.match(signed, /ReadInstalledDiagnosticInputs\(\)\s*\{[\s\S]*?SpecialFolder.ProgramFiles[\s\S]*?"VEIL", "Broker"[\s\S]*?Environment.ProcessPath[\s\S]*?"Veil.VolumeBroker.exe"[\s\S]*?"cef-system-baseline.json"[\s\S]*?"cef-system-baseline.sig"[\s\S]*?using var payload = OpenPinnedInput\(payloadPath\);\s*using var signature = OpenPinnedInput\(signaturePath\);[\s\S]*?ReadBoundedInput\(payload, 1, MaxPayloadBytes\)[\s\S]*?ReadBoundedInput\(signature, 384, 512\)[\s\S]*?detached.Length is not \(384 or 512\)[\s\S]*?RequireInputPin\(payload, payloadPath\);\s*RequireInputPin\(signature, signaturePath\);/);
    assert.match(signed, /OpenPinnedInput\(string path\)\s*\{\s*AdminOnlyFile.RequireProtectedExecutable\(path\);\s*var file = new FileStream\(path, FileMode.Open, FileAccess.Read, FileShare.Read\);[\s\S]*?RequireInputPin\(file, path\);[\s\S]*?catch \{ file.Dispose\(\); throw; \}/);
    assert.match(signed, /RequireInputPin\(FileStream file, string path\)\s*\{\s*AdminOnlyFile.RequireProtectedExecutable\(path\);\s*Native.RequireCanonicalSingleLinkImage\(file.SafeFileHandle, path\);/);
    assert.match(signed, /long length = input.Length;\s*if \(length < minimum \|\| length > maximum\)[\s\S]*?input.Position = 0;\s*byte\[\] bytes = new byte\[checked\(\(int\)length\)\];\s*input.ReadExactly\(bytes\);\s*if \(input.Length != length \|\| input.ReadByte\(\) != -1\)/);
    assert.match(broker, /private void RunDiagnosticCreationProbe\(object expectedGeneration\)[\s\S]*?try\s*\{\s*var \(baselinePayload, baselineSignature\) = CefSignedSystemModuleBaseline.ReadInstalledDiagnosticInputs\(\);\s*preparation = PrepareDiagnosticLaunch\(expectedGeneration, baselinePayload, baselineSignature\);/);
    assert.match(prep, /VerifyInstalledTrust\(baselinePayload, baselineSignature\);\s*verifier.RequireBoundTrust\(\);/);
    assert.match(fixture, /ReadBoundedInput\(input, 1, 1024 \* 1024\)[\s\S]*?Verify\(snapshot, signature, publicKey, host, 7\)[\s\S]*?new\[\] \{ 0, 1024 \* 1024 \+ 1 \}[\s\S]*?new\[\] \{ 383, 513 \}/);
    assert.doesNotMatch(protocol, /ReadInstalledDiagnosticInputs|RunDiagnosticCreationProbe|baselinePayload|baselineSignature/);
});


test('suspended broker diagnostic IPC exists only in separate build and requires verified relock', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const protocol = await readFile('apps/broker/Veil.VolumeBroker/Protocol.cs', 'utf8');
    const project = await readFile('apps/broker/Veil.VolumeBroker/Veil.VolumeBroker.csproj', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    const testProject = await readFile('tests/security/CefProcessBindingCheck/CefProcessBindingCheck.csproj', 'utf8');
    for (const source of [project, testProject])
        assert.match(source, /<PropertyGroup Condition="'\$\(Configuration\)' == 'CefDiagnostic'">\s*<DefineConstants>\$\(DefineConstants\);VEIL_CEF_DIAGNOSTIC_IPC<\/DefineConstants>/);
    assert.match(protocol, /#if VEIL_CEF_DIAGNOSTIC_IPC\s*ProbeCefSuspended, CheckCefRuntimeAuthority,\s*#endif/);
    assert.match(broker, /#if VEIL_CEF_DIAGNOSTIC_IPC\s*case BrokerCommand.ProbeCefSuspended:[\s\S]*?config.Development \|\| config.Backend != "bitlocker"[\s\S]*?authenticatedOwner != client[\s\S]*?Native.SameLiveProcess\(probeOwner, pipeClient\)[\s\S]*?diagnosticSession.Occupied \|\| diagnosticRelock.Pending[\s\S]*?RunDiagnosticCreationProbe\(probeGeneration\);\s*if \(!diagnosticRelock.Pending \|\| !TryCompleteDiagnosticRelock\(\)\)[\s\S]*?status = backend.Status\(\);\s*if \(!Registered\(status\) \|\| status.Unlocked \|\| !status.ReadyToUnlock\)[\s\S]*?return Protocol.CefSuspendedProbeVerified\(status\);[\s\S]*?#endif/);
    assert.match(protocol, /#if VEIL_CEF_DIAGNOSTIC_IPC(?:(?!#endif)[\s\S])*?internal static byte\[\] CefSuspendedProbeVerified[\s\S]*?status.Unlocked \|\| !status.ReadyToUnlock[\s\S]*?cefSuspendedProbeVerified = true, volumeGuid = status.VolumeGuid[\s\S]*?#endif/);
    assert.match(fixture, /#if VEIL_CEF_DIAGNOSTIC_IPC[\s\S]*?probe.Command == BrokerCommand.ProbeCefSuspended[\s\S]*?#else\s*RejectProbe\(probeRequest\);\s*#endif/);
});


test('desktop suspended probe is one-shot, broker-owned, and requires exact terminal receipt and cleanup', async () => {
    const entry = await readFile('apps/desktop/Engine/CefProtectedPreflightEntry.cs', 'utf8');
    const session = await readFile('apps/desktop/ProtectedProfileSession.cs', 'utf8');
    const client = await readFile('apps/desktop/BrokerClient.cs', 'utf8');
    const wire = await readFile('apps/desktop/Engine/CefStageWire.cs', 'utf8');
    const probe = session.slice(session.indexOf('internal void RunBrokerSuspendedProbe()'), session.indexOf('internal void CompleteCefPreflightCleanup()'));
    assert.match(entry, /if \(brokerProbe\)\s*\{\s*session.RunBrokerSuspendedProbe\(\);\s*result = SuspendedProbeVerified;\s*\}\s*else if \(prepareOnly\)/);
    assert.match(probe, /RequireBrokerOwner\(\);[\s\S]*?broker.ProbeCefSuspended\(guid\);[\s\S]*?var locked = broker.QueryRegisteredVolume\(\);[\s\S]*?Unlocked: false, ReadyToUnlock: true[\s\S]*?string.Equals\(locked.VolumeGuid, guid, StringComparison.OrdinalIgnoreCase\)[\s\S]*?if \(attachedHere\)[\s\S]*?broker.Detach\(\);[\s\S]*?broker.QueryRegisteredVolume\(\) is not null[\s\S]*?disposed = true;/);
    assert.doesNotMatch(probe, /AcquireCefPreparationLease|CreateSuspended|broker.Lock\(|broker.Unlock\(/);
    assert.equal((probe.match(/broker.ProbeCefSuspended\(/g) ?? []).length, 1);
    assert.match(client, /#if VEIL_CEF_PREFLIGHT_ONLY[\s\S]*?internal void ProbeCefSuspended[\s\S]*?SendCommand\("ProbeCefSuspended"\);[\s\S]*?IsSuspendedProbeReply[\s\S]*?#endif/);
    assert.match(client, /#if VEIL_CEF_PREFLIGHT_ONLY\s*if \(command == "ProbeCefSuspended"\) VerifyServer\(pipe\);\s*#endif/);
    assert.match(wire, /IsSuspendedProbeReply[\s\S]*?ExactObject\(root, "ok", "cefSuspendedProbeVerified", "volumeGuid"\)[\s\S]*?JsonValueKind.True[\s\S]*?JsonValueKind.True[\s\S]*?string.Equals\(guid.GetString\(\), expectedGuid, StringComparison.OrdinalIgnoreCase\)/);
});


test('diagnostic trust enrollment is create-only and binds independent release anchors before writes', async () => {
    const entry = await readFile('apps/broker/Veil.VolumeBroker/Program.cs', 'utf8');
    const registry = await readFile('apps/broker/Veil.VolumeBroker/CefSystemBaselineTrustState.cs', 'utf8');
    const update = await readFile('apps/broker/Veil.VolumeBroker/CefSystemBaselineTrustUpdate.cs', 'utf8');
    assert.match(entry, /#if VEIL_CEF_DIAGNOSTIC_IPC[\s\S]*?--cef-enroll-diagnostic-trust[\s\S]*?--disposable-vm[\s\S]*?WindowsBuiltInRole.Administrator[\s\S]*?ReadInstalledDiagnosticInputs\(\)[\s\S]*?InstallDiagnosticTrust\(payload, signature,[\s\S]*?DecodeDigest\(payloadSha256\)[\s\S]*?#endif/);
    assert.match(update, /PrepareDiagnosticEnrollment[\s\S]*?FixedTimeEquals\(SHA256.HashData\(payload\), independentPayloadSha256\)[\s\S]*?PrepareEnrollment\(payload, signature, publicKey, independentKeySha256,[\s\S]*?prepared.next.MinimumSequence != independentSequence[\s\S]*?authority.RequireIndependent\(prepared.next\);\s*return new\(null, prepared.next, host, authority\);/);
    assert.match(registry, /InstallDiagnosticTrust[\s\S]*?RequireMaintenanceAuthority\(\);[\s\S]*?PrepareDiagnosticEnrollment[\s\S]*?CefSystemBaselineWriterLock.Acquire\(\)[\s\S]*?RequireExpectedAndCopyRecord\(null,[\s\S]*?InstallApprovalAuthority\(approvalKey, approvalAnchor\);\s*authority.RequireSame\(ReadApprovalAuthorityStable\(\)\);\s*Publish\(update\);[\s\S]*?VerifyInstalledTrust\(payload, signature\).RequireBoundTrust\(\);/);
});


test('protected lifecycle adapter remains disconnected from product startup', async () => {
    const cmake = await readFile('apps/cef-smoke/CMakeLists.txt', 'utf8');
    const product = await readFile('apps/cef-smoke/product_candidate.cc', 'utf8');
    const adapter = await readFile('apps/cef-smoke/protected_cef_lifecycle.cc', 'utf8');
    const lifecycle = await readFile('apps/cef-smoke/protected_lifecycle.h', 'utf8');
    assert.match(cmake, /target_sources\(veil-cef-product PRIVATE protected_cef_lifecycle\.cc protected_fixed_fixture\.cc\)/);
    assert.doesNotMatch(product, /RunProtectedCefLifecycle|CefInitialize\s*\(|CefExecuteProcess\s*\(|CefShutdown\s*\(/);
    assert.match(adapter, /CefExecuteProcess\(args, nullptr, sandbox_info\)[\s\S]*?PrepareProtectedInitializationSettings\(registration\)[\s\S]*?CefInitialize\(args, \*settings, nullptr, sandbox_info\)[\s\S]*?RunProtectedFixedFixture\(kProtectedSyntheticPage, current\)[\s\S]*?CefShutdown\(\)/);
    assert.match(lifecycle, /if \(!outcome\.browsers_closed\) return \{Status::kCleanupUnconfirmed\};[\s\S]*?try \{ shutdown\(\); \}/);
});


test('protected execution authority is sticky and broker monitoring retains the exact slot', async () => {
    const lifecycle = await readFile('apps/cef-smoke/protected_lifecycle.h', 'utf8');
    const slot = await readFile('apps/broker/Veil.VolumeBroker/CefSessionObservationPolicy.cs', 'utf8');
    assert.match(lifecycle, /bool retired = false;[\s\S]*?if \(retired\) return false;[\s\S]*?require_current\(\)[\s\S]*?retired = true;[\s\S]*?fixture\(current\)/);
    assert.match(slot, /ObserveCurrent\(object\? expectedGeneration, Action<T> observe\)[\s\S]*?!ReferenceEquals\(generation, expectedGeneration\)[\s\S]*?observing = true;[\s\S]*?observe\(candidate\);[\s\S]*?Retire\(\);[\s\S]*?finally \{ observing = false; \}/);
    assert.match(slot, /if \(!retired \|\| releasing \|\| activating \|\| observing\) return false;/);
});


test('runtime authority wire remains separate from redemption and unreachable for suspended creation', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const session = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const wire = await readFile('apps/cef-smoke/broker_verification.h', 'utf8');
    const product = await readFile('apps/cef-smoke/product_candidate.cc', 'utf8');
    assert.match(broker, /#if VEIL_CEF_DIAGNOSTIC_IPC\s*else if \(request.Command == BrokerCommand.CheckCefRuntimeAuthority\)[\s\S]*?redemptionCandidate[\s\S]*?CheckCefRuntimeAuthority\(request, client, pipeClient\)[\s\S]*?#endif/);
    assert.match(session, /MatchesRuntimeMember[\s\S]*?borrowedCreation is null \|\| !activated[\s\S]*?matchesMember\(client, pipeClient\)/);
    assert.match(session, /if \(borrowedCreation is not null\)\s*throw new UnauthorizedAccessException\("CEF prepared successor activation is disabled."\);/);
    assert.match(wire, /CheckCefRuntimeAuthority/);
    assert.doesNotMatch(product, /RequestRuntimeAuthority|RunProtectedCefLifecycle/);
});


test('runtime authority samples retain one original broker object and never reopen after refusal', async () => {
    const impl = await readFile('apps/cef-smoke/broker_verification.cc', 'utf8');
    const wire = await readFile('apps/cef-smoke/broker_verification.h', 'utf8');
    const check = impl.slice(impl.indexOf('bool RuntimeAuthoritySession::Check()'));
    assert.match(impl, /struct RuntimeAuthoritySession::State[\s\S]*?process\(OpenProcess\(PROCESS_QUERY_LIMITED_INFORMATION \| SYNCHRONIZE, FALSE, pid\)\)/);
    assert.match(impl, /WaitForSingleObject\(process, 0\) != WAIT_TIMEOUT \|\| ServicePid\(\) != pid/);
    assert.match(check, /state\.latch\.Check[\s\S]*?state\.process\.get\(\), state\.pid/);
    assert.doesNotMatch(check, /OpenProcess|make_unique|ServicePid\(/);
    assert.match(wire, /if \(retired_ \|\| checking_\) \{ retired_ = true; return false; \}/);
});


test('runtime member authority compares the pipe object inside retained nested observation', async () => {
    const jobs = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs', 'utf8');
    const session = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    const roles = await readFile('apps/broker/Veil.VolumeBroker/CefRoleTokenPolicy.cs', 'utf8');
    assert.match(jobs, /ContainsPrivateRuntimeMember[\s\S]*?RequireBoundPrivatePeer\(peer, rootHandle\)[\s\S]*?ObserveLiveMember\(member\) == identity[\s\S]*?IsProcessInJob\(member, job,[\s\S]*?IsProcessInJob\(member, peerHandle,/);
    assert.match(session, /void RequireMemberRole\(ClientIdentity identity, CefRetainedProcessDiagnostic diagnostic\) =>\s*CefRuntimeRolePolicy.RequireProvisionalRole\(identity == root,\s*diagnostic.Role, diagnostic.TokenDecision\);/);
    assert.match(session, /observe = \(\) => CefNestedProcessDiagnostics.Observe\(brokerJob, peerJob, root, rootHandle,\s*registration, systemVerifier, \(identity, _, diagnostic\) =>\s*RequireMemberRole\(identity, diagnostic\)\);/);
    assert.match(session, /observeMember = \(client, pipeClient\) =>[\s\S]*?CefNestedProcessDiagnostics.Observe[\s\S]*?RequireMemberRole\(identity, diagnostic\);[\s\S]*?Native.SameLiveProcess\(handle, pipeClient\)[\s\S]*?matches != 1/);
    assert.match(roles, /void RequireProvisionalRole[\s\S]*?if \(!MatchesProvisionalRole\(isRoot, role, decision\)\)\s*throw new UnauthorizedAccessException/);
    assert.match(session, /RequireRuntimeAuthority[\s\S]*?guard.Observe\(readContext,[\s\S]*?observeMember\(client, pipeClient\)/);
    assert.match(roles, /decision == CefRoleTokenDecision.MatchesProvisionalBaseline &&\s*\(isRoot \? role == CefObservedRole.Browser : role == CefObservedRole.Renderer\)/);
});


test('fixed protected fixture requires exact load and closure evidence without product activation', async () => {
    const fixture = await readFile('apps/cef-smoke/protected_fixed_fixture.cc', 'utf8');
    const policy = await readFile('apps/cef-smoke/protected_fixture_observation.h', 'utf8');
    const entry = await readFile('apps/cef-smoke/product_candidate.cc', 'utf8');
    assert.match(fixture, /CreateBrowserSync\(window, client, kProtectedSyntheticPage/);
    assert.match(fixture, /OnBeforeClose[\s\S]*?observation\.Closed\(browser->GetIdentifier\(\)\)/);
    assert.match(fixture, /if \(!result\.browsers_closed\) return \{false, false\};[\s\S]*?DestroyWindow\(parent\)/);
    assert.match(policy, /confirmed && ready_at_close_ && !failed_, confirmed/);
    assert.doesNotMatch(entry, /RunProtectedFixedFixture|RunProtectedCefLifecycle/);
});


test('nested diagnostics bind kernel token snapshots across module observation', async () => {
    const observer = await readFile('apps/broker/Veil.VolumeBroker/CefProcessTokenObserver.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    assert.match(observer, /ObserveRuntimeToken\(SafeProcessHandle process, ClientIdentity expected\)[\s\S]*?ReadSnapshot\(process, expected, inspectLaunchPrivileges: false\);\s*return new\(snapshot.Token, snapshot.AuthenticationId, snapshot.TokenId, snapshot.ModifiedId\);/);
    assert.match(nested, /var rootToken = CefProcessTokenObserver.ObserveRuntimeToken\(rootHandle, root\);[\s\S]*?var token = CefProcessTokenObserver.ObserveRuntimeToken\(handle, identity\);\s*CefRuntimeTokenSnapshot.RequireLogon\(rootToken, token\);[\s\S]*?CefProcessModuleObserver.Observe\(handle, identity,[\s\S]*?CefRuntimeTokenSnapshot.RequireSame\(token,\s*CefProcessTokenObserver.ObserveRuntimeToken\(handle, identity\)\);[\s\S]*?CefRuntimeTokenSnapshot.RequireSame\(rootToken,\s*CefProcessTokenObserver.ObserveRuntimeToken\(rootHandle, root\)\);/);
});


test('CEF role diagnostics retain closed utility claim categories without admission expansion', async () => {
    const role = await readFile('apps/broker/Veil.VolumeBroker/CefCommandRoleObserver.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    assert.match(role, /record struct CefRoleDetails\(CefObservedRole Role,\s*CefUtilityService UtilityService, CefSandboxClaim SandboxClaim\)/);
    assert.match(role, /if \(service == CefUtilityService.Network\)\s*return claim == CefSandboxClaim.Network\s*\? new\(CefObservedRole.Network, service, claim\) : default;/);
    assert.match(role, /return new\(expected, details\);/);
    assert.match(nested, /if \(role != CefCommandRoleObserver.Observe\(handle, identity, root\)\)[\s\S]*?modules, role.Details, mitigations\);/);
});


test('CEF mitigation observations use read-only exact process binding and repeat checks', async () => {
    const mitigation = await readFile('apps/broker/Veil.VolumeBroker/CefProcessMitigationObserver.cs', 'utf8');
    const nested = await readFile('apps/broker/Veil.VolumeBroker/CefProcessModuleObserver.cs', 'utf8');
    assert.match(mitigation, /const uint queryAccess = 0x0400 \| 0x1000 \| 0x00100000;/);
    assert.match(mitigation, /Native.SameLiveProcess\(retained, query\)/);
    assert.match(mitigation, /if \(!GetProcessMitigationPolicy\(query, policy, out uint flags, \(UIntPtr\)sizeof\(uint\)\)\)\s*throw new Win32Exception\(\);/);
    assert.match(mitigation, /var before = CefMitigationSnapshot.Read\(Read\);\s*RequireIdentity\(\);\s*CefMitigationSnapshot.RequireSame\(before, CefMitigationSnapshot.Read\(Read\)\);\s*RequireIdentity\(\);/);
    assert.match(nested, /var mitigations = CefProcessMitigationObserver.Observe\(handle, identity\);\s*var modules = CefProcessModuleObserver.Observe[\s\S]*?CefMitigationSnapshot.RequireSame\(mitigations,\s*CefProcessMitigationObserver.Observe\(handle, identity\)\);/);
});


test('nested Win32 smoke records mitigation evidence without treating missing data as zero', async () => {
    const source = await readFile('tests/cef/CefNestedSmoke/Program.cs', 'utf8');
    assert.match(source, /MitigationSample\(string Status, MitigationFlags\? Flags, int\? Win32Error\)/);
    assert.match(source, /using var query = OpenProcess\(0x0400 \| 0x1000 \| 0x00100000, false, pid\);[\s\S]*?CompareObjectHandles\(original, query\)[\s\S]*?Contains\(original\) && Contains\(query\)[\s\S]*?outerJob.ContainsFixtureMember\(original\) && outerJob.ContainsFixtureMember\(query\)/);
    assert.match(source, /MitigationFlags Snapshot\(\) => new\(Read\(1\), Read\(2\), Read\(4\), Read\(7\), Read\(10\), Read\(13\)\);/);
    assert.match(source, /return first == second\s*\? new\("observed", first, null\)\s*: new\("policy-changed", null, null\);/);
    assert.match(source, /return new\("query-unavailable", null, error.NativeErrorCode\);/);
    assert.match(source, /scope = "last-live-sample-per-process"/);
});


test('broker lock and prospective library publication use the same serialized operation boundary', async () => {
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const gate = await readFile('apps/broker/Veil.VolumeBroker/CefBrokerOperationGate.cs', 'utf8');
    assert.match(broker, /private readonly CefBrokerOperationGate serial = new\(\);/);
    assert.match(broker, /internal async Task RevokeWindowsSessionAsync\(uint sessionId\)\s*\{\s*await serial.WaitAsync\(\);[\s\S]*?ClearAuthenticatedOwner\(\);[\s\S]*?finally \{ serial.Release\(\); \}/);
    assert.match(broker, /internal async Task SecureVolumeAsync\(\)\s*\{\s*await serial.WaitAsync\(\);[\s\S]*?ClearAuthenticatedOwner\(\);[\s\S]*?finally \{ serial.Release\(\); \}/);
    assert.match(gate, /private readonly SemaphoreSlim serial = new\(1, 1\);/);
    assert.match(gate, /await WaitAsync\(cancellation\).ConfigureAwait\(false\);[\s\S]*?catch \(OperationCanceledException\) when \(cancellation.IsCancellationRequested\) \{ return false; \}[\s\S]*?if \(cancellation.IsCancellationRequested \|\| !current\(\) \|\| cancellation.IsCancellationRequested\)\s*return false;\s*return publish\(\);[\s\S]*?finally \{ Release\(\); \}/);
    assert.doesNotMatch(broker, /\bTryPublishAsync\s*\(/);
    assert.doesNotMatch(gate, /\b(?:File|Directory|Path|Native|Protocol)\./);
});



test('CEF library inspection uses fixed existing file pins under exclusive profile ownership without granting replacement', async () => {
    const layout = await readFile('apps/desktop/Engine/CefProfileLayout.cs', 'utf8');
    const profile = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedProfileLease.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedDirectoryLease.cs', 'utf8');
    assert.match(layout, /LibraryFile = Path.Combine\(EngineRoot, "library-state.json"\);\s*LibraryStageFile = Path.Combine\(EngineRoot, "library-state.stage"\);/);
    assert.match(profile, /internal LibraryFileInspection InspectLibraryFiles\(\)\s*\{\s*RequireCurrent\(\);\s*if \(profileLock is null\)[\s\S]*?committed = CefProtectedDirectoryLease.OpenRegularFile\(layout.LibraryFile, volume, readStatus\);\s*staged = CefProtectedDirectoryLease.OpenRegularFile\(layout.LibraryStageFile, volume, readStatus\);\s*var inspection = new LibraryFileInspection\(this, committed, staged\);\s*inspection.RequireCurrent\(\);\s*return inspection;/);
    assert.match(profile, /owner.RequireCurrent\(\);\s*if \(owner.profileLock is null\)[\s\S]*?committed.RequireCurrent\(owner.readStatus\);\s*staged.RequireCurrent\(owner.readStatus\);\s*owner.RequireCurrent\(\);[\s\S]*?owner.retired = true;/);
    assert.match(pin, /FileShare.ReadWrite, IntPtr.Zero,\s*FileMode.Open, 0x02000000 \| 0x00200000/);
    assert.match(pin, /info.Links != 1/);
    assert.doesNotMatch(profile, /\bFile\.(?:Move|Replace|WriteAllText|Delete)\(/);
    assert.doesNotMatch(broker, /\bInspectLibraryFiles\s*\(/);
});



test('test-only native library first publication uses retained source identity and never replaces existing state', async () => {
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedDirectoryLease.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const project = await readFile('apps/broker/Veil.VolumeBroker/Veil.VolumeBroker.csproj', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(pin, /#if VEIL_CEF_PROFILE_ACCESS_TEST[\s\S]*?TryPublishNewLibraryFixture[\s\S]*?NtSetInformationFile[\s\S]*?#endif/);
    assert.match(pin, /OpenAccess\(engineRoot, 0xA1\);[\s\S]*?RequireSameIdentity\(directory.handle, directoryAccess\);[\s\S]*?CreateFileW\(stagedPath, 0x00110081, FileShare.Read, IntPtr.Zero,\s*FileMode.Open, 0x00200000, IntPtr.Zero\);/);
    assert.match(pin, /RequireRegisteredProtection\(volume, readStatus\(\)\);\s*if \(!current\(\)\) return false;\s*RequireRegularFile\(source, stagedPath, volume\);/);
    assert.match(pin, /byte\[\] name = Encoding.Unicode.GetBytes\("library-state.json"\);\s*byte\[\] buffer = new byte\[24 \+ name.Length\];[\s\S]*?WriteInt32LittleEndian\(buffer.AsSpan\(16, 4\), name.Length\);\s*name.CopyTo\(buffer, 20\);\s*int status = NtSetInformationFile\(source, out _, buffer, \(uint\)buffer.Length, 10\);\s*if \(status == 0\) return true;/);
    assert.match(fixture, /Native rename overwrote existing destination[\s\S]*?Revocation did not stop native rename[\s\S]*?Linked staging file was published[\s\S]*?Busy staging file was published[\s\S]*?Native handle first publication failed/);
    assert.doesNotMatch(broker, /\bTryPublishNewLibraryFixture\b/);
    assert.doesNotMatch(project, /VEIL_CEF_PROFILE_ACCESS_TEST/);
    assert.doesNotMatch(pin, /\bFile\.(?:Move|Replace)\(/);
});



test('prospective library access uses retained user token and actual fixed file and directory rights without service fallback', async () => {
    const pin = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedDirectoryLease.cs', 'utf8');
    const profile = await readFile('apps/broker/Veil.VolumeBroker/CefProtectedProfileLease.cs', 'utf8');
    const observer = await readFile('apps/broker/Veil.VolumeBroker/CefProcessTokenObserver.cs', 'utf8');
    const broker = await readFile('apps/broker/Veil.VolumeBroker/Broker.cs', 'utf8');
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(pin, /RequireLibraryDirectoryAccess\(\)[\s\S]*?OpenAccess\(path, 0xE3\);[\s\S]*?RequireDirectory\(observed, path, volume\);\s*RequireSameIdentity\(handle, observed\);/);
    assert.match(pin, /RequireLibraryDataAccess\(bool staged\)\s*\{\s*using var observed = OpenAccess\(path, staged \? 0x87u : 0x81u\);\s*RequireRegularFile\(observed, path, volume\);\s*RequireSameIdentity\(handle, observed\);/);
    assert.match(profile, /WindowsIdentity.RunImpersonated\(access, \(\) =>\s*\{[\s\S]*?owner.directories\[\^4\].RequireLibraryDirectoryAccess\(\);\s*committed.RequireLibraryDataAccess\(staged: false\);\s*staged.RequireLibraryDataAccess\(staged: true\);\s*\}\);\s*RequireCurrent\(\);[\s\S]*?owner.retired = true;/);
    assert.match(observer, /RequireLibraryAccess\(CefProtectedProfileLease.LibraryFileInspection inspection\)[\s\S]*?RequireCurrent\(\);\s*if \(!DuplicateTokenEx\(duplicate, 0x000C, IntPtr.Zero, 2, 2, out var access\)\)[\s\S]*?CefLaunchTokenPolicy.RequireDuplicate\(duplicateSnapshot, snapshot\);\s*RequireCurrent\(\);\s*inspection.RequireUserAccess\(access\);\s*CefLaunchTokenPolicy.RequireSame\(snapshot,[\s\S]*?RequireCurrent\(\);/);
    assert.match(fixture, /FileSystemRights.ReadData[\s\S]*?FileSystemRights.WriteData[\s\S]*?FileSystemRights.AppendData[\s\S]*?Reject\(\(\) => deniedInspection.RequireUserAccess\(fixtureUser.AccessToken\)\);[\s\S]*?FileSystemRights.CreateFiles, FileSystemRights.DeleteSubdirectoriesAndFiles/);
    const inspectionStart = profile.indexOf('internal sealed class LibraryFileInspection');
    const inspectionEnd = profile.indexOf('// Read-only snapshot, not a permit.', inspectionStart);
    assert.ok(inspectionStart >= 0 && inspectionEnd > inspectionStart, 'library inspection scope missing');
    const inspection = profile.slice(inspectionStart, inspectionEnd);
    const accessStart = inspection.indexOf('WindowsIdentity.RunImpersonated(access,');
    assert.ok(accessStart >= 0, 'library impersonation missing');
    const libraryScope = inspection.slice(accessStart, inspection.indexOf('});', accessStart) + 3);
    assert.match(libraryScope, /owner.directories\[\^4\].RequireLibraryDirectoryAccess\(\);\s*committed.RequireLibraryDataAccess\(staged: false\);\s*staged.RequireLibraryDataAccess\(staged: true\);/);
    assert.doesNotMatch(libraryScope, /readStatus|authorityCurrent|RequireCurrent\(|File\.Write|CreateDirectory/);
    assert.doesNotMatch(broker, /\bRequireLibraryAccess\s*\(/);
});



test('Windows ACL denial fixtures explicitly restore the saved DACL and verify fresh authority', async () => {
    const fixture = await readFile('tests/security/CefProcessBindingCheck/Program.cs', 'utf8');
    assert.match(fixture, /byte\[\] originalFileDescriptor = originalFileAcl.GetSecurityDescriptorBinaryForm\(\);[\s\S]*?finally\s*\{\s*(?:\/\/[^\n]*\s*)*originalFileAcl.SetSecurityDescriptorBinaryForm\(originalFileDescriptor, AccessControlSections.Access\);\s*info.SetAccessControl\(originalFileAcl\);[\s\S]*?Library file DACL restoration failed/);
    assert.match(fixture, /byte\[\] originalDirectoryDescriptor = originalDirectoryAcl.GetSecurityDescriptorBinaryForm\(\);[\s\S]*?finally\s*\{\s*originalDirectoryAcl.SetSecurityDescriptorBinaryForm\(originalDirectoryDescriptor, AccessControlSections.Access\);\s*directory.SetAccessControl\(originalDirectoryAcl\);[\s\S]*?Library directory DACL restoration failed/);
    assert.match(fixture, /using \(var restoredProfile = CefProtectedProfileLease.OpenExclusive[\s\S]*?using \(var restoredInspection = restoredProfile.InspectLibraryFiles\(\)\)\s*restoredInspection.RequireUserAccess\(fixtureUser.AccessToken\);\s*Check\(File.ReadAllText\(libraryPath\)/);
    assert.match(fixture, /byte\[\] originalScratchDescriptor = originalAcl.GetSecurityDescriptorBinaryForm\(\);[\s\S]*?finally\s*\{\s*originalAcl.SetSecurityDescriptorBinaryForm\(originalScratchDescriptor, AccessControlSections.Access\);\s*scratch.SetAccessControl\(originalAcl\);[\s\S]*?Scratch DACL restoration failed/);
});

