// SPDX-License-Identifier: MPL-2.0
#if VEIL_CEF_PREFLIGHT_ONLY
using System.Threading;
using System.Windows.Threading;

namespace Veil.Desktop;

// Keep the notification HWND pumping while the owning STA performs
// synchronous hashing, IPC and child-exit checks. Do not move the lease,
// profile dialog or broker cleanup onto this thread.
internal sealed class CefPreflightNotificationPump : IDisposable
{
    private readonly Thread thread;
    private readonly Dispatcher dispatcher;
    internal ProtectedSessionNotifications Notifications { get; }

    internal CefPreflightNotificationPump(Dispatcher owner, Action close)
    {
        var ready = new TaskCompletionSource<(ProtectedSessionNotifications Notifications, Dispatcher Dispatcher)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            ProtectedSessionNotifications? notifications = null;
            try
            {
                notifications = new ProtectedSessionNotifications(() =>
                {
                    if (!owner.HasShutdownStarted)
                        owner.BeginInvoke(DispatcherPriority.Send, close);
                });
                ready.TrySetResult((notifications, Dispatcher.CurrentDispatcher));
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
            }
            finally
            {
                // Unexpected pump loss is itself a refusal. The owning
                // thread checks this latch before staging/resume/confirmation.
                notifications?.MarkUnavailable();
                notifications?.Dispose();
            }
        }) { IsBackground = true, Name = "VEIL CEF preflight notifications" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // On timeout the entry fails before unlocking any volume; the
        // background thread cannot keep the validation process alive.
        var started = ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Notifications = started.Notifications;
        dispatcher = started.Dispatcher;
    }

    public void Dispose()
    {
        if (!dispatcher.HasShutdownStarted)
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("CEF preflight notification pump did not stop.");
    }
}
#endif
