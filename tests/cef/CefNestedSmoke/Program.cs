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
    // Expand the Windows cache path into fixture-owned scratch storage.
    ["SystemDrive"] = temp,
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
FixtureJob.TokenSample? browser = null;
var timer = Stopwatch.StartNew();
try
{
    child.ResumeAfterVerification(() => second.Contains(root));
    while (true)
    {
        foreach (uint pid in second.Members())
        {
            if (!second.TrySample(pid, image, child, out var sample)) continue;
            if (pid == rootPid)
                browser = sample with { Role = "browser" };
            else
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
    bool rendererRestrictedLow = observed.Values.Any(s =>
        s.Role == "renderer" && s.Restricted && s.IntegrityRid <= 0x1000);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scope = "nested-win32-smoke-only",
        schema = 3,
        rootPid, exit, outerEmpty, innerEmpty, restricted, rendererRestrictedLow,
        browser,
        observed = observed.Values.OrderBy(s => s.Pid),
        limitations = "Sampled command-line role, token, job membership and loader inventory; no full site isolation, product authorization or OS write trace."
    }));
    if (exit != 0 || !outerEmpty || !innerEmpty || !rendererRestrictedLow || browser is null)
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

    internal sealed record ModuleSample(string Name, string Location);
    internal sealed record TokenSample(uint Pid, uint IntegrityRid, bool Restricted, string Role, bool AppContainer,
        ModuleSample[]? Modules);

    internal bool TrySample(uint pid, string image, CefContainedProcess outerJob, out TokenSample sample)
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
                if (!GetTokenInformation(token, 29, buffer, needed, out uint appContainerBytes) ||
                    appContainerBytes != sizeof(uint)) return false;
                bool appContainer = Marshal.ReadInt32(buffer) != 0;
                string role = ReadRole(process);
                var modules = ObserveModules(pid, Path.GetDirectoryName(image)!);
                if (!Contains(process) || WaitForSingleObject(process, 0) != 0x102) return false;
                if (!outerJob.ContainsFixtureMember(process))
                    throw new InvalidOperationException("Live fixture member is missing from the outer job.");
                sample = new TokenSample(pid, rid, restricted, role, appContainer, modules);
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static ModuleSample[]? ObserveModules(uint pid, string runtimeDirectory)
    {
        try
        {
            // The caller keeps the original process handle live across this PID-based query.
            using var process = Process.GetProcessById(checked((int)pid));
            var modules = process.Modules;
            if (modules.Count is 0 or > 512) return null;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
            string runtime = runtimeDirectory + Path.DirectorySeparatorChar;
            return modules.Cast<ProcessModule>().Select(module =>
            {
                string path = module.FileName;
                string location = path.StartsWith(runtime, StringComparison.OrdinalIgnoreCase) ? "runtime" :
                    path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ? "windows" : "other";
                return new ModuleSample(Path.GetFileName(path), location);
            }).ToArray();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string ReadRole(SafeProcessHandle process)
    {
        // ProcessCommandLineInformation is used only for smoke diagnostics.
        // Read it from the same process object as the token and log only a role.
        const int commandLineInformation = 60;
        const int headerSize = 16; // x64 UNICODE_STRING
        NtQueryInformationProcess(process, commandLineInformation, IntPtr.Zero, 0, out uint needed);
        if (needed < headerSize || needed > 131072) return "unknown";
        IntPtr data = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (NtQueryInformationProcess(process, commandLineInformation, data, needed, out uint returned) != 0 ||
                returned < headerSize || returned > needed) return "unknown";
            int bytes = unchecked((ushort)Marshal.ReadInt16(data));
            IntPtr value = Marshal.ReadIntPtr(data, 8);
            long offset = value.ToInt64() - data.ToInt64();
            if (bytes == 0 || bytes % 2 != 0 || offset < headerSize || offset > returned - bytes)
                return "unknown";
            string? commandLine = Marshal.PtrToStringUni(value, bytes / 2);
            if (string.IsNullOrEmpty(commandLine) || commandLine.Contains('\0')) return "unknown";
            IntPtr args = CommandLineToArgvW(commandLine, out int count);
            if (args == IntPtr.Zero) return "unknown";
            try
            {
                string? type = null;
                string? utility = null;
                for (int i = 1; i < count; i++)
                {
                    string? arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, i * IntPtr.Size));
                    if (arg == "--") break;
                    if (arg?.StartsWith("--type=", StringComparison.Ordinal) == true)
                    {
                        if (type is not null) return "unknown";
                        type = arg[7..];
                    }
                    if (arg?.StartsWith("--utility-sub-type=", StringComparison.Ordinal) == true)
                        utility = arg[19..];
                }
                return type switch
                {
                    "renderer" => "renderer",
                    "gpu-process" => "gpu",
                    "utility" when utility == "network.mojom.NetworkService" => "network",
                    "utility" => "utility",
                    _ => "unknown",
                };
            }
            finally { LocalFree(args); }
        }
        finally { Marshal.FreeHGlobal(data); }
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
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr data, uint bytes, out uint returned);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
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
