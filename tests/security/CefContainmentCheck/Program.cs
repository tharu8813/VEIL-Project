// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using Veil.Desktop;
using Veil.VolumeBroker;

// Child mode uses only an isolated synthetic marker, never a browser/profile.
if (Environment.GetEnvironmentVariable("VEIL_SYNTHETIC_CHILD_MARKER") is { } marker)
{
    if (Environment.GetEnvironmentVariable("VEIL_SYNTHETIC_SPAWN") == "1")
    {
        // Only synthetic test descendants, same apphost; no browser or
        // protected profile. Do not use breakaway or shell execution.
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        info.Environment.Remove("VEIL_SYNTHETIC_SPAWN");
        info.Environment["VEIL_SYNTHETIC_CHILD_MARKER"] = marker + ".descendant";
        try
        {
            using var descendant = Process.Start(info) ?? throw new InvalidOperationException("Missing test descendant.");
            if (!SpinWait.SpinUntil(() => File.Exists(marker + ".descendant") || descendant.HasExited, TimeSpan.FromSeconds(10)))
                return 3;
            if (!File.Exists(marker + ".descendant") || descendant.HasExited)
                File.WriteAllText(marker, "denied");
            else
                File.WriteAllText(marker, descendant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            File.WriteAllText(marker, "denied");
        }
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
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
    // Actual nested-job integration: desktop atomically creates a suspended
    // child, then broker retains a second job without exporting its handle.
    // Test both independent containment owners; no CEF or BitLocker.
    using (var owner = Process.GetCurrentProcess())
    {
        var ownerIdentity = Native.ObserveProcess(owner.SafeHandle);
        foreach (bool closeBrokerFirst in new[] { true, false })
        {
            using var candidate = CefContainedProcess.CreateSuspended(image, environment);
            using var brokerJob = CefBrokerOwnedJob.CreateDisabledCandidate();
            var nomination = Native.DuplicateNominatedChild(ownerIdentity, owner.SafeHandle,
                new IntPtr(candidate.NominatedHandle));
            using var observed = nomination.Handle;
            int pid = GetProcessId(new IntPtr(candidate.NominatedHandle));
            using var observer = Process.GetProcessById(pid);
            _ = observer.SafeHandle;
            Check(!File.Exists(markerPath) && candidate.IsAlive, "Nested candidate not suspended.");
            brokerJob.AssignVerifiedCandidate(ownerIdentity, owner.SafeHandle, nomination.Identity, observed);
            Check(brokerJob.ContainsVerifiedCandidate(nomination.Identity, observed) &&
                !File.Exists(markerPath), "Broker did not bind suspended candidate.");
            candidate.ResumeAfterVerification(() => brokerJob.ContainsVerifiedCandidate(nomination.Identity, observed));
            Check(SpinWait.SpinUntil(() => File.Exists(markerPath), TimeSpan.FromSeconds(10)),
                "Nested candidate did not resume.");
            if (closeBrokerFirst) brokerJob.Dispose();
            else candidate.Dispose();
            Check(observer.WaitForExit(10000), "Candidate survived closing either owning job.");
            if (!closeBrokerFirst) Check(brokerJob.HasNoActiveProcesses(), "Broker job did not become empty.");
            File.Delete(markerPath);
        }
    }
    // Compare a disabled single-process job to a test-only multi-process
    // job, without granting product activation. Either job must drain both
    // the root and descendant when its last handle closes.
    environment["VEIL_SYNTHETIC_SPAWN"] = "1";
    using (var owner = Process.GetCurrentProcess())
    {
        var identity = Native.ObserveProcess(owner.SafeHandle);
        foreach (int mode in new[] { 0, 1, 2 })
        {
            using var candidate = CefContainedProcess.CreateSuspended(image, environment);
            using var brokerJob = mode == 0 ? CefBrokerOwnedJob.CreateDisabledCandidate() :
                CefBrokerOwnedJob.CreateMultiprocessFixture();
            var binding = Native.DuplicateNominatedChild(identity, owner.SafeHandle,
                new IntPtr(candidate.NominatedHandle));
            using var retained = binding.Handle;
            using var rootObserver = Process.GetProcessById(checked((int)binding.Identity.ProcessId));
            _ = rootObserver.SafeHandle;
            brokerJob.AssignVerifiedCandidate(identity, owner.SafeHandle, binding.Identity, retained);
            Check(brokerJob.ContainsVerifiedCandidate(binding.Identity, retained) == (mode == 0),
                "Fixture job was mistaken for disabled preflight admission.");
            candidate.ResumeAfterVerification(() => mode == 0
                ? brokerJob.ContainsVerifiedCandidate(binding.Identity, retained)
                : brokerJob.ContainsFixtureRoot(binding.Identity, retained));
            Check(SpinWait.SpinUntil(() => File.Exists(markerPath) &&
                new FileInfo(markerPath).Length > 0, TimeSpan.FromSeconds(15)), "Missing synthetic spawn result.");
            // The root is alive and writes the result only after observing
            // descendant startup/exit. File sharing is retried on Windows.
            string result = "";
            Check(SpinWait.SpinUntil(() =>
            {
                try { result = File.ReadAllText(markerPath); return result.Length > 0; }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(5)), "Cannot read synthetic spawn result.");
            if (mode == 0)
            {
                Check(result == "denied" && !File.Exists(markerPath + ".descendant"),
                    "Disabled job permitted a descendant.");
                candidate.TerminateAndDrain(5000);
                Check(brokerJob.HasNoActiveProcesses(), "Disabled job not drained.");
            }
            else
            {
                Check(int.TryParse(result, out int descendantPid) && descendantPid > 0,
                    "Multiprocess fixture descendant not observed.");
                using var descendant = Process.GetProcessById(descendantPid);
                _ = descendant.SafeHandle;
                Check(!descendant.HasExited && brokerJob.ContainsFixtureRoot(binding.Identity, retained) &&
                    !brokerJob.ContainsVerifiedCandidate(binding.Identity, retained),
                    "Fixture root or descendant missing, or disabled admission confused.");
                if (mode == 1) brokerJob.Dispose();
                else candidate.Dispose();
                Check(descendant.WaitForExit(10000), "Descendant survived closing a containing job.");
                Check(rootObserver.WaitForExit(10000), "Root survived closing a containing job.");
                if (mode == 2) Check(brokerJob.HasNoActiveProcesses(), "Broker fixture job did not drain.");
            }
            File.Delete(markerPath);
            if (File.Exists(markerPath + ".descendant")) File.Delete(markerPath + ".descendant");
        }
    }
    environment.Remove("VEIL_SYNTHETIC_SPAWN");
    Console.WriteLine("PASS: synthetic single-process refusal and test-only nested multiprocess job drain");
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
    // Exercise the actual disabled coordinator runner, both independently
    // owned jobs, and the production exited-object check in one sequence.
    // The callbacks simulate broker dispatch; no service, signed candidate,
    // real volume or CEF runtime is involved.
    using (var owner = Process.GetCurrentProcess())
    {
        var identity = Native.ObserveProcess(owner.SafeHandle);
        foreach (bool denyConfirmation in new[] { false, true })
        {
            using var candidate = CefContainedProcess.CreateSuspended(image, environment);
            using var brokerJob = CefBrokerOwnedJob.CreateDisabledCandidate();
            var binding = Native.DuplicateNominatedChild(identity, owner.SafeHandle,
                new IntPtr(candidate.NominatedHandle));
            using var retained = binding.Handle;
            int stageCalls = 0, confirmCalls = 0;
            bool confirmed = false;
            uint RunBoundCandidate() => CefDisabledPreflight.Run(candidate, () =>
            {
                Check(++stageCalls == 1 && !File.Exists(markerPath),
                    "Bound preflight staged twice or after execution.");
                brokerJob.AssignVerifiedCandidate(identity, owner.SafeHandle, binding.Identity, retained);
                Check(brokerJob.ContainsVerifiedCandidate(binding.Identity, retained),
                    "Bound preflight job membership missing.");
            }, () => true, () =>
            {
                Check(++confirmCalls == 1 && brokerJob.HasNoActiveProcesses(),
                    "Bound preflight confirmed twice or before broker job emptied.");
                long nomination = denyConfirmation ? owner.SafeHandle.DangerousGetHandle().ToInt64() : candidate.ExitedHandle;
                // Deliberately mismatched object must never succeed, even
                // when the actual candidate exited normally with code 29.
                bool matches = Native.MatchesExitedNomination(identity, owner.SafeHandle,
                    nomination, retained, CefDisabledPreflight.VerifiedButDisabled);
                if (denyConfirmation)
                {
                    Check(!matches, "Wrong exited nomination was accepted.");
                    throw new UnauthorizedAccessException("Synthetic broker confirmation denied.");
                }
                Check(matches, "Exact exited nomination was refused.");
                confirmed = true;
            });
            if (denyConfirmation)
            {
                bool rejected = false;
                try { _ = RunBoundCandidate(); }
                catch (UnauthorizedAccessException) { rejected = true; }
                Check(rejected && !confirmed, "Denied confirmation returned verified success.");
            }
            else
                Check(RunBoundCandidate() == CefDisabledPreflight.VerifiedButDisabled && confirmed,
                    "Bound disabled preflight failed.");
            Check(stageCalls == 1 && confirmCalls == 1 &&
                !candidate.IsAlive && brokerJob.HasNoActiveProcesses() &&
                candidate.ReadExitCode() == CefDisabledPreflight.VerifiedButDisabled &&
                File.Exists(markerPath), "Bound candidate exit or accounting mismatch.");
            File.Delete(markerPath);
        }
    }
    Console.WriteLine("PASS: synthetic bound disabled preflight exact exit confirmation and wrong-object refusal");
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
