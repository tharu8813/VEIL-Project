// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Veil.Desktop;

// Windows x64 process containment primitive, not a product launch permit.
// A child is assigned to its job atomically at suspended process creation.
// No breakaway, shell, inherited handles, or arbitrary command-line arguments.
internal sealed class CefContainedProcess : IDisposable
{
    // Never reuse the native verified-but-disabled exit code (29) for termination.
    internal const uint ForcedTerminationExit = 35;
    private readonly SafeFileHandle job;
    private readonly SafeProcessHandle process;
    private readonly SafeFileHandle thread;
    private bool resumed;
    private bool disposed;

    private CefContainedProcess(SafeFileHandle job, SafeProcessHandle process, SafeFileHandle thread)
    {
        this.job = job;
        this.process = process;
        this.thread = thread;
    }

#if VEIL_CEF_NESTED_SMOKE || VEIL_CEF_CONTAINMENT_TEST
    internal SafeFileHandle FixtureJobHandle => job;

    internal bool ContainsFixtureMember(SafeProcessHandle member)
    {
        if (!IsProcessInJob(member, job, out bool contained))
            throw Failure("IsProcessInJob");
        return contained;
    }
#endif

    internal static CefContainedProcess CreateSuspended(string image,
        IReadOnlyDictionary<string, string?> environment)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("CEF containment requires Windows x64.");
        if (!Path.IsPathFullyQualified(image) || image.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(image, Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(image), ".exe", StringComparison.OrdinalIgnoreCase) ||
            image.Contains('"') || image.Contains('\0'))
            throw new ArgumentException("Expected an absolute local executable.", nameof(image));
        string block = BuildEnvironmentBlock(environment);
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw Failure("CreateJobObject"); }
        SafeProcessHandle? process = null;
        SafeFileHandle? thread = null;
        try
        {
            var limits = new ExtendedLimitInformation();
            limits.Basic.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw Failure("SetInformationJobObject");
            // PROC_THREAD_ATTRIBUTE_JOB_LIST assigns the job during creation,
            // rather than leaving an orphan window before a separate assignment.
            // Windows 10+ supports this attribute; failure has no legacy fallback.
            using var attributes = new JobAttributeList(job);
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>() },
                Attributes = attributes.Pointer,
            };
            var command = new StringBuilder("\"" + image + "\"");
            const uint flags = 0x00000004 | 0x00000400 | 0x00080000;
            // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT
            IntPtr env = Marshal.StringToHGlobalUni(block);
            ProcessInformation info;
            try
            {
                if (!CreateProcessW(image, command, IntPtr.Zero, IntPtr.Zero, false, flags,
                    env, Path.GetDirectoryName(image)!, ref startup, out info))
                    throw Failure("CreateProcess");
            }
            finally { Marshal.FreeHGlobal(env); }
            process = new SafeProcessHandle(info.Process, ownsHandle: true);
            thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            if (!IsProcessInJob(process, job, out bool contained) || !contained)
                throw new InvalidOperationException("CEF child containment could not be confirmed.");
            if (!HasImageLoadMitigations(process))
                throw new InvalidOperationException("CEF image loading policy could not be confirmed.");
            return new CefContainedProcess(job, process, thread);
        }
        catch
        {
            // The primary thread has never been resumed. Explicit termination
            // is defensive if post-create membership verification failed.
            if (process is { IsInvalid: false, IsClosed: false })
                TerminateProcess(process, ForcedTerminationExit);
            thread?.Dispose();
            job.Dispose();
            process?.Dispose();
            throw;
        }
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var block = new StringBuilder();
        foreach (var entry in environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(entry.Key) || entry.Key.Contains('=') ||
                entry.Key.Contains('\0') || entry.Value is null || entry.Value.Contains('\0') ||
                !seen.Add(entry.Key))
                throw new ArgumentException("Invalid or ambiguous child environment.", nameof(environment));
            block.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
            if (block.Length > 32766)
                throw new ArgumentException("Child environment is too large.", nameof(environment));
        }
        if (block.Length == 0) block.Append('\0');
        return block.Append('\0').ToString();
    }

    // The caller must keep this object alive while nominating this handle.
    // No product broker or startup code currently invokes this primitive.
    internal long NominatedHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (resumed || !IsAlive) throw new InvalidOperationException("Child is not suspended and live.");
            return process.DangerousGetHandle().ToInt64();
        }
    }

    internal bool IsAlive => !disposed && WaitForSingleObject(process, 0) == 0x102;

    internal bool ImageLoadMitigationsActive => !disposed && HasImageLoadMitigations(process);

    private static bool HasImageLoadMitigations(SafeProcessHandle handle)
    {
        // PROCESS_MITIGATION_POLICY.ProcessImageLoadPolicy = 10.
        // Require NoRemoteImages, NoLowMandatoryLabelImages, PreferSystem32Images.
        return GetProcessMitigationPolicy(handle, 10, out uint flags, new UIntPtr(4u)) &&
            (flags & 7u) == 7u;
    }

    internal void ResumeAfterVerification(Func<bool> verified)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(verified);
        if (resumed || !IsAlive) throw new InvalidOperationException("Child cannot be resumed.");
        try
        {
            if (!verified() || !IsProcessInJob(process, job, out bool contained) || !contained ||
                !HasImageLoadMitigations(process) || ResumeThread(thread) != 1)
                throw new InvalidOperationException("CEF child verification or resume failed.");
            resumed = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool WaitForExit(int milliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        uint result = WaitForSingleObject(process, (uint)milliseconds);
        if (result == 0) return true;
        if (result == 0x102) return false;
        throw Failure("WaitForSingleObject");
    }

    internal long ExitedHandle
    {
        get
        {
            if (!WaitForExit(0)) throw new InvalidOperationException("Child has not exited.");
            return process.DangerousGetHandle().ToInt64();
        }
    }

    internal uint ReadExitCode()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!WaitForExit(0) || !GetExitCodeProcess(process, out uint code))
            throw new InvalidOperationException("CEF child exit was not verified.");
        return code;
    }

    private uint ActiveProcesses()
    {
        if (!QueryInformationJobObject(job, 1, out var info,
            (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
            throw Failure("QueryInformationJobObject");
        return info.ActiveProcesses;
    }

    // A successful disabled preflight must leave no live job member. Unlike
    // forced cleanup, this never terminates a process to obtain success.
    internal bool WaitForNaturalJobExit(int milliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (ActiveProcesses() != 0)
        {
            if (started.ElapsedMilliseconds >= milliseconds) return false;
            Thread.Sleep(Math.Min(20, Math.Max(1, milliseconds -
                (int)Math.Min(milliseconds, started.ElapsedMilliseconds))));
        }
        int remaining = Math.Max(0, milliseconds - (int)Math.Min(milliseconds, started.ElapsedMilliseconds));
        return WaitForExit(remaining);
    }

    // Forced cleanup is explicitly different from natural exit verification.
    internal void TerminateAndDrain(int milliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        if (ActiveProcesses() != 0 && !TerminateJobObject(job, ForcedTerminationExit))
            throw Failure("TerminateJobObject");
        if (!WaitForNaturalJobExit(milliseconds))
            throw new TimeoutException("CEF job drain could not be confirmed.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Last job handle closes first. All contained processes are terminated;
        // no assertion of graceful browser shutdown or volume relock is made.
        job.Dispose();
        thread.Dispose();
        process.Dispose();
    }

    private static Win32Exception Failure(string operation) => new(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }

    private sealed class JobAttributeList : IDisposable
    {
        private IntPtr list, value, mitigation;
        private bool initialized;
        private readonly SafeFileHandle job;
        private bool retained;
        internal IntPtr Pointer => list;

        internal JobAttributeList(SafeFileHandle job)
        {
            this.job = job;
            try
            {
                job.DangerousAddRef(ref retained);
                UIntPtr size = UIntPtr.Zero;
                bool unexpected = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                if (unexpected || Marshal.GetLastWin32Error() != 122 ||
                    size.ToUInt64() == 0 || size.ToUInt64() > int.MaxValue)
                    throw Failure("InitializeProcThreadAttributeList size");
                list = Marshal.AllocHGlobal((int)size.ToUInt64());
                if (!InitializeProcThreadAttributeList(list, 2, 0, ref size))
                    throw Failure("InitializeProcThreadAttributeList");
                initialized = true;
                value = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(value, job.DangerousGetHandle());
                // Attribute 13, input flag (0x20000): JOB_LIST.
                if (!UpdateProcThreadAttribute(list, 0, new UIntPtr(0x0002000D),
                    value, new UIntPtr((uint)IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw Failure("UpdateProcThreadAttribute JOB_LIST");
                // PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY (7 | INPUT).
                // IMAGE_LOAD_NO_REMOTE, NO_LOW_LABEL and PREFER_SYSTEM32,
                // ALWAYS_ON at bits 52, 56 and 60. Set at creation, not after resume.
                const long imagePolicy = (1L << 52) | (1L << 56) | (1L << 60);
                mitigation = Marshal.AllocHGlobal(sizeof(long));
                Marshal.WriteInt64(mitigation, imagePolicy);
                if (!UpdateProcThreadAttribute(list, 0, new UIntPtr(0x00020007),
                    mitigation, new UIntPtr(8u), IntPtr.Zero, IntPtr.Zero))
                    throw Failure("UpdateProcThreadAttribute MITIGATION_POLICY");
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (initialized) { DeleteProcThreadAttributeList(list); initialized = false; }
            if (list != IntPtr.Zero) { Marshal.FreeHGlobal(list); list = IntPtr.Zero; }
            if (value != IntPtr.Zero) { Marshal.FreeHGlobal(value); value = IntPtr.Zero; }
            if (mitigation != IntPtr.Zero) { Marshal.FreeHGlobal(mitigation); mitigation = IntPtr.Zero; }
            if (retained) { job.DangerousRelease(); retained = false; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass,
        ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string image, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute,
        IntPtr value, UIntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy,
        out uint flags, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass,
        out BasicAccountingInformation info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
