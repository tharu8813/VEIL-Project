using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Channels;

namespace Veil.VolumeBroker;

/// <summary>Windows Service Control Manager host for the production volume broker.</summary>
internal sealed class BrokerService
{
    private const string ServiceName = "Veil.VolumeBroker";
    private const uint Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private const uint Stop = 1, Shutdown = 5, Interrogate = 4;
    private const uint SessionChange = 14;
    private const uint AcceptStopShutdownAndSessionChange = 1 | 4 | 0x80;
    private const uint SessionLogoff = 6, SessionLock = 7;
    private const uint ErrorGeneralFailure = 31, ErrorNotImplemented = 120;
    private readonly CancellationTokenSource stop = new();
    private readonly ControlHandler handler;
    // The SCM callback copies only the session ID. A single worker serializes
    // revocation with broker operations; never block SCM on a process drain.
    private readonly Channel<uint> sessionNotifications = Channel.CreateUnbounded<uint>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private IntPtr statusHandle;
    private uint serviceError;

    private BrokerService() => handler = Control;

    internal static int Run()
    {
        var host = new BrokerService();
        using var stop = host.stop;
        ServiceMain main = host.Main;
        ServiceEntry[] entries = [new() { Name = ServiceName, Main = main }, default];
        bool connected = StartServiceCtrlDispatcherW(entries);
        int error = connected ? (int)host.serviceError : Marshal.GetLastWin32Error();
        GC.KeepAlive(main);
        GC.KeepAlive(host);
        return error;
    }

    private void Main(uint argumentCount, IntPtr arguments)
    {
        statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, handler, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero)
        {
            serviceError = (uint)Marshal.GetLastWin32Error();
            return;
        }

        try
        {
            Report(StartPending);
            using var identity = WindowsIdentity.GetCurrent();
            if (!identity.IsSystem) throw new UnauthorizedAccessException("The broker service requires LocalSystem.");
            var config = BrokerConfig.Load(BrokerConfig.DefaultPath, development: false);
            var broker = new Broker(config, new WindowsBitLockerBackend(config), new BrokerLog(null, console: false));
            bool ownsVolume = false;
            var sessionWorker = RevokeSessionsAsync(broker);
            try
            {
                broker.RunAsync(stop.Token, () =>
                {
                    // Claim the pipe first, then clean up a registered mount left by a previous run.
                    broker.SecureVolumeAsync().GetAwaiter().GetResult();
                    ownsVolume = true;
                    Report(Running);
                }).GetAwaiter().GetResult();
            }
            finally
            {
                sessionNotifications.Writer.TryComplete();
                try { sessionWorker.GetAwaiter().GetResult(); }
                finally { if (ownsVolume) broker.SecureVolumeAsync().GetAwaiter().GetResult(); }
            }
        }
        catch (Exception)
        {
            // The SCM records the status code. Do not place exception details in an elevated service log.
            serviceError = ErrorGeneralFailure;
        }
        finally { Report(Stopped); }
    }

    private async Task RevokeSessionsAsync(Broker broker)
    {
        await foreach (uint sessionId in sessionNotifications.Reader.ReadAllAsync())
            await broker.RevokeWindowsSessionAsync(sessionId);
    }

    private uint Control(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control == Interrogate) return 0;
        if (control == SessionChange)
        {
            // WTSSESSION_NOTIFICATION has two DWORDs. The event pointer is
            // valid only during this callback; do not retain it or accept IPC data.
            if (eventType is SessionLogoff or SessionLock && eventData != IntPtr.Zero &&
                Marshal.ReadInt32(eventData) == 8)
            {
                uint sessionId = unchecked((uint)Marshal.ReadInt32(eventData, 4));
                if (sessionId != 0 && sessionId <= int.MaxValue)
                    sessionNotifications.Writer.TryWrite(sessionId);
            }
            return 0;
        }
        if (control is not (Stop or Shutdown)) return ErrorNotImplemented;
        Report(StopPending);
        _ = stop.CancelAsync();
        return 0;
    }

    private void Report(uint state)
    {
        bool pending = state is StartPending or StopPending;
        var status = new ServiceStatus
        {
            Type = 0x10, // SERVICE_WIN32_OWN_PROCESS
            State = state,
            Accepted = state == Running ? AcceptStopShutdownAndSessionChange : 0,
            Win32ExitCode = serviceError,
            CheckPoint = pending ? 1u : 0,
            WaitHint = pending ? 240_000u : 0,
        };
        if (!SetServiceStatus(statusHandle, ref status) && state == Running) throw new Win32Exception();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMain(uint argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ControlHandler(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public ServiceMain? Main;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, Accepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW([In] ServiceEntry[] entries);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, ControlHandler handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
