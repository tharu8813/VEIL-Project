// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Veil.VolumeBroker;
using Veil.Desktop;
using System.Text.Json;

if (args is ["--child"])
{
    Console.WriteLine("ready");
    return Console.ReadLine() == "exit" ? 0 : 1;
}

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
    ArgumentList = { "--child" },
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
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(child.ExitCode == 0 && !Native.IsRunning(retained), "Exited child still considered live.");
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
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
static extern bool AssignProcessToJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job,
    Microsoft.Win32.SafeHandles.SafeProcessHandle process);

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
static extern bool TerminateJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job, uint code);
