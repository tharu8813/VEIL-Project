// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Veil.Desktop;

// Child mode uses only an isolated synthetic marker, never a browser/profile.
if (Environment.GetEnvironmentVariable("VEIL_SYNTHETIC_CHILD_MARKER") is { } marker)
{
    File.WriteAllText(marker, "started");
    if (Environment.GetEnvironmentVariable("VEIL_SYNTHETIC_EXIT") == "29") return 29;
    Thread.Sleep(Timeout.Infinite);
    return 0;
}
// The crash-owner mode leaves a suspended child in an atomic job. The
// outer test kills this owner without managed disposal and observes child exit.
if (args is ["--crash-owner", var crashMarker])
{
    var childEnv = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        if (entry.Key is string key && entry.Value is string value)
            childEnv[key] = value;
    childEnv["VEIL_SYNTHETIC_CHILD_MARKER"] = crashMarker;
    using var suspended = CefContainedProcess.CreateSuspended(Environment.ProcessPath!, childEnv);
    Console.WriteLine(GetProcessId(new IntPtr(suspended.NominatedHandle)));
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    return 0;
}
static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected refusal.");
}
var work = Path.Combine(Path.GetTempPath(), "veil-containment-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    string image = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test apphost.");
    string markerPath = Path.Combine(work, "started.txt");
    var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        if (entry.Key is string key && entry.Value is string value)
            environment[key] = value;
    environment["VEIL_SYNTHETIC_CHILD_MARKER"] = markerPath;
    Check(CefContainedProcess.BuildEnvironmentBlock(new Dictionary<string,string?> { ["B"]="2", ["a"]="1" }) ==
        "a=1\0B=2\0\0", "Environment ordering or termination invalid.");
    Reject(() => CefContainedProcess.BuildEnvironmentBlock(new Dictionary<string,string?> { ["a"]="1", ["A"]="2" }));
    Reject(() => CefContainedProcess.BuildEnvironmentBlock(new Dictionary<string,string?> { ["a"]="x\0y" }));
    using (var denied = CefContainedProcess.CreateSuspended(image, environment))
    {
        Check(denied.IsAlive && denied.NominatedHandle > 0 && !File.Exists(markerPath), "Child was not initially suspended.");
        Reject(() => denied.ResumeAfterVerification(() => false));
        Check(!denied.IsAlive && !File.Exists(markerPath), "Denied child started.");
    }
    using (var child = CefContainedProcess.CreateSuspended(image, environment))
    {
        Check(child.IsAlive && !child.WaitForExit(50) && !File.Exists(markerPath), "Child suspension failed.");
        Check(child.ImageLoadMitigationsActive, "Creation-time image load policy was not active.");
        int pid = GetProcessId(new IntPtr(child.NominatedHandle));
        using var observer = Process.GetProcessById(pid);
        // Retain an independent process handle before closing the job.
        _ = observer.SafeHandle;
        child.ResumeAfterVerification(() => true);
        Check(child.ImageLoadMitigationsActive, "Image load policy was absent after resume.");
        Check(SpinWait.SpinUntil(() => File.Exists(markerPath), TimeSpan.FromSeconds(10)), "Resumed synthetic child did not start.");
        child.Dispose();
        Check(observer.WaitForExit(10000), "Child survived job close.");
    }
    File.Delete(markerPath);
    environment["VEIL_SYNTHETIC_EXIT"] = "29";
    using (var preflight = CefContainedProcess.CreateSuspended(image, environment))
    {
        int stages = 0;
        uint exit = CefDisabledPreflight.Run(preflight, () =>
        {
            Check(preflight.NominatedHandle > 0 && !File.Exists(markerPath), "Stage did not see suspended child.");
            stages++;
        }, () => true, () =>
        {
            Check(preflight.ExitedHandle > 0 && preflight.ReadExitCode() == 29, "Confirmation did not see exited child.");
            stages++;
        });
        Check(exit == 29 && stages == 2 && File.Exists(markerPath) && !preflight.IsAlive,
            "Disabled preflight did not complete exactly once.");
    }
    File.Delete(markerPath);
    using (var denied = CefContainedProcess.CreateSuspended(image, environment))
    {
        Reject(() => CefDisabledPreflight.Run(denied, () => throw new InvalidOperationException("Synthetic denial"), () => true, () => throw new Exception("Unexpected confirmation")));
        Check(!File.Exists(markerPath) && !denied.IsAlive, "Denied preflight ran.");
    }
    environment.Remove("VEIL_SYNTHETIC_EXIT");
    // A revoked running candidate must not reach broker confirmation.
    using (var revoked = CefContainedProcess.CreateSuspended(image, environment))
    {
        bool confirmationCalled = false;
        int checks = 0;
        Reject(() => CefDisabledPreflight.Run(revoked, () => { }, () =>
        {
            checks++;
            return checks <= 2 || !File.Exists(markerPath);
        }, () => confirmationCalled = true));
        Check(!confirmationCalled && !revoked.IsAlive && File.Exists(markerPath),
            "Running child was not revoked and drained.");
        Check(revoked.ReadExitCode() == CefContainedProcess.ForcedTerminationExit &&
            revoked.ReadExitCode() != CefDisabledPreflight.VerifiedButDisabled,
            "Forced exit overlapped verified exit.");
    }
    File.Delete(markerPath);
    // A live job cannot pass natural exit observation or accidentally be
    // terminated by it. Explicit cleanup uses a distinct exit code.
    using (var live = CefContainedProcess.CreateSuspended(image, environment))
    {
        live.ResumeAfterVerification(() => true);
        Check(!live.WaitForNaturalJobExit(50) && live.IsAlive, "Live job accepted as naturally exited.");
        live.TerminateAndDrain(5000);
        Check(live.ReadExitCode() == CefContainedProcess.ForcedTerminationExit,
            "Forced drain exit code mismatch.");
    }
    if (File.Exists(markerPath)) File.Delete(markerPath);
    var ownerInfo = new ProcessStartInfo(image)
    {
        UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
        ArgumentList = { "--crash-owner", markerPath },
    };
    ownerInfo.Environment.Remove("VEIL_SYNTHETIC_CHILD_MARKER");
    using (var crashOwner = Process.Start(ownerInfo) ?? throw new InvalidOperationException("Cannot start synthetic owner."))
    {
        try
        {
            string? line = await crashOwner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(int.TryParse(line, out int childPid) && childPid > 0, "Missing suspended child PID.");
            using var observer = Process.GetProcessById(childPid);
            _ = observer.SafeHandle;
            Check(!observer.HasExited && !File.Exists(markerPath), "Crash-owner child was not suspended.");
            crashOwner.Kill();
            Check(crashOwner.WaitForExit(10000), "Synthetic owner did not terminate.");
            Check(observer.WaitForExit(10000), "Suspended child survived abrupt owner death.");
            Check(!File.Exists(markerPath), "Suspended child executed during owner death.");
        }
        finally
        {
            if (!crashOwner.HasExited) { crashOwner.Kill(); crashOwner.WaitForExit(10000); }
        }
    }
    Console.WriteLine("PASS: actual synthetic Windows atomic job assignment, denied resume, job close and abrupt owner termination");
    return 0;
}
finally { Directory.Delete(work, recursive: true); }

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern int GetProcessId(IntPtr process);
