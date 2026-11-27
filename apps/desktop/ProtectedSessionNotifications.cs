using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Veil.Desktop;

/// <summary>Requests protected browser shutdown on Windows session lock or a sleep transition.</summary>
internal sealed class ProtectedSessionNotifications : IDisposable
{
    private const int SessionChange = 0x02B1;
    private const int SessionLock = 7;
    private const int PowerBroadcast = 0x0218;
    private const int Suspend = 4;
    private const int ResumeAutomatic = 0x12;
    private const int NotifyForThisSession = 0;
    private const int HiddenPopup = unchecked((int)0x80000000); // WS_POPUP without WS_VISIBLE.
    private readonly HwndSource source;
    private readonly Action close;

    private int lockRequested;
    internal bool LockRequested => System.Threading.Volatile.Read(ref lockRequested) != 0;

#if VEIL_CEF_PREFLIGHT_ONLY
    // A stopped independent notification pump must fail closed.
    internal void MarkUnavailable() => System.Threading.Interlocked.Exchange(ref lockRequested, 1);
#endif

    internal ProtectedSessionNotifications(Action close)
    {
        this.close = close;
        // A hidden top-level HWND receives broadcasts even while the password dialog is open.
        source = new HwndSource(new HwndSourceParameters("VEIL protected session") { WindowStyle = HiddenPopup });
        source.AddHook(OnMessage);
        if (!WTSRegisterSessionNotification(source.Handle, NotifyForThisSession))
        {
            var failure = new Win32Exception();
            source.Dispose();
            throw new ProfileLocationException($"Session lock notifications could not be registered ({failure.NativeErrorCode}).");
        }
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        int reason = unchecked((int)wParam.ToInt64());
        bool powerTransition = message == PowerBroadcast && reason is Suspend or ResumeAutomatic;
        if (powerTransition || (message == SessionChange && reason == SessionLock))
        {
            if (System.Threading.Interlocked.Exchange(ref lockRequested, 1) == 0)
            {
                // Let Windows continue the transition. Volume I/O can outlast the suspend notification.
                source.Dispatcher.BeginInvoke(DispatcherPriority.Send, close);
            }
            if (powerTransition)
            {
                handled = true;
                return new IntPtr(1);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        WTSUnRegisterSessionNotification(source.Handle);
        source.Dispose();
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
