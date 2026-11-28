// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Veil.VolumeBroker;

// Broker-owned disabled candidate job. Never accepts a client-provided job object.
// The disabled preflight gets a single-process job, not a browser job.
// All production calls are serialized by the owning broker.
internal sealed class CefBrokerOwnedJob : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly uint activeLimit;
    private bool assignmentIssued;
    private bool assignmentExported;
    private CefBrokerOwnedJob? privatePeer;
    private SafeProcessHandle? privateRoot;
    private ClientIdentity? privateRootIdentity;
    private bool bound;
    private bool disposed;

    private CefBrokerOwnedJob(SafeFileHandle job, uint activeLimit)
    {
        this.job = job;
        this.activeLimit = activeLimit;
    }

    internal static CefBrokerOwnedJob CreateDisabledCandidate() => Create(1);

#if VEIL_CEF_CONTAINMENT_TEST
    // Linked into the isolated test app only. No IPC, product option or
    // production build can select this two-process limit.
    internal static CefBrokerOwnedJob CreateMultiprocessFixture() => Create(2);

    // Same-user synthetic apphost only. Both privately created broker Jobs
    // enter the creation attribute before the root thread can execute.
    // No handle export, IPC, elevated-token launch or product call exists.
    internal Veil.Desktop.CefContainedProcess CreateAtomicPairFixture(CefBrokerOwnedJob peer,
        string image, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (ReferenceEquals(this, peer) || disposed || peer.disposed ||
            activeLimit != 2 || peer.activeLimit != 2 ||
            assignmentIssued || peer.assignmentIssued || bound || peer.bound ||
            CompareJobObjects(job, peer.job) ||
            Accounting().TotalProcesses != 0 || peer.Accounting().TotalProcesses != 0)
            throw new UnauthorizedAccessException("Atomic fixture Jobs must be fresh and distinct.");
        RequireContainmentLimits(job, activeLimit);
        RequireContainmentLimits(peer.job, peer.activeLimit);
        // Mark both consumed even if creation fails; no alternate assignment
        // or retry with the same objects. The caller retains both Jobs.
        assignmentIssued = peer.assignmentIssued = true;
        return Veil.Desktop.CefContainedProcess.CreateSuspendedWithFixtureJobs(image, environment, job, peer.job);
    }

    internal Veil.Desktop.CefContainedProcess CreateAtomicUserPairFixture(CefBrokerOwnedJob peer,
        CefProcessTokenObserver.LaunchTokenLease lease, string image, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(peer);
        if (ReferenceEquals(this, peer) || disposed || peer.disposed ||
            activeLimit != 2 || peer.activeLimit != 2 ||
            assignmentIssued || peer.assignmentIssued || bound || peer.bound ||
            CompareJobObjects(job, peer.job) ||
            Accounting().TotalProcesses != 0 || peer.Accounting().TotalProcesses != 0)
            throw new UnauthorizedAccessException("Atomic user fixture Jobs must be fresh and distinct.");
        RequireContainmentLimits(job, activeLimit);
        RequireContainmentLimits(peer.job, peer.activeLimit);
        lease.RequireCurrent();
        assignmentIssued = peer.assignmentIssued = true;
        return lease.CreateUserFixture(image, environment, job, peer.job);
    }

    internal ClientIdentity[] ObserveSameJobFixture(
        IReadOnlyList<(ClientIdentity Identity, SafeProcessHandle Handle)> members) =>
        ObserveNestedRetainedMembers(job, members);

    internal bool ContainsFixtureRoot(ClientIdentity child, SafeProcessHandle childHandle) =>
        FixtureRootRefusal(child, childHandle) is null;

    private string? FixtureRootRefusal(ClientIdentity child, SafeProcessHandle childHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeLimit != 2 || !bound) return "root-job-not-bound";
        if (!Native.IsRunning(childHandle)) return "root-exited";
        if (Native.ObserveProcess(childHandle) != child) return "root-identity";
        if (!IsProcessInJob(childHandle, job, out bool inside))
            return $"root-membership-query: win32={Marshal.GetLastWin32Error()}";
        if (!inside) return "root-not-in-broker-job";
        var state = Accounting();
        return state.ActiveProcesses is >= 1 and <= 2 ? null : $"root-accounting: {FormatAccounting(state)}";
    }

    private static string FormatAccounting(AccountingInfo state) =>
        $"active={state.ActiveProcesses}, total={state.TotalProcesses}, terminated={state.TotalTerminatedProcesses}";

    // Stable synthetic pair only. Retain handles and verify identities on
    // either side of accounting; a PID in a marker is not membership proof.
    internal bool ContainsFixturePair(ClientIdentity root, SafeProcessHandle rootHandle,
        ClientIdentity descendant, SafeProcessHandle descendantHandle) =>
        FixturePairRefusal(root, rootHandle, descendant, descendantHandle) is null;

    // Diagnostic only in the synthetic test build. Refusal categories contain
    // no paths, user identifiers, or process handles; do not weaken a check.
    internal string? FixturePairRefusal(ClientIdentity root, SafeProcessHandle rootHandle,
        ClientIdentity descendant, SafeProcessHandle descendantHandle)
    {
        if (FixtureRootRefusal(root, rootHandle) is { } rootRefusal) return rootRefusal;
        if (root.ProcessId == descendant.ProcessId) return "duplicate-process";
        if (root.SessionId != descendant.SessionId) return "session";
        if (root.UserSid != descendant.UserSid) return "user";
        if (!string.Equals(root.ImagePath, descendant.ImagePath, StringComparison.OrdinalIgnoreCase)) return "image";
        if (descendant.CreatedFileTime < root.CreatedFileTime) return "creation-order";
        if (!Native.IsRunning(descendantHandle)) return "descendant-exited";
        if (Native.ObserveProcess(descendantHandle) != descendant) return "descendant-identity";
        if (!IsProcessInJob(descendantHandle, job, out bool inside)) return "membership-query";
        if (!inside) return "descendant-not-in-broker-job";
        var state = Accounting();
        if (!(state.ActiveProcesses == 2 && state.TotalProcesses == 2))
            return $"accounting: {FormatAccounting(state)}";
        if (FixtureRootRefusal(root, rootHandle) is { } recheckRefusal) return $"recheck: {recheckRefusal}";
        if (!Native.IsRunning(descendantHandle)) return "descendant-exited-recheck";
        if (Native.ObserveProcess(descendantHandle) != descendant) return "descendant-identity-recheck";
        if (!IsProcessInJob(descendantHandle, job, out inside) || !inside) return "membership-recheck";
        return null;
    }
#endif

    private static CefBrokerOwnedJob Create(uint activeLimit)
    {
#if VEIL_CEF_CONTAINMENT_TEST
        if (activeLimit != 1 && activeLimit != 2) throw new ArgumentOutOfRangeException(nameof(activeLimit));
#else
        if (activeLimit != 1) throw new ArgumentOutOfRangeException(nameof(activeLimit));
#endif
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
        try
        {
            var limits = new ExtendedLimits();
            // ACTIVE_PROCESS + KILL_ON_JOB_CLOSE. Neither breakaway flag.
            limits.Basic.Flags = 0x00000008 | 0x00002000;
            limits.Basic.ActiveLimit = activeLimit;
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception();
            return new CefBrokerOwnedJob(handle, activeLimit);
        }
        catch { handle.Dispose(); throw; }
    }

    internal long IssueAssignmentHandle(ClientIdentity owner, SafeProcessHandle observedOwner)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (assignmentIssued || Accounting().TotalProcesses != 0)
            throw new InvalidOperationException("CEF job assignment already issued.");
        assignmentIssued = true; // Never retry even if the reply is ambiguous.
        assignmentExported = true; // Even a failed duplication disqualifies private ownership.
        return Native.DuplicateCefJobAssignment(job, owner, observedOwner);
    }

    // Prefer broker-side assignment over transferring assignment rights to
    // the desktop. Caller must already have verified the nominated child's
    // signed image, runtime registration, and authenticated owner. The local
    // desktop job has already been installed atomically at suspended creation.
    internal void AssignVerifiedCandidate(ClientIdentity owner, SafeProcessHandle ownerHandle,
        ClientIdentity child, SafeProcessHandle childHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (assignmentIssued || bound || Accounting().TotalProcesses != 0 ||
            owner.ProcessId == child.ProcessId || Native.ObserveProcess(ownerHandle) != owner ||
            Native.ObserveProcess(childHandle) != child ||
            !IsProcessInJob(ownerHandle, job, out bool ownerInside) || ownerInside)
            throw new UnauthorizedAccessException("CEF job assignment was not authorized.");
        using var assignment = Native.OpenCefAssignmentHandle(child, childHandle);
        assignmentIssued = true; // No retry or alternate job after failure.
        if (!AssignProcessToJobObject(job, assignment)) throw new Win32Exception();
        BindSingleCandidate(owner, ownerHandle, child, childHandle);
    }

    internal void BindSingleCandidate(ClientIdentity owner, SafeProcessHandle ownerHandle,
        ClientIdentity child, SafeProcessHandle childHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!assignmentIssued || bound || owner.ProcessId == child.ProcessId ||
            Native.ObserveProcess(ownerHandle) != owner || Native.ObserveProcess(childHandle) != child ||
            !IsProcessInJob(ownerHandle, job, out bool ownerInside) || ownerInside ||
            !IsProcessInJob(childHandle, job, out bool childInside) || !childInside)
            throw new UnauthorizedAccessException("CEF candidate is not the exclusive nominated job member.");
        var state = Accounting();
        if (state.TotalProcesses != 1 || state.ActiveProcesses != 1 ||
            Native.ObserveProcess(ownerHandle) != owner || Native.ObserveProcess(childHandle) != child)
            throw new UnauthorizedAccessException("CEF job membership changed.");
        bound = true;
    }

    internal bool ContainsVerifiedCandidate(ClientIdentity child, SafeProcessHandle childHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeLimit != 1 || !bound || !Native.IsRunning(childHandle) || Native.ObserveProcess(childHandle) != child ||
            !IsProcessInJob(childHandle, job, out bool inside) || !inside) return false;
        var state = Accounting();
        return state.TotalProcesses == 1 && state.ActiveProcesses == 1;
    }

    // Production-shaped diagnostics accept only another privately created
    // broker Job object, never a caller-supplied native handle. The original
    // desktop containment remains separate and is not attested by this pair.
    private SafeFileHandle RequirePrivatePeer(CefBrokerOwnedJob peer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(peer);
        if (ReferenceEquals(this, peer) || peer.disposed || !bound || !peer.bound ||
            assignmentExported || peer.assignmentExported || activeLimit != peer.activeLimit ||
            CompareJobObjects(job, peer.job))
            throw new UnauthorizedAccessException("CEF private Job pair refused.");
        return peer.job;
    }

    internal void RequirePrivateNestedBinding(CefBrokerOwnedJob peer,
        ClientIdentity owner, SafeProcessHandle ownerHandle,
        ClientIdentity root, SafeProcessHandle rootHandle)
    {
        var peerHandle = RequirePrivatePeer(peer);
        if (privatePeer is not null || peer.privatePeer is not null)
            throw new UnauthorizedAccessException("CEF private Job pair already bound.");
        RequireContainmentLimits(peerHandle, peer.activeLimit);
        RequireInitialNestedBinding(peerHandle, owner, ownerHandle, root, rootHandle);
        RequireContainmentLimits(peerHandle, peer.activeLimit);
        // Bind once to the exact retained objects. Neither a different peer
        // nor a reused PID can subsequently authorize a diagnostic or drain.
        privatePeer = peer;
        peer.privatePeer = this;
        privateRoot = peer.privateRoot = rootHandle;
        privateRootIdentity = peer.privateRootIdentity = root;
    }

    private SafeFileHandle RequireBoundPrivatePeer(CefBrokerOwnedJob peer, SafeProcessHandle rootHandle)
    {
        var peerHandle = RequirePrivatePeer(peer);
        if (!ReferenceEquals(privatePeer, peer) || !ReferenceEquals(peer.privatePeer, this) ||
            !ReferenceEquals(privateRoot, rootHandle) || !ReferenceEquals(peer.privateRoot, rootHandle))
            throw new UnauthorizedAccessException("CEF private Job lifetime binding mismatch.");
        return peerHandle;
    }

    internal T[] ObservePrivateNestedDiagnostics<T>(CefBrokerOwnedJob peer,
        ClientIdentity root, SafeProcessHandle rootHandle, Func<ClientIdentity, SafeProcessHandle, T> observe)
    {
        var peerHandle = RequireBoundPrivatePeer(peer, rootHandle);
        if (privateRootIdentity != root)
            throw new UnauthorizedAccessException("CEF private Job root identity mismatch.");
        RequireContainmentLimits(job, activeLimit);
        RequireContainmentLimits(peerHandle, peer.activeLimit);
        var result = ObserveCollectedNestedDiagnostics(peerHandle, root, rootHandle, observe);
        RequireContainmentLimits(job, activeLimit);
        RequireContainmentLimits(peerHandle, peer.activeLimit);
        return result;
    }

    internal bool TerminateAndVerifyPrivateDrain(CefBrokerOwnedJob peer,
        SafeProcessHandle rootHandle, int milliseconds) =>
        TerminateAndVerifyNestedDrain(RequireBoundPrivatePeer(peer, rootHandle), rootHandle, milliseconds);

    // Validate the initial containment pair before ownership is transferred.
    // The desktop Job must already come from a separately trusted source.
    // Membership and limits alone cannot establish who created that Job.
    internal void RequireInitialNestedBinding(SafeFileHandle desktopJob,
        ClientIdentity owner, SafeProcessHandle ownerHandle,
        ClientIdentity root, SafeProcessHandle rootHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(desktopJob);
        if (!bound || desktopJob.IsClosed || desktopJob.IsInvalid ||
            CompareJobObjects(job, desktopJob) || owner.ProcessId == root.ProcessId ||
            owner.SessionId != root.SessionId ||
            !string.Equals(owner.UserSid, root.UserSid, StringComparison.OrdinalIgnoreCase) ||
            root.CreatedFileTime < owner.CreatedFileTime ||
            ObserveLiveMember(ownerHandle) != owner || ObserveLiveMember(rootHandle) != root)
            throw new UnauthorizedAccessException("CEF initial nested binding identity refused.");

        void RequireInitialState()
        {
            if (!IsProcessInJob(ownerHandle, job, out bool inBroker) || inBroker ||
                !IsProcessInJob(ownerHandle, desktopJob, out bool inDesktop) || inDesktop)
                throw new UnauthorizedAccessException("CEF owner is not outside both jobs.");
            var brokerState = Accounting();
            var desktopState = Accounting(desktopJob);
            if (brokerState.ActiveProcesses != 1 || brokerState.TotalProcesses != 1 ||
                brokerState.TotalTerminatedProcesses != 0 ||
                desktopState.ActiveProcesses != 1 || desktopState.TotalProcesses != 1 ||
                desktopState.TotalTerminatedProcesses != 0)
                throw new UnauthorizedAccessException("CEF initial nested jobs must contain only the original root.");
            RequireContainmentLimits(job, activeLimit);
            RequireContainmentLimits(desktopJob, null);
        }

        RequireInitialState();
        _ = ObserveNestedRetainedMembers(desktopJob, [(root, rootHandle)]);
        RequireInitialState();
        if (ObserveLiveMember(ownerHandle) != owner || ObserveLiveMember(rootHandle) != root)
            throw new UnauthorizedAccessException("CEF initial nested binding changed.");
    }

    private static void RequireContainmentLimits(SafeFileHandle observedJob, uint? expectedCap)
    {
        if (!QueryJobLimits(observedJob, 9, out var limits,
            (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero))
            throw new UnauthorizedAccessException("CEF job limits unavailable.", new Win32Exception());
        const uint killOnClose = 0x00002000, breakaway = 0x00000800 | 0x00001000;
        uint flags = limits.Basic.Flags;
        if ((flags & killOnClose) == 0 || (flags & breakaway) != 0 ||
            (expectedCap is uint cap && ((flags & 0x00000008) == 0 || limits.Basic.ActiveLimit != cap)))
            throw new UnauthorizedAccessException("CEF job containment limits refused.");
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryJobLimits(SafeFileHandle job, int infoClass, out ExtendedLimits limits,
        uint length, IntPtr returned);

    // P2-03 observation primitive, not a launch grant. The serialized broker
    // must supply retained process handles obtained from its own observations.
    // This verifies one job only, not the second job, module pins, role
    // policy, or events between observations. No handles are transferred.
    internal ClientIdentity[] ObserveRetainedMembers(
        IReadOnlyList<(ClientIdentity Identity, SafeProcessHandle Handle)> members)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(members);
        if (!bound || members.Count == 0 || (uint)members.Count > activeLimit)
            throw new UnauthorizedAccessException("CEF job observation unavailable.");

        // Freeze caller's list within the serialized broker operation.
        var retained = members.ToArray();
        var before = Accounting();
        if (before.ActiveProcesses != (uint)retained.Length)
            throw new UnauthorizedAccessException("Incomplete CEF job membership.");

        var pids = new HashSet<int>();
        foreach (var member in retained)
        {
            if (member.Identity is null || member.Handle is null ||
                !pids.Add(member.Identity.ProcessId))
                throw new UnauthorizedAccessException("Invalid CEF job observation.");
            CheckRetainedMember(member.Identity, member.Handle);
        }
        foreach (var member in retained)
            CheckRetainedMember(member.Identity, member.Handle);

        var after = Accounting();
        if (after.ActiveProcesses != before.ActiveProcesses ||
            after.TotalProcesses != before.TotalProcesses ||
            after.TotalTerminatedProcesses != before.TotalTerminatedProcesses)
            throw new UnauthorizedAccessException("CEF job membership changed during observation.");

        return retained.Select(member => member.Identity).ToArray();
    }

    // Disconnected P2-03 primitive. The second handle must be a trusted,
    // independently owned desktop Job retained by the serialized observer.
    // Never obtain it from a client message or expose this as launch authority.
    internal ClientIdentity[] ObserveNestedRetainedMembers(SafeFileHandle desktopJob,
        IReadOnlyList<(ClientIdentity Identity, SafeProcessHandle Handle)> members) =>
        ObserveNestedRetainedDiagnostics(desktopJob, members, (identity, _) => identity);

    // Run broker-internal read-only diagnostics inside the SAME two-job
    // accounting bracket. The caller must serialize and retain both jobs and
    // the original process handles. Results are observations, not admission.
    // The callback must not be supplied by a client or publish partial results.
    internal T[] ObserveNestedRetainedDiagnostics<T>(SafeFileHandle desktopJob,
        IReadOnlyList<(ClientIdentity Identity, SafeProcessHandle Handle)> members,
        Func<ClientIdentity, SafeProcessHandle, T> observe)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(desktopJob);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(observe);
        if (desktopJob.IsInvalid || desktopJob.IsClosed || CompareJobObjects(job, desktopJob))
            throw new UnauthorizedAccessException("Expected two distinct retained CEF jobs.");
        var retained = members.ToArray();
        var brokerBefore = Accounting();
        var desktopBefore = Accounting(desktopJob);
        if (desktopBefore.ActiveProcesses != (uint)retained.Length)
            throw new UnauthorizedAccessException("Incomplete desktop CEF job membership.");
        _ = ObserveRetainedMembers(retained);
        void RequireNestedMembers()
        {
            foreach (var member in retained)
            {
                CheckRetainedMember(member.Identity, member.Handle);
                if (!IsProcessInJob(member.Handle, desktopJob, out bool inside) || !inside ||
                    ObserveLiveMember(member.Handle) != member.Identity)
                    throw new UnauthorizedAccessException("CEF member missing from desktop job.");
            }
        }
        RequireNestedMembers();
        var diagnostics = new T[retained.Length];
        for (int i = 0; i < retained.Length; i++)
        {
            CheckRetainedMember(retained[i].Identity, retained[i].Handle);
            diagnostics[i] = observe(retained[i].Identity, retained[i].Handle);
        }
        RequireNestedMembers();
        // Bracket both jobs, including historical counters so equal active
        // counts cannot hide observed member creation/termination churn.
        var desktopAfter = Accounting(desktopJob);
        var brokerAfter = Accounting();
        if (!SameMembershipAccounting(desktopBefore, desktopAfter) ||
            !SameMembershipAccounting(brokerBefore, brokerAfter))
            throw new UnauthorizedAccessException("Nested CEF membership changed during observation.");
        return diagnostics;
    }

    // Collect only from the two trusted Job objects, never client-supplied
    // PIDs. A PID is a lookup hint; retained live identity, membership and
    // unchanged accounting/list observations are required before returning.
    // The original browser root handle remains an independent anchor.
    internal T[] ObserveCollectedNestedDiagnostics<T>(SafeFileHandle desktopJob,
        ClientIdentity root, SafeProcessHandle rootHandle,
        Func<ClientIdentity, SafeProcessHandle, T> observe)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(desktopJob);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHandle);
        ArgumentNullException.ThrowIfNull(observe);
        if (!bound || desktopJob.IsInvalid || desktopJob.IsClosed ||
            CompareJobObjects(job, desktopJob) || ObserveLiveMember(rootHandle) != root)
            throw new UnauthorizedAccessException("CEF collection root or jobs unavailable.");

        var brokerBefore = Accounting();
        var desktopBefore = Accounting(desktopJob);
        var pids = QueryMemberPids(job, brokerBefore.ActiveProcesses);
        if (!pids.Contains(root.ProcessId) ||
            !pids.SequenceEqual(QueryMemberPids(desktopJob, desktopBefore.ActiveProcesses)))
            throw new UnauthorizedAccessException("CEF job process lists differ.");

        var retained = new List<(ClientIdentity Identity, SafeProcessHandle Handle)>();
        try
        {
            foreach (int pid in pids)
            {
                // PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE only.
                var handle = OpenObservedProcess(0x00101000, false, (uint)pid);
                try
                {
                    if (handle.IsInvalid) throw new Win32Exception();
                    var identity = ObserveLiveMember(handle);
                    if (identity.ProcessId != pid || identity.SessionId != root.SessionId ||
                        !string.Equals(identity.UserSid, root.UserSid, StringComparison.OrdinalIgnoreCase) ||
                        identity.CreatedFileTime < root.CreatedFileTime ||
                        !string.Equals(identity.ImagePath, root.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                        (pid == root.ProcessId && (identity != root || !Native.SameLiveProcess(rootHandle, handle))))
                        throw new UnauthorizedAccessException("CEF collected member identity mismatch.");
                    CheckRetainedMember(identity, handle);
                    retained.Add((identity, handle));
                }
                catch { handle.Dispose(); throw; }
            }

            var result = ObserveNestedRetainedDiagnostics(desktopJob, retained, observe);
            if (!pids.SequenceEqual(QueryMemberPids(job, brokerBefore.ActiveProcesses)) ||
                !pids.SequenceEqual(QueryMemberPids(desktopJob, desktopBefore.ActiveProcesses)) ||
                !SameMembershipAccounting(brokerBefore, Accounting()) ||
                !SameMembershipAccounting(desktopBefore, Accounting(desktopJob)) ||
                ObserveLiveMember(rootHandle) != root)
                throw new UnauthorizedAccessException("CEF membership changed during collection.");
            foreach (var member in retained)
                CheckRetainedMember(member.Identity, member.Handle);
            return result;
        }
        finally
        {
            foreach (var member in retained) member.Handle.Dispose();
        }
    }

    private int[] QueryMemberPids(SafeFileHandle observedJob, uint expected)
    {
        if (!Environment.Is64BitProcess || expected == 0 || expected > activeLimit)
            throw new UnauthorizedAccessException("Invalid CEF collection size.");
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts, then ULONG_PTRs.
        // Fixed capacity follows the existing broker cap; never retry with a
        // larger buffer or relax the production one-process limit.
        int bytes = checked(8 + (int)activeLimit * IntPtr.Size);
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!QueryJobProcessList(observedJob, 3, buffer, (uint)bytes, IntPtr.Zero))
                throw new Win32Exception();
            uint assigned = unchecked((uint)Marshal.ReadInt32(buffer));
            uint listed = unchecked((uint)Marshal.ReadInt32(buffer, 4));
            if (assigned != expected || listed != expected)
                throw new UnauthorizedAccessException("Incomplete CEF job process list.");
            var result = new int[(int)listed];
            var seen = new HashSet<int>();
            for (int i = 0; i < result.Length; i++)
            {
                long pid = Marshal.ReadInt64(buffer, 8 + i * IntPtr.Size);
                if (pid <= 0 || pid > int.MaxValue || !seen.Add((int)pid))
                    throw new UnauthorizedAccessException("Invalid or duplicate CEF member PID.");
                result[i] = (int)pid;
            }
            Array.Sort(result);
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryJobProcessList(SafeFileHandle job, int infoClass, IntPtr info, uint length, IntPtr returned);

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern SafeProcessHandle OpenObservedProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    private static bool SameMembershipAccounting(AccountingInfo before, AccountingInfo after) =>
        before.ActiveProcesses == after.ActiveProcesses && before.TotalProcesses == after.TotalProcesses &&
        before.TotalTerminatedProcesses == after.TotalTerminatedProcesses;

    [DllImport("kernelbase.dll", EntryPoint = "CompareObjectHandles", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareJobObjects(SafeFileHandle first, SafeFileHandle second);

    private void CheckRetainedMember(ClientIdentity identity, SafeProcessHandle handle)
    {
        if (!Native.IsRunning(handle) || ObserveLiveMember(handle) != identity ||
            !IsProcessInJob(handle, job, out bool inside) || !inside ||
            ObserveLiveMember(handle) != identity)
            throw new UnauthorizedAccessException("CEF retained job member did not match.");
    }

    // The process can enter termination between a successful wait check and
    // an identity query. Native errors are observation refusals, never proof
    // that a process exited. Preserve the native exception for diagnostics.
    private static ClientIdentity ObserveLiveMember(SafeProcessHandle handle)
    {
        try { return Native.ObserveProcess(handle); }
        catch (Win32Exception error)
        {
            throw new UnauthorizedAccessException("CEF live member identity could not be verified.", error);
        }
    }

    internal bool HasNoActiveProcesses()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Accounting().ActiveProcesses == 0;
    }

    // Only this internally created job can be terminated. A false result
    // requires keeping its handle and retrying; not a proof of exit/relock.
    internal bool TerminateAndDrain(int milliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        var elapsed = Stopwatch.StartNew();
        if (HasNoActiveProcesses()) return true;
        if (!TerminateJobObject(job, 35)) return HasNoActiveProcesses();
        while (!HasNoActiveProcesses())
        {
            if (elapsed.ElapsedMilliseconds >= milliseconds) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    // Disconnected drain primitive. Both Job handles must already be
    // authenticated, distinct and retained for the same session. No client
    // supplied handles and no volume unlock/relock authority here.
    internal bool TerminateAndVerifyNestedDrain(SafeFileHandle desktopJob,
        SafeProcessHandle rootHandle, int milliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(desktopJob);
        ArgumentNullException.ThrowIfNull(rootHandle);
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        if (!bound || desktopJob.IsInvalid || desktopJob.IsClosed ||
            rootHandle.IsInvalid || rootHandle.IsClosed || CompareJobObjects(job, desktopJob))
            return false;
        // Terminate only the broker-owned Job. A live member outside it
        // must not be killed by trusting an arbitrary desktop Job handle.
        var elapsed = Stopwatch.StartNew();
        if (!TerminateAndDrain(milliseconds)) return false;
        // Job accounting can reach zero before the retained process object
        // is signaled. Use only the remaining shared timeout, not a second
        // full timeout, and never interpret WAIT_FAILED as process exit.
        int remaining = (int)Math.Max(0L, milliseconds - elapsed.ElapsedMilliseconds);
        if (WaitForObservedRoot(rootHandle, (uint)remaining) != 0) return false;
        while (true)
        {
            if (Accounting(desktopJob).ActiveProcesses == 0 &&
                Accounting().ActiveProcesses == 0 && WaitForObservedRoot(rootHandle, 0) == 0)
                return true;
            remaining = (int)Math.Max(0L, milliseconds - elapsed.ElapsedMilliseconds);
            if (remaining == 0) return false;
            Thread.Sleep(Math.Min(20, remaining));
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    private static extern uint WaitForObservedRoot(SafeProcessHandle handle, uint milliseconds);

    private AccountingInfo Accounting() => Accounting(job);

    private static AccountingInfo Accounting(SafeFileHandle observedJob)
    {
        if (!QueryInformationJobObject(observedJob, 1, out var info,
            (uint)Marshal.SizeOf<AccountingInfo>(), IntPtr.Zero))
            throw new Win32Exception();
        return info;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        job.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccountingInfo
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out AccountingInfo info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
