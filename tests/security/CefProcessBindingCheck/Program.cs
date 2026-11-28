// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Veil.VolumeBroker;
using Veil.Desktop;
using System.Text.Json;

if (args is ["--child"] or ["--child", "--type=renderer"])
{
    Console.WriteLine("ready");
    return Console.ReadLine() == "exit" ? 0 : 1;
}

CefRoleTokenPolicyTests.Run();
CefCommandRoleTests.Run();
CefSystemModuleVerifierTests.Run();

// Refusal before any file open or process creation. The test is independent
// of signed installed runtime, elevation or machine trust provisioning.
foreach (string invalidPath in new[] { "", "relative.exe", @"\\server\share\cef.exe", @"\rooted-not-qualified.exe",
    Path.Combine(Path.GetTempPath(), "cef.dll") })
{
    bool refused = false;
    try { using var invalid = CefPinnedImage.OpenVerifiedBeforeCreation(invalidPath, new CefImageDigest(new string('0', 64))); }
    catch (UnauthorizedAccessException) { refused = true; }
    if (!refused) throw new InvalidOperationException("Invalid pre-creation executable path accepted.");
}
Console.WriteLine("PASS: invalid pre-creation executable paths refused");

// Synthetic protection snapshots; never claim that the CI disk is
// encrypted. Use a deliberately unrelated volume GUID for actual refusal.
string expectedVolume = @"\\?\Volume{" + Guid.NewGuid().ToString("D") + @"}\";
var protectedState = new BackendStatus(true, expectedVolume, true, true, true, true);
CefProtectedDirectoryLease.RequireRegisteredProtection(expectedVolume, protectedState);
foreach (var bad in new[] { protectedState with { Attached = false },
    protectedState with { Unlocked = false }, protectedState with { FullyEncrypted = false },
    protectedState with { ProtectionOn = false }, protectedState with { ProtectorPresent = false },
    protectedState with { VolumeGuid = @"\\?\Volume{" + Guid.NewGuid().ToString("D") + @"}\" } })
{
    bool rejected = false;
    try { CefProtectedDirectoryLease.RequireRegisteredProtection(expectedVolume, bad); }
    catch (UnauthorizedAccessException) { rejected = true; }
    Check(rejected, "Unprotected or unrelated volume snapshot accepted.");
}
string directoryFixture = Path.Combine(Path.GetTempPath(), "veil-cef-directory-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directoryFixture);
try
{
    bool rejected = false;
    try { using var pin = CefProtectedDirectoryLease.Open(directoryFixture, expectedVolume, () => protectedState); }
    catch (UnauthorizedAccessException) { rejected = true; }
    Check(rejected, "Unrelated actual directory volume accepted.");
}
finally { Directory.Delete(directoryFixture); }
Console.WriteLine("PASS: synthetic protection state and actual wrong-volume directory refusal");

// Real filesystem handles, synthetic backend protection status. The
// actual CI disk is not asserted to be BitLocker protected.
string profileFixture = Path.Combine(Path.GetTempPath(), "veil-cef-profile-" + Guid.NewGuid().ToString("N"));
string driveRoot = Path.GetPathRoot(profileFixture)!;
var volumeBuffer = new System.Text.StringBuilder(64);
Check(GetVolumeNameForVolumeMountPointW(driveRoot, volumeBuffer, (uint)volumeBuffer.Capacity),
    "Cannot identify fixture volume.");
string fixtureVolume = volumeBuffer.ToString();
var fixtureStatus = new BackendStatus(true, fixtureVolume, true, true, true, true);
string fixtureWindows = Path.Combine(driveRoot, "veil-synthetic-system-" + Guid.NewGuid().ToString("N"));
var profileLayout = new CefProfileLayout(profileFixture);
bool authority = true;
Directory.CreateDirectory(profileFixture);
try
{
    foreach (string path in profileLayout.Directories) Directory.CreateDirectory(path);
    using (var profile = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
        fixtureWindows, () => fixtureStatus, () => authority))
    {
        var plan = profile.VerifiedEnvironment();
        Check(plan.Count == 7 && plan["TEMP"] == profileLayout.Scratch &&
            plan[CefChildEnvironmentPlan.RootKey] == profileLayout.EngineRoot &&
            plan[CefChildEnvironmentPlan.CacheKey] == profileLayout.Cache &&
            plan[CefChildEnvironmentPlan.LogKey] == profileLayout.LogFile,
            "Retained profile environment mismatch.");
        profile.RequireCurrent();
        authority = false;
        Reject(() => profile.RequireCurrent());
        authority = true;
        Reject(() => profile.RequireCurrent());
    }
    using (var profile = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
        fixtureWindows, () => fixtureStatus, () => authority))
    {
        File.WriteAllText(profileLayout.LogFile, "synthetic");
        Reject(() => profile.VerifiedEnvironment());
        File.Delete(profileLayout.LogFile);
        Reject(() => profile.RequireCurrent());
    }
    // Existing regular log and nested regular files can be observed
    // without modifying their data. A new hard link must retire the lease.
    string nested = Path.Combine(profileLayout.Cache, "nested");
    Directory.CreateDirectory(nested);
    string payload = Path.Combine(nested, "data.bin");
    string alias = Path.Combine(profileLayout.Scratch, "alias.bin");
    File.WriteAllText(payload, "synthetic-data");
    File.WriteAllText(profileLayout.LogFile, "synthetic-log");
    using (var profile = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
        fixtureWindows, () => fixtureStatus, () => authority))
    {
        profile.RequireCurrent();
        Check(File.ReadAllText(profileLayout.LogFile) == "synthetic-log" &&
            File.ReadAllText(payload) == "synthetic-data", "Profile inspection modified data.");
        Check(CreateHardLinkW(alias, payload, IntPtr.Zero), "Cannot create fixture hard link.");
        Reject(() => profile.RequireCurrent());
        File.Delete(alias);
        Reject(() => profile.RequireCurrent());
    }
    Check(CreateHardLinkW(alias, profileLayout.LogFile, IntPtr.Zero), "Cannot create fixture log hard link.");
    Reject(() =>
    {
        using var linked = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
            fixtureWindows, () => fixtureStatus, () => authority);
    });
    File.Delete(alias);
    File.Delete(profileLayout.LogFile);
    Directory.Delete(nested, recursive: true);
    // Broker requires an existing regular single-link lock. It must not
    // manufacture one or defeat a desktop's existing FileShare.None open.
    string profileLockPath = Path.Combine(profileFixture, "profile.lock");
    using (var missingLock = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
        fixtureWindows, () => fixtureStatus, () => authority))
    {
        Reject(() => missingLock.AcquireExclusiveLock());
        Check(!File.Exists(profileLockPath), "Broker created a missing profile lock.");
    }
    File.WriteAllText(profileLockPath, "original-lock");
    using (var desktopLock = new FileStream(profileLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    using (var competing = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
        fixtureWindows, () => fixtureStatus, () => authority))
    {
        Reject(() => competing.AcquireExclusiveLock());
        desktopLock.Dispose();
        Reject(() => competing.AcquireExclusiveLock());
    }
    using (var owned = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
        fixtureWindows, () => fixtureStatus, () => authority))
    {
        owned.AcquireExclusiveLock();
        owned.RequireCurrent();
        using var competing = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
            fixtureWindows, () => fixtureStatus, () => authority);
        Reject(() => competing.AcquireExclusiveLock());
        Reject(() => owned.AcquireExclusiveLock());
        owned.RequireCurrent();
    }
    Check(File.ReadAllText(profileLockPath) == "original-lock",
        "Broker modified the profile lock contents.");
    Check(CreateHardLinkW(alias, profileLockPath, IntPtr.Zero), "Cannot create fixture lock hard link.");
    using (var linkedLock = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
        fixtureWindows, () => fixtureStatus, () => authority))
        Reject(() => linkedLock.AcquireExclusiveLock());
    File.Delete(alias);
    File.Delete(profileLockPath);
    Console.WriteLine("PASS: existing exclusive profile lock, busy/missing/linked refusal and release");
    Directory.Delete(profileLayout.Cache);
    bool missingRefused = false;
    try { using var missing = CefProtectedProfileLease.Open(profileFixture, fixtureVolume,
        fixtureWindows, () => fixtureStatus, () => authority); }
    catch (System.ComponentModel.Win32Exception) { missingRefused = true; }
    Check(missingRefused && !Directory.Exists(profileLayout.Cache),
        "Missing directory was accepted or created.");
    // The failed open must release earlier pins so cleanup can proceed.
}
finally { Directory.Delete(profileFixture, recursive: true); }
Console.WriteLine("PASS: retained profile directory chain, explicit environment and irreversible refusal (synthetic protection)");

// Pure installed-registration schema and broker identity matching. No
// machine registration is published or changed by this fixture.
string registrationBroker = Path.Combine(driveRoot, "veil-fixture", "Broker", "Veil.VolumeBroker.exe");
var registrationFields = new Dictionary<string, object>
{
    ["volumeId"] = "synthetic",
    ["pipeName"] = "VEIL.VolumeBroker",
    ["brokerImage"] = registrationBroker,
    ["profilePath"] = profileFixture,
    ["requireWindowsService"] = true,
    ["development"] = false,
};
string ParseRegistration(byte[] bytes) => CefRegisteredProfileLease.Parse(bytes, "synthetic",
    "VEIL.VolumeBroker", registrationBroker, fixtureWindows);
byte[] validRegistration = JsonSerializer.SerializeToUtf8Bytes(registrationFields);
Check(ParseRegistration(validRegistration) == profileFixture, "Valid registered profile rejected.");
void RejectRegistration(byte[] bytes)
{
    bool rejected = false;
    try { _ = ParseRegistration(bytes); }
    catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException)
    { rejected = true; }
    Check(rejected, "Invalid installed client registration accepted.");
}
foreach (var change in new (string Key, object Value)[] {
    ("volumeId", "other"), ("pipeName", "other"), ("brokerImage", profileFixture),
    ("profilePath", "relative"), ("profilePath", fixtureWindows), ("development", true),
    ("requireWindowsService", false), ("requireWindowsService", "true"), ("unknown", 1) })
{
    var changed = new Dictionary<string, object>(registrationFields) { [change.Key] = change.Value };
    RejectRegistration(JsonSerializer.SerializeToUtf8Bytes(changed));
}
var missingRegistration = new Dictionary<string, object>(registrationFields);
missingRegistration.Remove("profilePath");
RejectRegistration(JsonSerializer.SerializeToUtf8Bytes(missingRegistration));
string duplicateRegistration = System.Text.Encoding.UTF8.GetString(validRegistration);
RejectRegistration(System.Text.Encoding.UTF8.GetBytes(duplicateRegistration[..^1] + ",\"volumeId\":\"synthetic\"}"));
Console.WriteLine("PASS: installed client registration schema and independent broker binding (pure parser)");

// Exercise the actual production parser with no broker or volume access.
using (var parsed = Protocol.Parse("""{"command":"CheckCefChild","volumeId":"synthetic","childHandle":"123"}"""u8))
{
    Check(parsed.Command == BrokerCommand.CheckCefChild && parsed.ChildHandle == 123 &&
        parsed.Secret is null, "Valid CEF preflight wire request rejected.");
}
using (var staged = Protocol.Parse("""{"command":"StageCefChild","volumeId":"synthetic","childHandle":"123"}"""u8))
    Check(staged.Command == BrokerCommand.StageCefChild && staged.ChildHandle == 123, "Stage request rejected.");
using (var redeemed = Protocol.Parse("""{"command":"RedeemCefChild","volumeId":"synthetic"}"""u8))
    Check(redeemed.Command == BrokerCommand.RedeemCefChild && redeemed.ChildHandle is null &&
        redeemed.Secret is null, "Redemption request rejected.");
foreach (string bad in new[] {
    """{"command":"RedeemCefChild","volumeId":"synthetic","childHandle":"123"}""",
    """{"command":"RedeemCefChild","volumeId":"synthetic","secret":"synthetic"}""",
    """{"command":"RedeemCefChild","volumeId":"synthetic","pid":"123"}""",
    """{"command":"StageCefChild","volumeId":"synthetic"}""",
    """{"command":"StageCefChild","volumeId":"synthetic","childHandle":"1","secret":"synthetic"}""",
    """{"command":"CheckCefChild","volumeId":"synthetic"}""",
    """{"command":"CheckCefChild","volumeId":"synthetic","childHandle":123}""",
    """{"command":"CheckCefChild","volumeId":"synthetic","childHandle":"0"}""",
    """{"command":"CheckCefChild","volumeId":"synthetic","childHandle":"01"}""",
    """{"command":"CheckCefChild","volumeId":"synthetic","childHandle":"-1"}""",
    """{"command":"CheckCefChild","volumeId":"synthetic","childHandle":"9223372036854775808"}""",
    """{"command":"CheckCefChild","volumeId":"synthetic","childHandle":"1","childHandle":"2"}""",
    """{"command":"CheckCefChild","volumeId":"synthetic","childHandle":"1","secret":"synthetic"}""",
    """{"command":"CheckCefChild","volumeId":"synthetic","childHandle":"1","path":"synthetic"}""",
    """{"command":"Status","volumeId":"synthetic","childHandle":"1"}"""
})
{
    bool rejected = false;
    try { using var _ = Protocol.Parse(System.Text.Encoding.UTF8.GetBytes(bad)); }
    catch (ProtocolException error) { rejected = error.Code == "BadRequest"; }
    Check(rejected, "Malformed CEF preflight wire request accepted.");
}
// Real desktop request writer -> real broker parser, with a handle above
// JavaScript's exact integer range. No service or installed profile involved.
using (var buffer = new MemoryStream())
{
    using (var writer = new Utf8JsonWriter(buffer))
        CefStageWire.WriteRequest(writer, "synthetic", 9007199254740993L);
    using var roundtrip = Protocol.Parse(buffer.ToArray());
    Check(roundtrip.Command == BrokerCommand.StageCefChild &&
        roundtrip.VolumeId == "synthetic" && roundtrip.ChildHandle == 9007199254740993L,
        "Desktop staging wire did not match broker parser.");
}
const string protectedReply = """{"ok":true,"status":{"attached":true,"volumeGuid":"synthetic-guid","fullyEncrypted":true,"protectionOn":true,"unlocked":true,"readyToUnlock":false,"protectorPresent":true}}""";
using (var reply = JsonDocument.Parse(protectedReply))
{
    Check(CefStageWire.IsProtectedStageReply(reply.RootElement, "synthetic-guid"), "Valid staging response rejected.");
    Check(!CefStageWire.IsProtectedStageReply(reply.RootElement, "other-guid"), "Wrong volume accepted.");
}
foreach (var invalidReply in new[] {
    """{"ok":false,"error":"CefUnavailable"}""",
    """{"ok":true,"cefChildVerified":true}""",
    protectedReply.Replace("\"ok\":true", "\"ok\":false,\"ok\":true"),
    protectedReply.Replace("\"attached\":true", "\"attached\":false"),
    protectedReply.Replace("\"fullyEncrypted\":true", "\"fullyEncrypted\":false"),
    protectedReply.Replace("\"protectionOn\":true", "\"protectionOn\":false"),
    protectedReply.Replace("\"unlocked\":true", "\"unlocked\":false"),
    protectedReply.Replace("\"protectorPresent\":true", "\"protectorPresent\":false"),
    protectedReply.Replace("\"readyToUnlock\":false", "\"readyToUnlock\":\"false\""),
    protectedReply.Replace("\"attached\":true", "\"attached\":true,\"extra\":true")
})
{
    using var reply = JsonDocument.Parse(invalidReply);
    Check(!CefStageWire.IsProtectedStageReply(reply.RootElement, "synthetic-guid"), "Invalid staging response accepted.");
}
Console.WriteLine("PASS: strict CEF preflight protocol parsing and desktop staging wire");

using var owner = Process.GetCurrentProcess();
var identity = Native.ObserveProcess(owner.SafeHandle);
using var child = Process.Start(new ProcessStartInfo
{
    FileName = Environment.ProcessPath ?? throw new InvalidOperationException("No test apphost."),
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    ArgumentList = { "--child", "--type=renderer" },
    CreateNoWindow = true,
}) ?? throw new InvalidOperationException("Test child did not start.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    { return; }
    throw new InvalidOperationException("Invalid child nomination accepted.");
}

try
{
    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready",
        "Test child failed to become ready.");
    var expected = Native.ObserveProcess(child.SafeHandle);
    var modules = Native.SnapshotModulePaths(child.SafeHandle, expected);
    Check(modules.Contains(expected.ImagePath, StringComparer.OrdinalIgnoreCase), "Live module snapshot omitted child image.");
    Check(modules.Length == modules.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Duplicate module paths.");
    Reject(() => Native.SnapshotModulePaths(owner.SafeHandle, expected));
    var binding = Native.DuplicateNominatedChild(identity, owner.SafeHandle,
        child.SafeHandle.DangerousGetHandle());
    using var retained = binding.Handle;
    Check(binding.Identity == expected && Native.IsRunning(retained), "Child identity not retained.");
    var roleObservation = CefCommandRoleObserver.Observe(retained, expected, identity);
    Check(roleObservation.Identity == expected && roleObservation.Role == CefObservedRole.Renderer,
        "Retained command role observation did not recognize synthetic renderer.");
    Check(roleObservation == CefCommandRoleObserver.Observe(child.SafeHandle, expected, identity),
        "Independent handles returned different command role observations.");
    Check(CefCommandRoleObserver.Observe(retained, expected, expected).Role == CefObservedRole.Unknown,
        "Renderer arguments accepted as browser root.");
    Reject(() => CefCommandRoleObserver.Observe(owner.SafeHandle, expected, identity));
    Reject(() => CefCommandRoleObserver.Observe(retained,
        expected with { CreatedFileTime = expected.CreatedFileTime + 1 }, identity));
    Console.WriteLine("PASS: retained process command role observation");
    var tokenObservation = CefProcessTokenObserver.Observe(retained, expected);
    Check(tokenObservation.Identity == expected &&
        tokenObservation.ElevationType is >= 1 and <= 3, "Retained token observation invalid.");
    Check(tokenObservation == CefProcessTokenObserver.Observe(child.SafeHandle, expected),
        "Independent handles to the same process did not observe the same token fields.");
    Reject(() => CefProcessTokenObserver.Observe(owner.SafeHandle, expected));
    Reject(() => CefProcessTokenObserver.Observe(retained,
        expected with { CreatedFileTime = expected.CreatedFileTime + 1 }));
    Reject(() => CefProcessTokenObserver.Observe(retained,
        expected with { SessionId = expected.SessionId + 1 }));
    var launchToken = CefProcessTokenObserver.ObserveLaunchToken(retained, expected);
    Check(launchToken.Token == tokenObservation &&
        launchToken == CefProcessTokenObserver.ObserveLaunchToken(child.SafeHandle, expected),
        "Independent retained handles disagreed on group/privilege launch snapshot.");
    Reject(() => CefProcessTokenObserver.ObserveLaunchToken(owner.SafeHandle, expected));
    if (launchToken.Token.Elevated || launchToken.Token.ElevationType != 1 ||
        launchToken.AdministratorGroupPresent || !launchToken.OnlyStandardPrivileges)
        Reject(() => CefLaunchTokenPolicy.RequireStandard(launchToken));
    Reject(() => CefProcessTokenObserver.LaunchTokenLease.Acquire(owner.SafeHandle, expected).Dispose());
    bool eligible = true;
    try { CefLaunchTokenPolicy.RequireStandard(launchToken); }
    catch (UnauthorizedAccessException) { eligible = false; }
    if (eligible)
    {
        using var tokenLease = CefProcessTokenObserver.LaunchTokenLease.Acquire(retained, expected);
        tokenLease.RequireCurrent();
        tokenLease.Dispose();
        bool disposedRefused = false;
        try { tokenLease.RequireCurrent(); }
        catch (ObjectDisposedException) { disposedRefused = true; }
        Check(disposedRefused, "Disposed primary token lease accepted.");
        Console.WriteLine("PASS: actual standard-user token duplicate lease (no process creation)");
    }
    else
    {
        Reject(() => CefProcessTokenObserver.LaunchTokenLease.Acquire(retained, expected).Dispose());
        Console.WriteLine("SKIP: positive primary token duplicate requires separate standard-user account; refusal tested");
    }
    Console.WriteLine("PASS: actual retained launch token inventory (not a launch authorization)");
    Console.WriteLine("PASS: retained process token snapshot and wrong-object refusal");
    Check(Native.SameLiveProcess(retained, child.SafeHandle), "Different handles to the same child did not match.");
    Check(!Native.SameLiveProcess(retained, owner.SafeHandle), "Different process objects matched.");
    using (var watch = Native.OpenForWait(identity, owner.SafeHandle))
        Check(Native.SameLiveProcess(watch, owner.SafeHandle), "Owner watch did not retain the observed object.");
    Reject(() => Native.DuplicateNominatedChild(identity, owner.SafeHandle, IntPtr.Zero).Handle.Dispose());
    Reject(() => Native.DuplicateNominatedChild(identity, owner.SafeHandle, owner.SafeHandle.DangerousGetHandle()).Handle.Dispose());
    Reject(() => Native.DuplicateNominatedChild(identity with { CreatedFileTime = identity.CreatedFileTime + 1 },
        owner.SafeHandle, child.SafeHandle.DangerousGetHandle()).Handle.Dispose());
    Reject(() => Native.DuplicateNominatedChild(identity, child.SafeHandle, child.SafeHandle.DangerousGetHandle()).Handle.Dispose());
    // A non-process handle must never be accepted as an executable child.
    using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
    Reject(() => Native.DuplicateNominatedChild(identity, owner.SafeHandle,
        signal.SafeWaitHandle.DangerousGetHandle()).Handle.Dispose());
    await child.StandardInput.WriteLineAsync("exit");
    await child.StandardInput.FlushAsync();
    // Wait for the exact duplicated kernel object, not only the managed
    // Process exit notification. A timeout/failure is still a test failure.
    uint retainedWait = WaitForSingleObject(retained, 10000);
    Check(retainedWait == 0,
        $"Retained child did not signal: wait=0x{retainedWait:X8}, lastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(child.ExitCode == 0, $"Synthetic child returned unexpected exit code {child.ExitCode}.");
    Check(!Native.IsRunning(retained), "Signaled retained child still considered live.");
    Reject(() => CefCommandRoleObserver.Observe(retained, expected, identity));
    Reject(() => CefProcessTokenObserver.Observe(retained, expected));
    Check(!Native.SameLiveProcess(retained, child.SafeHandle), "Exited process objects matched as live.");
    Reject(() => Native.SnapshotModulePaths(retained, expected));
    Check(Native.MatchesExitedNomination(identity, owner.SafeHandle, child.SafeHandle.DangerousGetHandle().ToInt64(), retained, 0), "Exited nominated object did not match.");
    Check(!Native.MatchesExitedNomination(identity, owner.SafeHandle, child.SafeHandle.DangerousGetHandle().ToInt64(), retained, 29), "Incorrect exit code accepted.");
    Check(!Native.MatchesExitedNomination(identity, owner.SafeHandle, owner.SafeHandle.DangerousGetHandle().ToInt64(), retained, 0), "Different object accepted.");
    Reject(() => Native.DuplicateNominatedChild(identity, owner.SafeHandle, child.SafeHandle.DangerousGetHandle()).Handle.Dispose());
    using var victim = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
        ArgumentList = { "--child" }, CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Missing synthetic termination child.");
    try
    {
        Check(await victim.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready",
            "Synthetic termination child not ready.");
        var nominated = Native.DuplicateNominatedChild(identity, owner.SafeHandle, victim.SafeHandle.DangerousGetHandle());
        using var observed = nominated.Handle;
        Reject(() => Native.OpenCefTerminationHandle(nominated.Identity, owner.SafeHandle).Dispose());
        using var stopHandle = Native.OpenCefTerminationHandle(nominated.Identity, observed);
        Check(Native.SameLiveProcess(observed, stopHandle), "Termination handle object mismatch.");
        Check(Native.TerminateCefChildAndWait(stopHandle), "Retained child exit not confirmed.");
        Check(victim.WaitForExit(2000) && victim.ExitCode == 35 &&
            Native.TerminateCefChildAndWait(stopHandle), "Forced termination failed or used success exit.");
    }
    finally
    {
        if (!victim.HasExited) { victim.Kill(); victim.WaitForExit(10000); }
    }
    // Isolated live test child only. Production must assign atomically at
    // suspended creation; this test exercises the broker-owned object and
    // reduced-access duplicate without an actual service or CEF.
    using (var brokerJob = CefBrokerOwnedJob.CreateDisabledCandidate())
    using (var assignment = new Microsoft.Win32.SafeHandles.SafeFileHandle(
        new IntPtr(brokerJob.IssueAssignmentHandle(identity, owner.SafeHandle)), true))
    using (var jobChild = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
        ArgumentList = { "--child" }, CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Missing synthetic job child."))
    {
        try
        {
            Check(await jobChild.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready",
                "Job child not ready.");
            var jobIdentity = Native.ObserveProcess(jobChild.SafeHandle);
            Reject(() => brokerJob.BindSingleCandidate(identity, owner.SafeHandle, jobIdentity, jobChild.SafeHandle));
            Check(AssignProcessToJobObject(assignment, jobChild.SafeHandle), "Synthetic job assignment failed.");
            brokerJob.BindSingleCandidate(identity, owner.SafeHandle, jobIdentity, jobChild.SafeHandle);
            Check(!brokerJob.HasNoActiveProcesses(), "Live job reported empty.");
            Check(!TerminateJobObject(assignment, 35) && !jobChild.HasExited,
                "Reduced-access assignment handle could terminate the job.");
            Check(brokerJob.TerminateAndDrain(5000) && jobChild.WaitForExit(5000) &&
                jobChild.ExitCode == 35, "Broker-owned job did not drain.");
        }
        finally
        {
            if (!jobChild.HasExited) { jobChild.Kill(); jobChild.WaitForExit(10000); }
        }
    }
    Console.WriteLine("PASS: synthetic Windows child handle duplication, generation binding and exit detection");
    return 0;
}
finally
{
    if (!child.HasExited)
    {
        child.Kill();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}


[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint milliseconds);

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
static extern bool AssignProcessToJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job,
    Microsoft.Win32.SafeHandles.SafeProcessHandle process);

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
static extern bool TerminateJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job, uint code);


[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
    ExactSpelling = true, SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
static extern bool GetVolumeNameForVolumeMountPointW(string root, System.Text.StringBuilder name, uint length);


[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
    ExactSpelling = true, SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
static extern bool CreateHardLinkW(string newName, string existingName, IntPtr reserved);
