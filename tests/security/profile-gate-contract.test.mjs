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
    assert.doesNotMatch(broker, /CefPinnedImage|CefImageDigest/);
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
    assert.match(stage, /pendingCefChild = candidate;\s*_ = ExpirePendingCefChildAsync\(candidate\);/);
    assert.match(stage, /if \(!ReferenceEquals\(pendingCefChild, candidate\)\) candidate\.Dispose\(\)/);
    assert.match(broker, /request\.Command is BrokerCommand\.Lock or BrokerCommand\.Detach\)\s*RevokePendingCefChild\(\)/);
    assert.match(broker, /private void ClearAuthenticatedOwner\(\)\s*\{\s*RevokePendingCefChild\(\)/);
    assert.match(broker, /if \(ReferenceEquals\(pendingCefChild, expected\)\) RevokePendingCefChild\(\)/);
    assert.match(preflight, /return new CefPendingChild\(new CefChildAdmission/);
    assert.match(pending, /admission\.Revoke\(\);\s*try \{ runtime\?\.Dispose\(\); \}\s*finally\s*\{\s*try \{ image\.Dispose\(\); \}\s*finally \{ process\.Dispose\(\); \}/);
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
    assert.match(redeem, /!Protected\(backend\.Status\(\)\) \|\|\s*!CefChildPreflight\.VerifyRedemption[\s\S]*?!Protected\(backend\.Status\(\)\)[\s\S]*?!pending\.TryRedeem/);
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
    assert.match(impl, /return IsVerifiedReply\(reply\) && SameServer/);
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
    assert.match(cmake, /add_library\(veil-cef-product SHARED EXCLUDE_FROM_ALL product_candidate\.cc\s+protected_bootstrap\.cc bootstrap_registration\.cc broker_verification\.cc product_module_policy\.cc\)/);
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
    assert.match(launch, /using var attributes = new JobAttributeList\(job\)[\s\S]*?CreateProcessW\(image, command, IntPtr\.Zero, IntPtr\.Zero, false, flags[\s\S]*?IsProcessInJob\(process, job/);
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
    assert.match(broker, /if \(request.Command is BrokerCommand.Lock or BrokerCommand.Detach\)\s*RevokePendingCefChild\(\);[^\n]*\n\s*var status = backend.Status\(\)/);
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
    assert.match(stage, /OpenInstalled\(serializedBrokerAccess: true\)[\s\S]*?new CefChildAdmission[\s\S]*?binding.Handle, image, runtime/);
    assert.match(lease, /!serializedBrokerAccess && Environment.CurrentManagedThreadId != ownerThread/);
    assert.match(lease, /foreach \(string modulePath in modulePaths\) CheckObservedApplicationModule/);
});


test('disabled preflight polls live authority and never confirms forced termination', async () => {
    const runner = await readFile('apps/desktop/Engine/CefDisabledPreflight.cs', 'utf8');
    const process = await readFile('apps/desktop/Engine/CefContainedProcess.cs', 'utf8');
    assert.match(runner, /while \(!child.WaitForExit\(0\)\)[\s\S]*?if \(!Current\(\)\)[\s\S]*?!child.ImageLoadMitigationsActive[\s\S]*?child.WaitForExit\(Math.Min\(250, remaining\)\)/);
    assert.match(process, /ForcedTerminationExit = 35/);
    assert.match(process, /TerminateJobObject\(job, ForcedTerminationExit\)/);
    const natural = process.slice(process.indexOf('internal bool WaitForNaturalJobExit'), process.indexOf('internal void TerminateAndDrain'));
    assert.doesNotMatch(natural, /TerminateJobObject|TerminateProcess/);
    assert.match(runner, /!child.WaitForNaturalJobExit\(250\) \|\| !Current\(\)[\s\S]*?confirm\(\);/);
});
