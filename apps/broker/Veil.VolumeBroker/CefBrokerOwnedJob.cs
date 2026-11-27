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

    internal bool ContainsFixtureRoot(ClientIdentity child, SafeProcessHandle childHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeLimit != 2 || !bound || !Native.IsRunning(childHandle) ||
            Native.ObserveProcess(childHandle) != child ||
            !IsProcessInJob(childHandle, job, out bool inside) || !inside) return false;
        var state = Accounting();
        return state.ActiveProcesses is >= 1 and <= 2;
    }

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
        if (!ContainsFixtureRoot(root, rootHandle)) return "root-membership";
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
            return $"accounting: active={state.ActiveProcesses}, total={state.TotalProcesses}, terminated={state.TotalTerminatedProcesses}";
        if (!ContainsFixtureRoot(root, rootHandle)) return "root-recheck";
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

    private AccountingInfo Accounting()
    {
        if (!QueryInformationJobObject(job, 1, out var info,
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
