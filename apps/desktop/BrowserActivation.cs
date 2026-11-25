using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Threading;

namespace Veil.Desktop;

/// <summary>External activation is only web navigation, never privileged browser commands.</summary>
internal static class ExternalAddress
{
    internal const int Limit = 8192;
    internal static string? Parse(string? text)
    {
        if (text is null || text.Length == 0 || text.Length > Limit ||
            text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '"' or '\\') ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) || !BrowserWindow.IsWebUrl(uri.AbsoluteUri) ||
            !uri.IsWellFormedOriginalString() || uri.UserInfo.Length != 0 || uri.AbsoluteUri.Length > Limit)
            return null;
        return uri.AbsoluteUri;
    }

    internal static bool Installed => string.Equals(Environment.ProcessPath,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VEIL", "Desktop", "Veil.Desktop.exe"),
        StringComparison.OrdinalIgnoreCase) && File.Exists(BrokerClientConfig.ProductPath);
}

/// <summary>One protected launch per user across logon sessions; forwarding stays session-local. No secrets, URL files, or elevated endpoint.</summary>
internal sealed class BrowserActivation : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string name;
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    private NamedPipeServerStream? listening;
    private bool disposed;
    internal bool Primary { get; }

    internal BrowserActivation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("No Windows user SID.");
        using var process = Process.GetCurrentProcess();
        name = "VEIL.Activation." + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(sid + ":" + process.SessionId)));
        // A protected volume is shared across logon sessions. Keep its launch lease user-wide.
        // The pipe name above still includes the session ID, so links never cross sessions.
        string userKey = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(sid)));
        mutex = new Mutex(false, @"Global\VEIL.ProtectedLaunch." + userKey);
        try { Primary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Primary = true; }
    }

    // Only forward to an already unlocked instance. No automatic second unlock or profile fallback.
    internal bool Forward(string? address)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            byte[] bytes = Utf8.GetBytes(address ?? "");
            byte[] length = BitConverter.GetBytes(bytes.Length);
            pipe.WriteAsync(length, deadline.Token).AsTask().GetAwaiter().GetResult();
            pipe.WriteAsync(bytes, deadline.Token).AsTask().GetAwaiter().GetResult();
            pipe.FlushAsync(deadline.Token).GetAwaiter().GetResult();
            byte[] reply = new byte[1];
            pipe.ReadExactlyAsync(reply, deadline.Token).AsTask().GetAwaiter().GetResult();
            return reply[0] == 1;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        { return false; }
    }

    internal void Start(Dispatcher dispatcher, Func<string?, Task<bool>> activate)
    {
        if (!Primary || disposed) throw new InvalidOperationException("Activation listener unavailable.");
        _ = ListenAsync(dispatcher, activate, stop.Token);
    }

    private async Task ListenAsync(Dispatcher dispatcher, Func<string?, Task<bool>> activate, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                listening = pipe;
                await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                byte[] length = new byte[4];
                await pipe.ReadExactlyAsync(length, deadline.Token).ConfigureAwait(false);
                int count = BitConverter.ToInt32(length);
                if (count < 0 || count > ExternalAddress.Limit * 4) continue;
                byte[] bytes = new byte[count];
                await pipe.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
                string? url = count == 0 ? null : ExternalAddress.Parse(Utf8.GetString(bytes));
                bool accepted = false;
                if ((count == 0 || url is not null) && !cancel.IsCancellationRequested && !dispatcher.HasShutdownStarted)
                {
                    // Marshal through native chrome only; never let a pipe client select a private window.
                    accepted = await dispatcher.InvokeAsync(() => activate(url), DispatcherPriority.Normal, deadline.Token)
                        .Task.Unwrap().ConfigureAwait(false);
                }
                await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or
                InvalidOperationException or DecoderFallbackException)
            {
                if (cancel.IsCancellationRequested) break;
                // Avoid a busy retry loop if another process or policy blocks the pipe.
                try { await Task.Delay(100, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { listening = null; }
        }
    }

    // Close the activation endpoint without releasing the launch lease. The lease
    // must remain held until the protected session has completed its cleanup attempt.
    internal void StopListening()
    {
        if (disposed) return;
        stop.Cancel();
        listening?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        try { StopListening(); }
        finally
        {
            disposed = true;
            try { if (Primary) mutex.ReleaseMutex(); }
            finally
            {
                mutex.Dispose();
                stop.Dispose();
            }
        }
    }
}
