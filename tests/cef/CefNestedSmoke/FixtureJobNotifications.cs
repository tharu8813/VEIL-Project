// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Test-only observation channel. Job membership and drain are checked separately.
internal sealed class FixtureJobNotifications : IDisposable
{
    private const int MaxEvents = 512;
    private readonly SafeFileHandle port;
    private readonly List<JobEvent> events = [];
    internal IReadOnlyList<JobEvent> Events => events;

    internal FixtureJobNotifications(SafeFileHandle outer, SafeFileHandle inner)
    {
        port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, 0, 1);
        try
        {
            if (port.IsInvalid) throw new Win32Exception();
            // The root is assigned to both jobs but remains suspended here.
            Associate(outer, 1);
            Associate(inner, 2);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    private void Associate(SafeFileHandle job, nint key)
    {
        var association = new JobAssociation { Key = key, Port = port.DangerousGetHandle() };
        if (!SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf<JobAssociation>()))
            throw new Win32Exception();
    }

    internal void Drain(uint waitMilliseconds = 0)
    {
        while (GetQueuedCompletionStatus(port, out uint message, out nint key, out nuint value, waitMilliseconds))
        {
            waitMilliseconds = 0;
            var kind = message switch
            {
                4 => "empty",
                6 => "start",
                7 => "exit",
                8 => "abnormal-exit",
                _ => null,
            };
            if (kind is null) continue;
            if (key is not (1 or 2) || value > uint.MaxValue ||
                (kind == "empty" ? value != 0 : value == 0) || events.Count >= MaxEvents)
                throw new InvalidOperationException("Invalid or excessive fixture job notifications.");
            events.Add(new JobEvent(key == 1 ? "outer" : "inner", kind, (uint)value));
        }
        int error = Marshal.GetLastWin32Error();
        if (error != 258) throw new Win32Exception(error);
    }

    public void Dispose() => port.Dispose();

    internal sealed record JobEvent(string Job, string Kind, uint Pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAssociation
    {
        public nint Key;
        public nint Port;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateIoCompletionPort(nint file, nint existing, nuint key, uint threads);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobAssociation info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint message, out nint key,
        out nuint value, uint waitMilliseconds);
}
