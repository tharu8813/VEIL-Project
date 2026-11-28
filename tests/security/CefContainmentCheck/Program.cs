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
        // Inherit the root's console. CREATE_NO_WINDOW can add a conhost
        // to the nested jobs and invalidate the two-process fixture.
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false
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
    // Inject a failure AFTER native creation but BEFORE verification.
    // The escrow, not a guessed PID, retains the original process handles.
    foreach (bool injectFailure in new[] { true, false })
    {
        using var outer = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var inner = CefBrokerOwnedJob.CreateMultiprocessFixture();
        var custody = new CefContainedProcess.CreationCustody();
        bool refused = false;
        try
        {
            outer.CreateAtomicRetainedPairFixture(inner, image, environment, custody, injectFailure);
        }
        catch (InvalidOperationException error) when (error.Message == "Synthetic post-create verification failure.")
        { refused = true; }
        Check(refused == injectFailure && custody.HasRetainedProcess && !File.Exists(markerPath),
            "Creation custody did not retain the original suspended child.");
        if (injectFailure)
        {
            Check(!outer.HasNoActiveProcesses() && !inner.HasNoActiveProcesses(),
                "Injected refusal did not retain both live Job members.");
            Reject(() => custody.TakeVerified());
            bool drained = custody.TryDrainAndRelease(5000);
            Check(drained, "Rejected creation did not confirm root and three-Job exit.");
            Check(!custody.HasRetainedProcess, "Drained custody retained child.");
            Check(outer.HasNoActiveProcesses() && inner.HasNoActiveProcesses(), "Rejected creation peer accounting not empty.");
            Check(!File.Exists(markerPath), "Rejected creation executed.");
            Check(custody.TryDrainAndRelease(0), "Repeated cleanup refused.");
            Reject(() => custody.TakeVerified());
        }
        else
        {
            using var created = custody.TakeVerified();
            Check(created.IsAlive && !custody.HasRetainedProcess && !custody.TryDrainAndRelease(0),
                "Custody retained cleanup authority after verified transfer.");
            Reject(() => custody.TakeVerified());
            var deadline = Stopwatch.StartNew();
            created.TerminateAndDrain(5000);
            int remaining = (int)Math.Max(0L, 5000 - deadline.ElapsedMilliseconds);
            Check(created.WaitForRetainedCreationExit(remaining) &&
                outer.HasNoActiveProcesses() && inner.HasNoActiveProcesses(),
                "Transferred child did not drain all three Jobs.");
        }
        using var freshOuter = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var freshInner = CefBrokerOwnedJob.CreateMultiprocessFixture();
        Reject(() => freshOuter.CreateAtomicRetainedPairFixture(freshInner, image, environment, custody, false));
        Check(freshOuter.HasNoActiveProcesses() && freshInner.HasNoActiveProcesses() && !File.Exists(markerPath),
            "Reused custody created a process.");
    }
    Console.WriteLine("PASS: post-create failure retains handles until verified exit; successful transfer is one-shot");
    // Simulate allocation failure at the earliest managed point after the
    // original handles have been attached to their preallocated wrappers.
    // No process reopening by PID and no actual memory exhaustion.
    using (var outer = CefBrokerOwnedJob.CreateMultiprocessFixture())
    using (var inner = CefBrokerOwnedJob.CreateMultiprocessFixture())
    {
        var custody = new CefContainedProcess.CreationCustody();
        bool refused = false;
        try
        {
            outer.CreateAtomicRetainedPairFixture(inner, image, environment, custody,
                injectFailure: false, injectAllocationFailure: true);
        }
        catch (OutOfMemoryException error) when (error.Message == "Synthetic allocation failure immediately after capture.")
        { refused = true; }
        Check(refused && custody.HasRetainedProcess &&
            !outer.HasNoActiveProcesses() && !inner.HasNoActiveProcesses() && !File.Exists(markerPath),
            "Allocation refusal lost original suspended root.");
        Reject(() => custody.TakeVerified());
        Check(custody.TryDrainAndRelease(5000) && !custody.HasRetainedProcess &&
            outer.HasNoActiveProcesses() && inner.HasNoActiveProcesses() && !File.Exists(markerPath),
            "Allocation refusal did not confirm root and three-Job exit.");
    }
    Console.WriteLine("PASS: preallocated creation handles survive synthetic post-create allocation failure");
    // A second-stage validation refusal must retain the actual created
    // process, not permit a transfer or reentrant cleanup.
    using (var outer = CefBrokerOwnedJob.CreateMultiprocessFixture())
    using (var inner = CefBrokerOwnedJob.CreateMultiprocessFixture())
    {
        var custody = new CefContainedProcess.CreationCustody();
        outer.CreateAtomicRetainedPairFixture(inner, image, environment, custody, false);
        bool refused = false;
        try
        {
            custody.VerifyRetainedRoot(root =>
            {
                Check(Native.IsRunning(root) && !File.Exists(markerPath), "Post-create root not suspended/live.");
                Reject(() => custody.TakeVerified());
                Check(!custody.TryDrainAndRelease(0), "Reentrant cleanup accepted.");
                throw new UnauthorizedAccessException("Synthetic post-create token refusal.");
            });
        }
        catch (UnauthorizedAccessException error) when (error.Message == "Synthetic post-create token refusal.")
        { refused = true; }
        Check(refused && custody.HasRetainedProcess, "Post-create refusal lost custody.");
        Reject(() => custody.TakeVerified());
        Check(custody.TryDrainAndRelease(5000) && outer.HasNoActiveProcesses() &&
            inner.HasNoActiveProcesses() && !File.Exists(markerPath), "Post-create refusal did not drain.");
    }
    Console.WriteLine("PASS: second-stage validation refusal retains original root and blocks transfer");
    // The creation custody owns the original root while both Jobs are
    // bound. Revalidation may use the same handle wrapper only, never a
    // second wrapper even if it points to the same kernel process object.
    foreach (bool wrongOwner in new[] { false, true })
    {
        using var owner = Process.GetCurrentProcess();
        var ownerIdentity = Native.ObserveProcess(owner.SafeHandle);
        using var outer = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var inner = CefBrokerOwnedJob.CreateMultiprocessFixture();
        var custody = new CefContainedProcess.CreationCustody();
        outer.CreateAtomicRetainedPairFixture(inner, image, environment, custody, false);
        bool refused = false;
        try
        {
            custody.VerifyRetainedRoot(root =>
            {
                var identity = Native.ObserveProcess(root);
                outer.BindPreparedPrivatePair(inner, ownerIdentity,
                    wrongOwner ? root : owner.SafeHandle, identity, root);
                outer.RequireOrBindPreparedPrivatePair(inner, ownerIdentity, owner.SafeHandle, identity, root);
                using var alias = new Microsoft.Win32.SafeHandles.SafeProcessHandle(root.DangerousGetHandle(), ownsHandle: false);
                bool aliasRefused = false;
                try { outer.RequireOrBindPreparedPrivatePair(inner, ownerIdentity, owner.SafeHandle, identity, alias); }
                catch (UnauthorizedAccessException) { aliasRefused = true; }
                Check(aliasRefused, "Prepared pair accepted a different root handle wrapper.");
                outer.RequirePreparedPrivateRoot(inner, ownerIdentity, owner.SafeHandle, identity, root);
                Check(!File.Exists(markerPath), "Binding unexpectedly executed the root.");
            });
        }
        catch (UnauthorizedAccessException) when (wrongOwner) { refused = true; }
        Check(refused == wrongOwner && custody.HasRetainedProcess && !File.Exists(markerPath),
            "Prepared pair binding lost root or accepted invalid owner.");
        if (wrongOwner) Reject(() => custody.TakeVerified());
        Check(custody.TryDrainAndRelease(5000) && outer.HasNoActiveProcesses() &&
            inner.HasNoActiveProcesses() && !File.Exists(markerPath), "Bound creation did not drain.");
    }
    Console.WriteLine("PASS: creation custody binds and revalidates exact private Job pair without resume");
    // Exercise the production-shaped lifetime with the isolated apphost,
    // not the installed product, service token or real encrypted volume.
    foreach (bool injectFailure in new[] { false, true })
    {
        using var outer = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var inner = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var prerequisites = new MemoryStream();
        var slot = new CefDiagnosticSessionSlot<ICefDiagnosticLifetime>();
        var generation = new object();
        var relock = new CefDiagnosticRelock();
        var probe = new CefPreparedCreationLifetime(outer, inner, prerequisites, (a, b, custody) =>
        {
            Check(slot.Occupied && slot.Current(generation) is null,
                "Creation ran before slot ownership.");
            a.CreateAtomicRetainedPairFixture(b, image, environment, custody, injectFailure);
            Check(!File.Exists(markerPath), "Creation probe unexpectedly executed.");
            // Simulate reentrant owner revocation while creating.
            relock.Request(generation);
            slot.Retire();
            Check(!slot.TryReleaseRetired(0) && prerequisites.CanRead,
                "Creating slot released prerequisites.");
        });
        var outcome = slot.RegisterAndActivate(generation, probe, probe.Create, out var failure);
        Check(outcome == CefDiagnosticActivationOutcome.Retired && failure is not null &&
            slot.CleanupPending && prerequisites.CanRead && !File.Exists(markerPath),
            "Creation probe did not retain the retired lifetime.");
        relock.Request(generation);
        bool locked = false;
        Check(!relock.TryComplete(generation, !slot.Occupied, () => { locked = true; return true; }) && !locked,
            "Relock accepted before creation drain.");
        Check(slot.TryReleaseRetired(5000) && !slot.Occupied && !prerequisites.CanRead &&
            !File.Exists(markerPath), "Creation probe did not release after drain.");
        Check(relock.TryComplete(generation, !slot.Occupied, () => { locked = true; return true; }) && locked,
            "Relock policy did not complete after creation drain.");
    }
    Console.WriteLine("PASS: owned suspended creation probe retires, drains, and gates relock");
    // Periodic monitor owns the exact slot until its sample completes.
    static void RejectAuthority(Action action)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected authority refusal.");
    }
    var observedSlot = new CefDiagnosticSessionSlot<ICefDiagnosticLifetime>();
    var observedGeneration = new object();
    var observedLifetime = new RevocationFaultFixture(null) { AllowRelease = true };
    Check(observedSlot.TryRegister(observedGeneration, observedLifetime), "Monitor fixture registration failed.");
    RejectAuthority(() => observedSlot.ObserveCurrent(new object(), _ => throw new InvalidOperationException("Stale sample ran.")));
    Check(observedSlot.Current(observedGeneration) == observedLifetime, "Stale monitor retired current lifetime.");
    observedSlot.ObserveCurrent(observedGeneration, candidate =>
        Check(ReferenceEquals(candidate, observedLifetime) && observedSlot.Current(observedGeneration) is null,
            "Monitor did not hold exclusive observation."));
    RejectAuthority(() => observedSlot.ObserveCurrent(observedGeneration, _ =>
    {
        observedSlot.Retire();
        Check(!observedSlot.TryReleaseRetired(0), "Reentrant retirement released observing slot.");
    }));
    Check(observedSlot.CleanupPending && observedSlot.TryReleaseRetired(0) && !observedSlot.Occupied,
        "Retired monitor slot did not release after sample.");
    var failedObservation = new RevocationFaultFixture(null) { AllowRelease = true };
    Check(observedSlot.TryRegister(observedGeneration, failedObservation), "Second monitor fixture registration failed.");
    RejectAuthority(() => observedSlot.ObserveCurrent(observedGeneration, _ => throw new UnauthorizedAccessException("Synthetic observation failure.")));
    Check(observedSlot.CleanupPending && observedSlot.TryReleaseRetired(0), "Observation failure left authority active.");
    Console.WriteLine("PASS: exact monitor slot, stale generation, reentrant revocation and terminal observation failure");
    // Fault injection exercises the same drain orchestration, without
    // pretending that a synthetic refusal is a real Windows Job failure.
    foreach (int failedStage in new[] { 0, 1, 2, 3 })
    foreach (bool throws in new[] { false, true })
    {
        using var pin = new MemoryStream();
        var retained = new CefRetainedLifetime(pin);
        var attempts = new List<int>();
        var budgets = new List<int>();
        bool retried = false;
        bool Attempt(int stage, int budget)
        {
            attempts.Add(stage);
            budgets.Add(budget);
            if (retried || stage != failedStage) return true;
            if (throws) throw new IOException("Synthetic private drain failure.");
            return false;
        }
        bool Verify()
        {
            attempts.Add(3);
            if (retried || failedStage != 3) return true;
            if (throws) throw new IOException("Synthetic final empty check failure.");
            return false;
        }
        bool Drain() => CefPreparedCreationLifetime.TryDrainAll(0,
            budget => Attempt(0, budget), budget => Attempt(1, budget),
            budget => Attempt(2, budget), Verify);
        bool released = false, faulted = false;
        try { released = retained.TryRelease(Drain); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { faulted = true; }
        Check(!released && pin.CanRead && faulted == throws, "Failed private drain released prerequisites.");
        Check(attempts.SequenceEqual(failedStage == 3 ? new[] { 0, 1, 2, 3 } : new[] { 0, 1, 2 }) &&
            budgets.Count == 3 && budgets.All(budget => budget == 0),
            "Private drain skipped a stop attempt, reused a deadline or verified incomplete exit.");
        attempts.Clear();
        retried = true;
        Check(retained.TryRelease(Drain) && !pin.CanRead &&
            attempts.SequenceEqual(new[] { 0, 1, 2, 3 }), "Private drain retry did not release after proof.");
    }
    Console.WriteLine("PASS: private drain failures still attempt every stop and retain prerequisites until retry proof");
    // Retry worker policy: recoverable faults do not erase the pending
    // generation or allow a relock after an incomplete cleanup callback.
    foreach (int failedPhase in new[] { 0, 1 })
    {
        var retryGeneration = new object();
        var obligation = new CefDiagnosticRelock();
        obligation.Request(retryGeneration);
        bool cleanupConfirmed = false;
        int lockCalls = 0;
        bool retried = false;
        var injected = new IOException("Synthetic retry worker failure.");
        void Cleanup()
        {
            if (!retried && failedPhase == 0) throw injected;
            cleanupConfirmed = true;
        }
        void Relock()
        {
            Check(obligation.TryComplete(retryGeneration, cleanupConfirmed, () =>
            {
                ++lockCalls;
                if (!retried && failedPhase == 1) throw injected;
                return true;
            }), "Retry relock not completed.");
        }
        var failure = CefStopRetryPolicy.TryAttempt(Cleanup, Relock);
        Check(ReferenceEquals(failure, injected) && obligation.Pending &&
            lockCalls == failedPhase, "Retry fault cleared obligation or attempted premature lock.");
        Check(CefStopRetryPolicy.ShouldContinue(0, 30, obligation.Pending, failure) &&
            CefStopRetryPolicy.ShouldContinue(0, 30, false, failure) &&
            !CefStopRetryPolicy.ShouldContinue(29, 30, obligation.Pending, failure),
            "Retry policy ignored failure or attempt bound.");
        retried = true;
        Check(CefStopRetryPolicy.TryAttempt(Cleanup, Relock) is null && !obligation.Pending &&
            lockCalls == failedPhase + 1 &&
            !CefStopRetryPolicy.ShouldContinue(1, 30, false, null),
            "Successful retry did not verify relock and stop.");
    }
    Console.WriteLine("PASS: retry worker defers unsafe relock, retains generation and retries recoverable faults");
    // Synthetic owner-revocation faults must not skip independent cleanup
    // phases. A retired slot remains hidden and blocks a new registration
    // until its actual release callback confirms success.
    foreach (int failedPhase in new[] { 0, 1, 2, 3, 4 })
    {
        var generation = new object();
        var obligation = new CefDiagnosticRelock();
        var slot = new CefDiagnosticSessionSlot<ICefDiagnosticLifetime>();
        var injected = new IOException("Synthetic owner revocation failure.");
        var candidate = new RevocationFaultFixture(failedPhase == 0 ? injected : null);
        Check(slot.TryRegister(generation, candidate), "Revocation fixture registration refused.");
        obligation.Request(generation);
        var phases = new List<int>();
        void Phase(int phase, Action action)
        {
            phases.Add(phase);
            if (phase == failedPhase && phase != 0) throw injected;
            action();
        }
        Exception? failure = null;
        try
        {
            CefOwnerRevocationCleanup.Run(() => Phase(0, slot.Retire),
                () => Phase(1, () => { }),
                () => Phase(2, () => Check(!slot.TryReleaseRetired(0), "Unconfirmed drain released slot.")),
                () => Phase(3, () => { }));
        }
        catch (IOException error) { failure = error; }
        Check(phases.SequenceEqual(new[] { 0, 1, 2, 3 }) &&
            (failedPhase == 4 ? failure is null : ReferenceEquals(failure, injected)),
            "Owner revocation skipped a phase or lost the failure.");
        Check(slot.CleanupPending && slot.Current(generation) is null &&
            !slot.TryRegister(new object(), new RevocationFaultFixture(null)) && obligation.Pending,
            "Revoked session was visible or relock obligation was lost.");
        Check(!obligation.TryComplete(generation, !slot.Occupied,
            () => throw new InvalidOperationException("Premature relock.")), "Unconfirmed cleanup allowed relock.");
        candidate.AllowRelease = true;
        Check(slot.TryReleaseRetired(0) && obligation.TryComplete(generation, !slot.Occupied, () => true),
            "Verified cleanup did not permit relock.");
    }
    Console.WriteLine("PASS: owner revocation faults preserve retired slot and attempt all cleanup phases");
    // Terminal preparation refusal has no creation slot, but still owns
    // a generation-bound relock obligation. Stale requests cannot latch
    // or overwrite another generation's obligation.
    {
        var currentGeneration = new object();
        var staleGeneration = new object();
        var terminalRelock = new CefDiagnosticRelock();
        Check(!terminalRelock.RequestIfCurrent(staleGeneration, currentGeneration) &&
            !terminalRelock.RequestIfCurrent(currentGeneration, null) && !terminalRelock.Pending,
            "Stale preparation latched a relock.");
        Check(terminalRelock.RequestIfCurrent(currentGeneration, currentGeneration) &&
            terminalRelock.Pending, "Terminal preparation did not latch relock.");
        Check(!terminalRelock.RequestIfCurrent(staleGeneration, staleGeneration) &&
            terminalRelock.RequestIfCurrent(currentGeneration, currentGeneration),
            "Another generation overwrote terminal relock.");
        int lockAttempts = 0;
        bool LockAndVerify() { ++lockAttempts; return lockAttempts == 2; }
        Check(!terminalRelock.TryComplete(staleGeneration, true, LockAndVerify) &&
            !terminalRelock.TryComplete(currentGeneration, false, LockAndVerify) &&
            lockAttempts == 0, "Stale or incomplete preparation attempted lock.");
        Check(!terminalRelock.TryComplete(currentGeneration, true, LockAndVerify) &&
            terminalRelock.Pending && lockAttempts == 1, "Unverified lock cleared terminal obligation.");
        Check(terminalRelock.TryComplete(currentGeneration, true, LockAndVerify) &&
            !terminalRelock.Pending && lockAttempts == 2, "Verified lock did not clear obligation.");
    }
    Console.WriteLine("PASS: terminal preparation relock is generation-bound and requires verified lock");
    // Exercise in-place ownership handoff using the actual synthetic root.
    // No apphost execution and no second native handle owner.
    foreach (int mode in new[] { 0, 1, 2 })
    {
        using var outer = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var inner = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var prerequisites = new MemoryStream();
        var slot = new CefDiagnosticSessionSlot<ICefDiagnosticLifetime>();
        var generation = new object();
        var probe = new CefPreparedCreationLifetime(outer, inner, prerequisites, (a, b, custody) =>
            a.CreateAtomicRetainedPairFixture(b, image, environment, custody, false));
        Check(slot.RegisterAndActivate(generation, probe, probe.Create, out _) ==
            CefDiagnosticActivationOutcome.Activated, "Handoff fixture creation refused.");
        Check(!slot.ReplacePrepared(new object(), probe, () => throw new InvalidOperationException("Stale factory executed."), out var staleFailure) &&
            staleFailure is null && ReferenceEquals(slot.Current(generation), probe), "Stale handoff changed slot.");
        bool switched = slot.ReplacePrepared(generation, probe, () =>
            probe.PrepareSuccessor((a, b, child, root) =>
            {
                Check(slot.Occupied && slot.Current(generation) is null &&
                    child.IsAlive && Native.IsRunning(root) && prerequisites.CanRead &&
                    !File.Exists(markerPath), "Handoff lost original ownership.");
                Check(!slot.TryReleaseRetired(0), "Handoff allowed early release.");
                if (mode == 1) throw new UnauthorizedAccessException("Synthetic successor construction refusal.");
                if (mode == 2)
                {
                    slot.Retire();
                    Check(!slot.TryReleaseRetired(0), "Retired handoff released during construction.");
                }
                return new BorrowingFixtureSuccessor(probe);
            }), out var handoffFailure);
        Check(switched == (mode == 0) && (handoffFailure is null) == switched &&
            slot.Occupied && prerequisites.CanRead && !File.Exists(markerPath), "Handoff result mismatch.");
        if (switched)
            Check(slot.Current(generation) is BorrowingFixtureSuccessor, "Successor was not published.");
        else
            Check(slot.CleanupPending && slot.Current(generation) is null, "Failed handoff was not retired.");
        slot.Retire();
        Check(slot.TryReleaseRetired(5000) && !slot.Occupied && !prerequisites.CanRead &&
            !File.Exists(markerPath), "Handoff lifetime did not drain and release.");
    }
    Console.WriteLine("PASS: in-place suspended lifetime handoff, construction refusal and reentrant retirement");
    // Same-user synthetic atomic Job pair: handoff before resume, including
    // a failure after resume actually ran. No product or BitLocker access.
    using (var owner = Process.GetCurrentProcess())
    {
        var ownerIdentity = Native.ObserveProcess(owner.SafeHandle);
        foreach (bool failAfterResume in new[] { false, true })
        {
            using var outer = CefBrokerOwnedJob.CreateMultiprocessFixture();
            using var inner = CefBrokerOwnedJob.CreateMultiprocessFixture();
            using var candidate = outer.CreateAtomicPairFixture(inner, image, environment);
            var binding = Native.DuplicateNominatedChild(ownerIdentity, owner.SafeHandle,
                new IntPtr(candidate.NominatedHandle));
            using var retained = binding.Handle;
            outer.BindSingleCandidate(ownerIdentity, owner.SafeHandle, binding.Identity, retained);
            inner.BindSingleCandidate(ownerIdentity, owner.SafeHandle, binding.Identity, retained);
            outer.RequirePrivateNestedBinding(inner, ownerIdentity, owner.SafeHandle, binding.Identity, retained);
            var session = new HandoffFixtureLifetime(candidate, outer, inner, retained);
            var slot = new CefDiagnosticSessionSlot<HandoffFixtureLifetime>();
            var generation = new object();
            Check(!File.Exists(markerPath), "Fixture ran before ownership handoff.");
            var outcome = slot.RegisterAndActivate(generation, session, () =>
            {
                Check(slot.Occupied && slot.Current(generation) is null, "Resume before handoff.");
                Check(outer.ContainsFixtureRoot(binding.Identity, retained) &&
                    inner.ContainsFixtureRoot(binding.Identity, retained), "Prepared pair changed.");
                bool wrongRootRefused = false;
                try { candidate.RequireRetainedPreparedRoot(owner.SafeHandle); }
                catch (UnauthorizedAccessException) { wrongRootRefused = true; }
                Check(wrongRootRefused && candidate.IsAlive && !File.Exists(markerPath),
                    "Wrong retained root accepted or handles lost.");
                candidate.ResumeRetainedRoot(retained);
                bool repeatedResumeRefused = false;
                try { candidate.ResumeRetainedRoot(retained); }
                catch (UnauthorizedAccessException) { repeatedResumeRefused = true; }
                Check(repeatedResumeRefused, "Retained root resumed twice.");
                Check(SpinWait.SpinUntil(() => File.Exists(markerPath), TimeSpan.FromSeconds(10)),
                    "Handoff fixture did not start.");
                if (failAfterResume) throw new UnauthorizedAccessException("Synthetic lost resume confirmation.");
            }, out var failure);
            Check(failAfterResume
                ? outcome == CefDiagnosticActivationOutcome.Retired && failure is UnauthorizedAccessException &&
                    slot.CleanupPending && slot.Current(generation) is null
                : outcome == CefDiagnosticActivationOutcome.Activated && failure is null &&
                    slot.Current(generation) == session, "Handoff result mismatch.");
            Check(!slot.TryRegister(new object(), session), "Occupied handoff replaced.");
            slot.Retire();
            Check(slot.TryReleaseRetired(5000) && !slot.Occupied &&
                candidate.WaitForExit(0) && outer.HasNoActiveProcesses() && inner.HasNoActiveProcesses(),
                "Handoff cleanup did not verify all exits.");
            File.Delete(markerPath);
        }
    }
    Console.WriteLine("PASS: actual synthetic atomic Job pair handoff and ambiguous resume drain");
    // Explicit VM test, never silently claim positive user-token creation
    // on an elevated CI account. The runner must have the required OS rights.
    if (Environment.GetEnvironmentVariable("VEIL_TEST_STANDARD_USER_CREATION") == "1")
    {
        using var owner = Process.GetCurrentProcess();
        var ownerIdentity = Native.ObserveProcess(owner.SafeHandle);
        using var lease = CefProcessTokenObserver.LaunchTokenLease.Acquire(owner.SafeHandle, ownerIdentity);
        using var outer = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var inner = CefBrokerOwnedJob.CreateMultiprocessFixture();
        using var candidate = outer.CreateAtomicUserPairFixture(inner, lease, image, environment);
        var binding = Native.DuplicateNominatedChild(ownerIdentity, owner.SafeHandle, new IntPtr(candidate.NominatedHandle));
        using var retained = binding.Handle;
        Check(candidate.IsAlive && !File.Exists(markerPath), "User fixture executed before verification.");
        outer.BindSingleCandidate(ownerIdentity, owner.SafeHandle, binding.Identity, retained);
        inner.BindSingleCandidate(ownerIdentity, owner.SafeHandle, binding.Identity, retained);
        outer.RequirePrivateNestedBinding(inner, ownerIdentity, owner.SafeHandle, binding.Identity, retained);
        candidate.ResumeAfterVerification(() =>
        {
            lease.RequireFixtureRoot(retained, binding.Identity);
            return outer.ContainsFixtureRoot(binding.Identity, retained) &&
                inner.ContainsFixtureRoot(binding.Identity, retained);
        });
        Check(SpinWait.SpinUntil(() => File.Exists(markerPath), TimeSpan.FromSeconds(10)),
            "User fixture did not execute.");
        Check(outer.TerminateAndVerifyPrivateDrain(inner, retained, 5000), "User fixture did not drain.");
        File.Delete(markerPath);
        Console.WriteLine("PASS: explicit standard-user atomic creation fixture");
    }
    else Console.WriteLine("SKIP: positive user-token process creation requires explicitly configured Windows VM");
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
            brokerJob.RequireInitialNestedBinding(candidate.FixtureJobHandle,
                ownerIdentity, owner.SafeHandle, nomination.Identity, observed);
            void RefuseInitialBinding(Action action)
            {
                try { action(); }
                catch (UnauthorizedAccessException) { return; }
                throw new InvalidOperationException("Invalid initial nested binding accepted.");
            }
            RefuseInitialBinding(() => brokerJob.RequireInitialNestedBinding(candidate.FixtureJobHandle,
                ownerIdentity, owner.SafeHandle, nomination.Identity, owner.SafeHandle));
            RefuseInitialBinding(() => brokerJob.RequireInitialNestedBinding(candidate.FixtureJobHandle,
                ownerIdentity with { CreatedFileTime = ownerIdentity.CreatedFileTime + 1 },
                owner.SafeHandle, nomination.Identity, observed));
            var nested = brokerJob.ObserveNestedRetainedMembers(candidate.FixtureJobHandle,
                [(nomination.Identity, observed)]);
            Check(nested.Length == 1 && nested[0] == nomination.Identity,
                "Suspended nested candidate observation failed.");
            // Equal counts are insufficient: a separate Job with one
            // suspended process must not certify the nominated candidate.
            using (var unrelated = CefContainedProcess.CreateSuspended(image, environment))
            {
                bool rejected = false;
                try { brokerJob.ObserveNestedRetainedMembers(unrelated.FixtureJobHandle,
                    [(nomination.Identity, observed)]); }
                catch (UnauthorizedAccessException) { rejected = true; }
                Check(rejected, "Unrelated Job with the same count accepted.");
                RefuseInitialBinding(() => brokerJob.RequireInitialNestedBinding(unrelated.FixtureJobHandle,
                    ownerIdentity, owner.SafeHandle, nomination.Identity, observed));
            }
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
    // processes on last-handle close or explicit termination.
    environment["VEIL_SYNTHETIC_SPAWN"] = "1";
    using (var owner = Process.GetCurrentProcess())
    {
        var identity = Native.ObserveProcess(owner.SafeHandle);
        foreach (int mode in new[] { 0, 1, 2, 3, 4, 5, 6 })
        {
            using var brokerJob = mode == 0 ? CefBrokerOwnedJob.CreateDisabledCandidate() :
                CefBrokerOwnedJob.CreateMultiprocessFixture();
            using var privatePeer = mode is 5 or 6 ? CefBrokerOwnedJob.CreateMultiprocessFixture() : null;
            if (mode == 6)
            {
                bool duplicateRefused = false;
                try { using var invalid = brokerJob.CreateAtomicPairFixture(brokerJob, image, environment); }
                catch (UnauthorizedAccessException) { duplicateRefused = true; }
                Check(duplicateRefused && !File.Exists(markerPath), "Duplicate atomic Jobs accepted.");
            }
            using var candidate = mode == 6
                ? brokerJob.CreateAtomicPairFixture(privatePeer!, image, environment)
                : CefContainedProcess.CreateSuspended(image, environment);
            var binding = Native.DuplicateNominatedChild(identity, owner.SafeHandle,
                new IntPtr(candidate.NominatedHandle));
            using var retained = binding.Handle;
            using var rootObserver = Process.GetProcessById(checked((int)binding.Identity.ProcessId));
            _ = rootObserver.SafeHandle;
            if (mode == 6)
            {
                Check(!File.Exists(markerPath) && candidate.IsAlive && candidate.ImageLoadMitigationsActive,
                    "Atomic pair was not suspended with mitigations.");
                brokerJob.BindSingleCandidate(identity, owner.SafeHandle, binding.Identity, retained);
                privatePeer!.BindSingleCandidate(identity, owner.SafeHandle, binding.Identity, retained);
                Check(brokerJob.ContainsFixtureRoot(binding.Identity, retained) &&
                    privatePeer.ContainsFixtureRoot(binding.Identity, retained),
                    "Creation-time Job pair did not retain the original root.");
                bool reuseRefused = false;
                try { using var invalid = brokerJob.CreateAtomicPairFixture(privatePeer, image, environment); }
                catch (UnauthorizedAccessException) { reuseRefused = true; }
                Check(reuseRefused && !File.Exists(markerPath), "Atomic pair reused before resume.");
            }
            else
            {
                brokerJob.AssignVerifiedCandidate(identity, owner.SafeHandle, binding.Identity, retained);
                privatePeer?.AssignVerifiedCandidate(identity, owner.SafeHandle, binding.Identity, retained);
            }
            // Mode 6 adds both private Jobs at CreateProcess time; mode 5
            // retains the previous post-creation assignment fixture.
            if (privatePeer is not null)
                brokerJob.RequirePrivateNestedBinding(privatePeer, identity, owner.SafeHandle, binding.Identity, retained);
            brokerJob.RequireInitialNestedBinding(candidate.FixtureJobHandle,
                identity, owner.SafeHandle, binding.Identity, retained);
            Check(brokerJob.ContainsVerifiedCandidate(binding.Identity, retained) == (mode == 0),
                "Fixture job was mistaken for disabled preflight admission.");
            candidate.ResumeAfterVerification(() => mode == 0
                ? brokerJob.ContainsVerifiedCandidate(binding.Identity, retained)
                : brokerJob.ContainsFixtureRoot(binding.Identity, retained) &&
                    (privatePeer is null || privatePeer.ContainsFixtureRoot(binding.Identity, retained)));
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
                var descendantHandle = descendant.SafeHandle;
                var descendantIdentity = Native.ObserveProcess(descendantHandle);
                string? refusal = brokerJob.FixturePairRefusal(binding.Identity, retained, descendantIdentity, descendantHandle);
                Check(refusal is null, $"Fixture pair refused (mode {mode}): {refusal}");

                // A real retained-object snapshot of the broker-owned job.
                // The external owner and a reused identity must not pass
                // even if the number of supplied handles is correct.
                var snapshot = brokerJob.ObserveRetainedMembers([
                    (binding.Identity, retained), (descendantIdentity, descendantHandle)]);
                Check(snapshot.Length == 2 && snapshot[0] == binding.Identity &&
                    snapshot[1] == descendantIdentity, "Retained job snapshot mismatch.");
                void RefuseObservation(Action observe)
                {
                    try { observe(); }
                    catch (UnauthorizedAccessException) { return; }
                    throw new InvalidOperationException("Invalid retained job observation accepted.");
                }
                RefuseObservation(() => brokerJob.RequireInitialNestedBinding(candidate.FixtureJobHandle,
                    identity, owner.SafeHandle, binding.Identity, retained));
                RefuseObservation(() => brokerJob.ObserveRetainedMembers([(binding.Identity, retained)]));
                RefuseObservation(() => brokerJob.ObserveRetainedMembers([
                    (binding.Identity, retained), (binding.Identity, retained)]));
                RefuseObservation(() => brokerJob.ObserveRetainedMembers([
                    (binding.Identity, retained), (identity, owner.SafeHandle)]));
                RefuseObservation(() => brokerJob.ObserveRetainedMembers([
                    (binding.Identity, retained),
                    (descendantIdentity with { CreatedFileTime = descendantIdentity.CreatedFileTime + 1 }, descendantHandle)]));
                RefuseObservation(() => brokerJob.ObserveRetainedMembers([
                    (binding.Identity, retained), (descendantIdentity, retained)]));

                var nestedSnapshot = brokerJob.ObserveNestedRetainedMembers(candidate.FixtureJobHandle,
                    [(binding.Identity, retained), (descendantIdentity, descendantHandle)]);
                Check(nestedSnapshot.SequenceEqual(snapshot), "Nested Job snapshot differs.");
                int diagnosticCalls = 0;
                var diagnosticPids = brokerJob.ObserveNestedRetainedDiagnostics(candidate.FixtureJobHandle,
                    [(binding.Identity, retained), (descendantIdentity, descendantHandle)],
                    (expected, handle) =>
                    {
                        Check(Native.ObserveProcess(handle) == expected, "Diagnostic got wrong retained object.");
                        diagnosticCalls++;
                        return expected.ProcessId;
                    });
                Check(diagnosticCalls == 2 && diagnosticPids.SequenceEqual(snapshot.Select(item => item.ProcessId)),
                    "Nested diagnostics did not cover both original members.");
                RefuseObservation(() => brokerJob.ObserveNestedRetainedDiagnostics(candidate.FixtureJobHandle,
                    [(binding.Identity, retained)],
                    (expected, _) => { diagnosticCalls++; return expected.ProcessId; }));
                Check(diagnosticCalls == 2, "Incomplete membership reached diagnostic callback.");
                var openedByCollector = new List<Microsoft.Win32.SafeHandles.SafeProcessHandle>();
                var collected = brokerJob.ObserveCollectedNestedDiagnostics(candidate.FixtureJobHandle,
                    binding.Identity, retained, (expected, handle) =>
                    {
                        Check(Native.ObserveProcess(handle) == expected, "Collected handle identity mismatch.");
                        Check(Native.SameLiveProcess(handle, expected == binding.Identity ? retained : descendantHandle),
                            "Collected handle was not the original member object.");
                        openedByCollector.Add(handle);
                        return expected.ProcessId;
                    });
                Check(collected.OrderBy(pid => pid).SequenceEqual(snapshot.Select(item => item.ProcessId).OrderBy(pid => pid)),
                    "Collector did not enumerate the complete nested membership.");
                Check(openedByCollector.Count == 2 && openedByCollector.All(handle => handle.IsClosed) &&
                    Native.IsRunning(retained) && Native.IsRunning(descendantHandle),
                    "Collection did not release only its own handles.");
                RefuseObservation(() => brokerJob.ObserveCollectedNestedDiagnostics(candidate.FixtureJobHandle,
                    binding.Identity, descendantHandle, (expected, _) => expected.ProcessId));
                RefuseObservation(() => brokerJob.ObserveCollectedNestedDiagnostics(candidate.FixtureJobHandle,
                    binding.Identity with { CreatedFileTime = binding.Identity.CreatedFileTime + 1 },
                    retained, (expected, _) => expected.ProcessId));
                using (var unrelated = CefContainedProcess.CreateSuspended(image, environment))
                {
                    RefuseObservation(() => brokerJob.ObserveCollectedNestedDiagnostics(unrelated.FixtureJobHandle,
                        binding.Identity, retained, (expected, _) => expected.ProcessId));
                }
                RefuseObservation(() => brokerJob.ObserveNestedRetainedMembers(candidate.FixtureJobHandle,
                    [(binding.Identity, retained)]));
                RefuseObservation(() => brokerJob.ObserveSameJobFixture(
                    [(binding.Identity, retained), (descendantIdentity, descendantHandle)]));
                RefuseObservation(() => brokerJob.ObserveNestedRetainedMembers(candidate.FixtureJobHandle,
                    [(binding.Identity, retained), (identity, owner.SafeHandle)]));
                Check(!brokerJob.ContainsVerifiedCandidate(binding.Identity, retained),
                    "Multiprocess fixture confused with disabled admission.");
                Check(!brokerJob.ContainsFixturePair(binding.Identity, retained, identity, owner.SafeHandle),
                    "Outside process accepted as fixture descendant.");
                Check(!brokerJob.ContainsFixturePair(binding.Identity, retained, binding.Identity, retained),
                    "Root accepted as its own descendant.");
                Check(brokerJob.FixturePairRefusal(binding.Identity, retained,
                    descendantIdentity with { CreatedFileTime = descendantIdentity.CreatedFileTime + 1 }, descendantHandle)
                    == "descendant-identity", "Altered descendant identity not specifically refused.");
                switch (mode)
                {
                    case 1: brokerJob.Dispose(); break;
                    case 2: candidate.Dispose(); break;
                    case 3:
                        // Force both members to exit DURING the diagnostic.
                        // No result may escape the post-observation checks.
                        var failedCollectionHandles = new List<Microsoft.Win32.SafeHandles.SafeProcessHandle>();
                        RefuseObservation(() => brokerJob.ObserveCollectedNestedDiagnostics(candidate.FixtureJobHandle,
                            binding.Identity, retained, (expected, handle) =>
                            {
                                failedCollectionHandles.Add(handle);
                                Check(brokerJob.TerminateAndDrain(5000), "Broker fixture termination did not drain.");
                                // Zero active accounting is not the same as a signaled
                                // process object. Force both exits to be observable
                                // before allowing the diagnostic to advance.
                                Check(rootObserver.WaitForExit(5000), "Root did not signal during diagnostic.");
                                Check(descendant.WaitForExit(5000), "Descendant did not signal during diagnostic.");
                                return expected.ProcessId;
                            }));
                        Check(failedCollectionHandles.Count == 1,
                            "Diagnostic did not stop after both retained members exited.");
                        Check(failedCollectionHandles.All(handle => handle.IsClosed),
                            "Failed collection leaked its observed process handle.");
                        break;
                    case 4: candidate.TerminateAndDrain(5000); break;
                    case 5:
                    case 6:
                        if (privatePeer is null) throw new InvalidOperationException("Missing private peer.");
                        var privateMembers = brokerJob.ObservePrivateNestedDiagnostics(privatePeer,
                            binding.Identity, retained, (expected, handle) =>
                            {
                                Check(Native.ObserveProcess(handle) == expected, "Private diagnostic identity mismatch.");
                                return expected.ProcessId;
                            });
                        Check(privateMembers.SequenceEqual(snapshot.Select(item => item.ProcessId).Order()),
                            "Private Job pair did not observe both members.");
                        RefuseObservation(() => brokerJob.RequirePrivateNestedBinding(privatePeer,
                            identity, owner.SafeHandle, binding.Identity, retained));
                        RefuseObservation(() => brokerJob.ObservePrivateNestedDiagnostics(brokerJob,
                            binding.Identity, retained, (expected, _) => expected.ProcessId));
                        RefuseObservation(() => brokerJob.ObservePrivateNestedDiagnostics(privatePeer,
                            binding.Identity, rootObserver.SafeHandle, (expected, _) => expected.ProcessId));
                        RefuseObservation(() => brokerJob.TerminateAndVerifyPrivateDrain(privatePeer, owner.SafeHandle, 0));
                        using (var substitute = CefBrokerOwnedJob.CreateMultiprocessFixture())
                        {
                            RefuseObservation(() => brokerJob.TerminateAndVerifyPrivateDrain(substitute, retained, 0));
                        }
                        Check(!rootObserver.HasExited && !descendant.HasExited, "Rejected private drain stopped members.");
                        Check(brokerJob.TerminateAndVerifyPrivateDrain(privatePeer, retained, 5000),
                            "Private Job pair did not drain.");
                        Check(brokerJob.TerminateAndVerifyPrivateDrain(privatePeer, retained, 5000),
                            "Private Job pair did not verify repeated drain.");
                        Check(brokerJob.TerminateAndVerifyNestedDrain(candidate.FixtureJobHandle, retained, 5000),
                            "Nested drain did not verify both jobs and retained root exit.");
                        Check(brokerJob.TerminateAndVerifyNestedDrain(candidate.FixtureJobHandle, retained, 5000),
                            "Repeated nested drain did not verify.");
                        Check(!brokerJob.TerminateAndVerifyNestedDrain(candidate.FixtureJobHandle, owner.SafeHandle, 0),
                            "Live unrelated owner was accepted as exited root.");
                        using (var unrelated = CefContainedProcess.CreateSuspended(image, environment))
                        {
                            Check(!brokerJob.TerminateAndVerifyNestedDrain(unrelated.FixtureJobHandle, retained, 0),
                                "Nonempty unrelated desktop job was accepted.");
                            Check(unrelated.IsAlive, "Drain terminated an unrelated desktop job.");
                        }
                        break;
                }
                Check(descendant.WaitForExit(10000), $"Descendant survived fixture cleanup (mode {mode}).");
                Check(rootObserver.WaitForExit(10000), $"Root survived fixture cleanup (mode {mode}).");
                if (mode >= 3)
                    Check(rootObserver.ExitCode == CefContainedProcess.ForcedTerminationExit &&
                        descendant.ExitCode == CefContainedProcess.ForcedTerminationExit, "Unexpected forced exit code.");
                if (mode != 1) Check(SpinWait.SpinUntil(brokerJob.HasNoActiveProcesses, TimeSpan.FromSeconds(5)), "Broker fixture job did not drain.");
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


// Fixture wrapper borrows handles from the surrounding using scope; the slot
// controls retirement/drain, while the fixture scope disposes after exit proof.
// This does not claim production resource ownership transfer or CEF admission.
internal sealed class HandoffFixtureLifetime(CefContainedProcess child,
    CefBrokerOwnedJob outer, CefBrokerOwnedJob inner,
    Microsoft.Win32.SafeHandles.SafeProcessHandle root) : ICefDiagnosticLifetime
{
    private bool retired;
    public void Revoke() => retired = true;
    public bool RevokeAndRelease(int milliseconds)
    {
        if (!retired) throw new InvalidOperationException("Fixture not retired.");
        return outer.TerminateAndVerifyPrivateDrain(inner, root, milliseconds) &&
            child.WaitForNaturalJobExit(milliseconds);
    }
}

// The new slot owner delegates cleanup to the original custody owner.
internal sealed class BorrowingFixtureSuccessor(CefPreparedCreationLifetime owner) : ICefDiagnosticLifetime
{
    public void Revoke() => owner.Revoke();
    public bool RevokeAndRelease(int milliseconds) => owner.RevokeAndRelease(milliseconds);
}

internal sealed class RevocationFaultFixture(Exception? revokeFailure) : ICefDiagnosticLifetime
{
    internal bool AllowRelease { get; set; }
    public void Revoke()
    {
        if (revokeFailure is not null) throw revokeFailure;
    }
    public bool RevokeAndRelease(int milliseconds) => AllowRelease;
}
