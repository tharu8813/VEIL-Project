// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Veil.Desktop;

// Interactive synthetic Win32 smoke only. This executable is never shipped
// with VEIL. No service, product profile or product candidate is accepted.
if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
    !Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0 ||
    new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Use a non-elevated interactive Windows x64 session.");
if (args is not [var imageArg, var tempArg])
    throw new ArgumentException("Expected built smoke executable and new temporary directory.");
string image = Path.GetFullPath(imageArg), temp = Path.GetFullPath(tempArg);
if (Path.GetFileName(image) != "veil-cef-smoke.exe" || !File.Exists(image) ||
    !File.Exists(Path.Combine(Path.GetDirectoryName(image)!, "veil-cef-smoke.dll")) ||
    Directory.Exists(temp) || File.Exists(temp) ||
    image.StartsWith(@"\\", StringComparison.Ordinal) || temp.StartsWith(@"\\", StringComparison.Ordinal))
    throw new ArgumentException("Refusing non-fixture executable or reused temporary directory.");
Directory.CreateDirectory(temp);
var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
{
    ["TEMP"] = temp, ["TMP"] = temp,
    ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    ["PATH"] = Environment.SystemDirectory,
};
using var child = CefContainedProcess.CreateSuspended(image, environment);
using var root = new SafeProcessHandle(new IntPtr(child.NominatedHandle), ownsHandle: false);
using var second = new FixtureJob();
uint rootPid = FixtureJob.ProcessId(root);
second.AssignRoot(root);
var initial = second.Members();
if (initial.Length != 1 || initial[0] != rootPid)
    throw new InvalidOperationException("Unexpected initial job membership.");
var observed = new Dictionary<uint, FixtureJob.TokenSample>();
var timer = Stopwatch.StartNew();
try
{
    child.ResumeAfterVerification(() => second.Contains(root));
    while (true)
    {
        foreach (uint pid in second.Members())
        {
            if (pid == rootPid) continue;
            if (second.TrySample(pid, image, out var sample))
                observed[pid] = sample;
        }
        if (child.WaitForExit(0)) break;
        if (timer.Elapsed >= TimeSpan.FromSeconds(30))
            throw new TimeoutException("Nested CEF smoke did not exit.");
        child.WaitForExit(100);
    }
    uint exit = child.ReadExitCode();
    bool outerEmpty = child.WaitForNaturalJobExit(5000);
    bool innerEmpty = second.Members().Length == 0;
    bool restricted = observed.Values.Any(s => s.Restricted && s.IntegrityRid <= 0x1000);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scope = "nested-win32-smoke-only",
        rootPid, exit, outerEmpty, innerEmpty, restricted,
        observed = observed.Values.OrderBy(s => s.Pid),
        limitations = "Sampled token evidence only; no subprocess role, full site isolation, product authorization or OS write trace."
    }));
    if (exit != 0 || !outerEmpty || !innerEmpty || !restricted)
        throw new InvalidOperationException("Nested CEF fixture did not establish required evidence.");
    return 0;
}
finally
{
    // Both jobs were created exclusively by this test. No global process kill.
    try { child.TerminateAndDrain(5000); }
    finally { second.Dispose(); }
}

// Independent test-only job with no active process cap or breakaway. The
// production broker's single-process job remains unchanged.
sealed class FixtureJob : IDisposable
{
    private readonly SafeFileHandle job;
    internal FixtureJob()
    {
        job = CreateJobObjectW(IntPtr.Zero, null);
        try
        {
            if (job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits();
            limits.Basic.Flags = 0x2000;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception();
        }
        catch { job.Dispose(); throw; }
    }

    internal void AssignRoot(SafeProcessHandle root)
    {
        if (Members().Length != 0 || !AssignProcessToJobObject(job, root) || !Contains(root))
            throw new InvalidOperationException("Nested fixture job assignment failed.");
    }

    internal bool Contains(SafeProcessHandle process) =>
        IsProcessInJob(process, job, out bool contained) && contained;

    internal static uint ProcessId(SafeProcessHandle process)
    {
        uint pid = GetProcessId(process);
        return pid != 0 ? pid : throw new Win32Exception();
    }

    internal uint[] Members()
    {
        const int capacity = 128;
        const int bytes = 8 + capacity * 8;
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!QueryInformationJobObject(job, 3, buffer, bytes, out _))
                throw new Win32Exception();
            int assigned = Marshal.ReadInt32(buffer, 0);
            int count = Marshal.ReadInt32(buffer, 4);
            if (count < 0 || count > capacity || assigned != count)
                throw new InvalidOperationException("Incomplete fixture job membership.");
            var result = new uint[count];
            for (int i = 0; i < count; ++i)
            {
                long pid = Marshal.ReadInt64(buffer, 8 + 8 * i);
                if (pid <= 0 || pid > uint.MaxValue) throw new InvalidOperationException("Invalid job PID.");
                result[i] = (uint)pid;
            }
            if (result.Distinct().Count() != count) throw new InvalidOperationException("Duplicate job PID.");
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal sealed record TokenSample(uint Pid, uint IntegrityRid, bool Restricted);

    internal bool TrySample(uint pid, string image, out TokenSample sample)
    {
        sample = null!;
        using var process = OpenProcess(0x1000 | 0x00100000, false, pid);
        if (process.IsInvalid || !Contains(process) || WaitForSingleObject(process, 0) != 0x102)
            return false;
        var name = new StringBuilder(32768);
        uint length = (uint)name.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, name, ref length))
            return false;
        if (!string.Equals(name.ToString(), image, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected executable in isolated CEF fixture.");
        if (!OpenProcessToken(process, 8, out var token)) return false;
        using (token)
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out uint needed);
            if (needed == 0 || needed > 65536) return false;
            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, needed, out _)) return false;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero || !IsValidSid(sid)) return false;
                byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                if (count == 0) return false;
                uint rid = unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))));
                bool restricted = IsTokenRestricted(token);
                if (!Contains(process) || WaitForSingleObject(process, 0) != 0x102) return false;
                sample = new TokenSample(pid, rid, restricted);
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public void Dispose() => job.Dispose();

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
    private struct IoCounters { public ulong A, B, C, D, E, F; }
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
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, IntPtr buffer, int length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint ms);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr data, uint bytes, out uint needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
