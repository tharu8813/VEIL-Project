// SPDX-License-Identifier: MPL-2.0
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Veil.Desktop;

internal sealed class CefDevelopmentEngine : IBrowserEngine
{
    private bool viewCreated;

    public string DisplayName => "CEF development host";
    public string? RuntimeVersion => null;
    public bool IsReady { get; private set; }

    internal static string FixtureProfile => Path.GetFullPath(Path.Combine(Path.GetTempPath(),
        $"veil-cef-wpf-profile-{Environment.ProcessId}"));

    public Task InitializeAsync(string protectedProfileRoot, NetworkSettings network)
    {
        string? nativeProfile = Marshal.PtrToStringUni(CefNative.VeilCefProfile());
        if (network != NetworkSettings.Standard ||
            !string.Equals(FixtureProfile, Path.GetFullPath(protectedProfileRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(FixtureProfile, nativeProfile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CEF development requires its synthetic profile and default fixture settings.");
        IsReady = true;
        return Task.CompletedTask;
    }

    public IBrowserEngineView CreateView(string? privateProfileName = null)
    {
        if (!IsReady || viewCreated || privateProfileName is not null)
            throw new InvalidOperationException("The CEF fixture supports one standard development view.");
        viewCreated = true;
        return new CefDevelopmentView();
    }
}

internal sealed class CefDevelopmentView : HwndHost, IBrowserEngineView
{
    private nint hwnd;
    internal ulong NativeHandle { get; private set; }
    private readonly CefNative.BeforeBrowse onStarting;
    private readonly CefNative.NavigationComplete onCompleted;
    private readonly CefNative.PageStateChanged onPageStateChanged;
    private readonly CefNative.RenderProcessFailed onRenderProcessFailed;
    public event EventHandler<EngineProcessFailedEventArgs>? ProcessFailed;
    // Native CEF denies popups until multi-view adoption and web-requested close are supported.
    public event Func<EngineNewWindowRequest, Task>? NewWindowRequested { add { } remove { } }
    public event EventHandler? WindowCloseRequested { add { } remove { } }
    public event EventHandler? FullScreenChanged;
    public bool? IsPageFullScreen => hwnd == 0 || CefNative.VeilCefClosed(NativeHandle) != 0
        ? null : CefNative.VeilCefIsPageFullScreen(NativeHandle) != 0;
    public event EventHandler? SourceChanged;
    public event EventHandler? TitleChanged;
    public event EventHandler? HistoryChanged;
    // The development adapter does not yet expose cached favicon image bytes.
    public event EventHandler? FaviconChanged { add { } remove { } }
    public Task<Stream?> GetFaviconPngAsync() => Task.FromResult<Stream?>(null);
    public event EventHandler? AudioStateChanged;
    public bool? IsDocumentPlayingAudio => null;
    public bool? IsMuted => hwnd == 0 || CefNative.VeilCefClosed(NativeHandle) != 0
        ? null : CefNative.VeilCefIsMuted(NativeHandle) != 0;
    public void SetMuted(bool muted)
    {
        RequireView();
        if (IsMuted == muted) return;
        CefNative.VeilCefSetMuted(NativeHandle, muted ? 1 : 0);
        AudioStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<EngineNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<EngineNavigationCompletedEventArgs>? NavigationCompleted;

    internal CefDevelopmentView()
    {
        onRenderProcessFailed = code =>
        {
            if (hwnd == 0) return;
            try
            {
                ProcessFailed?.Invoke(this, new EngineProcessFailedEventArgs(EngineFailureScope.PageOrChildProcess, code));
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("CEF process failure handler failed: {0}", error.GetType().Name);
            }
        };
        onPageStateChanged = kind =>
        {
            if (hwnd == 0) return;
            try
            {
                switch (kind)
                {
                    case 1: SourceChanged?.Invoke(this, EventArgs.Empty); break;
                    case 2: TitleChanged?.Invoke(this, EventArgs.Empty); break;
                    case 3: HistoryChanged?.Invoke(this, EventArgs.Empty); break;
                    case 4: FullScreenChanged?.Invoke(this, EventArgs.Empty); break;
                }
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("CEF page state handler failed: {0}", error.GetType().Name);
            }
        };
        onStarting = (url, id, redirect) =>
        {
            var callback = NavigationStarting;
            if (callback is null) return 1;
            try
            {
                var request = new EngineNavigationStartingEventArgs(url, id, redirect != 0);
                callback(this, request);
                return request.Cancel ? 1 : 0;
            }
            catch (Exception)
            {
                return 1;
            }
        };
        onCompleted = (id, success, canceled, code) =>
        {
            try
            {
                NavigationCompleted?.Invoke(this,
                    new EngineNavigationCompletedEventArgs(id, success != 0, canceled != 0,
                        new EngineNavigationError(EngineErrorDomain.Cef, code, $"CEF ({code})")));
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("CEF navigation completion handler failed: {0}", error.GetType().Name);
            }
        };
    }

    public FrameworkElement Element => this;
    public string Source => hwnd == 0 ? "" : Marshal.PtrToStringUni(CefNative.VeilCefSource(NativeHandle)) ?? "";
    public string DocumentTitle => hwnd == 0 ? "" : Marshal.PtrToStringUni(CefNative.VeilCefTitle(NativeHandle)) ?? "";
    public bool CanGoBack => hwnd != 0 && CefNative.VeilCefCanGoBack(NativeHandle) != 0;
    public bool CanGoForward => hwnd != 0 && CefNative.VeilCefCanGoForward(NativeHandle) != 0;
    public double ZoomFactor
    {
        get => Math.Pow(1.2, CefNative.VeilCefZoomLevel(NativeHandle));
        set
        {
            if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            RequireView();
            CefNative.VeilCefSetZoomLevel(NativeHandle, Math.Log(value, 1.2));
        }
    }

    public Task InitializeAsync()
    {
        RequireView();
        return Task.CompletedTask;
    }

    public void Navigate(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttp ||
            url.Host is not ("127.0.0.1" or "[::1]" or "localhost") || url.UserInfo.Length != 0)
            throw new ArgumentException("Enter a local HTTP test URL.", nameof(address));
        RequireView();
        CefNative.VeilCefNavigate(NativeHandle, url.AbsoluteUri);
    }

    public void ShowInternalHtml(string trustedHostHtml) => throw new NotSupportedException();
    public void GoBack() => CefNative.VeilCefGoBack(NativeHandle);
    public void GoForward() => CefNative.VeilCefGoForward(NativeHandle);
    public void Reload() => CefNative.VeilCefReload(NativeHandle);
    public Task ReloadIgnoringCacheAsync()
    {
        RequireView();
        CefNative.VeilCefReloadIgnoringCache(NativeHandle);
        return Task.CompletedTask;
    }

    // Native find callbacks and request lifetime have not yet been verified.
    public Task<bool?> FindTextAsync(string query, bool backwards) => Task.FromResult<bool?>(null);
    public void Stop() => CefNative.VeilCefStop(NativeHandle);

    private void RequireView()
    {
        if (hwnd == 0 || CefNative.VeilCefClosed(NativeHandle) != 0)
            throw new InvalidOperationException("The CEF view is unavailable.");
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        const uint childVisible = 0x50000000;
        hwnd = CefNative.CreateWindowExW(0, "STATIC", "", childVisible,
            0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (hwnd == 0) throw new InvalidOperationException("Cannot create the CEF parent window.");
        NativeHandle = CefNative.VeilCefOpen(hwnd, onStarting, onCompleted, onPageStateChanged, onRenderProcessFailed);
        if (NativeHandle == 0)
        {
            CefNative.DestroyWindow(hwnd);
            hwnd = 0;
            throw new InvalidOperationException("Cannot create the CEF browser.");
        }
        return new HandleRef(this, hwnd);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (hwnd != 0) CefNative.VeilCefResize(NativeHandle, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef window)
    {
        CefNative.VeilCefDetachCallbacks(NativeHandle, window.Handle);
        NativeHandle = 0;
        CefNative.DestroyWindow(window.Handle);
        hwnd = 0;
    }
}

internal static class CefNative
{
    private const string Module = "veil-cef-wpf.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal delegate int BeforeBrowse([MarshalAs(UnmanagedType.LPWStr)] string url, ulong id, int redirect);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NavigationComplete(ulong id, int success, int canceled, int code);
    [DllImport(Module)]
    internal static extern ulong VeilCefOpen(nint parent, BeforeBrowse starting, NavigationComplete completed,
        PageStateChanged changed, RenderProcessFailed failed);
    [DllImport(Module)] internal static extern void VeilCefDetachCallbacks(ulong id, nint parent);
    [DllImport(Module)] internal static extern void VeilCefResize(ulong id, nint parent);
    [DllImport(Module, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern void VeilCefNavigate(ulong id, string url);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void PageStateChanged(int kind);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void RenderProcessFailed(int code);
    [DllImport(Module)] internal static extern int VeilCefIsPageFullScreen(ulong id);
    [DllImport(Module)] internal static extern nint VeilCefSource(ulong id);
    [DllImport(Module)] internal static extern nint VeilCefTitle(ulong id);
    [DllImport(Module)] internal static extern nint VeilCefProfile();
    [DllImport(Module)] internal static extern uint VeilCefPopupsBlocked(ulong id);
    [DllImport(Module)] internal static extern uint VeilCefDownloadsBlocked(ulong id);
    [DllImport(Module)] internal static extern uint VeilCefBlobDownloadsBlocked(ulong id);
    [DllImport(Module)] internal static extern uint VeilCefPermissionsDenied(ulong id);
    [DllImport(Module)] internal static extern uint VeilCefMediaPermissionsDenied(ulong id);
    [DllImport(Module)] internal static extern int VeilCefResponseMatches(ulong id);
    [DllImport(Module)] internal static extern int VeilCefAttachmentFilteredBytes(ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int OwnedChunk(nint data, int length);
    [DllImport(Module, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int VeilCefStartOwnedAttachment(ulong id, string url, OwnedChunk chunk);
    [DllImport(Module, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int VeilCefClickFixtureAttachment(ulong id, string url);
    [DllImport(Module)] internal static extern int VeilCefPageAttachmentIntercepted(ulong id);
    [DllImport(Module)] internal static extern int VeilCefOwnedAttachmentResult(ulong id);
    [DllImport(Module)] internal static extern void VeilCefCancelOwnedAttachment(ulong id);
    [DllImport(Module)] internal static extern int VeilCefClosed(ulong id);
    [DllImport(Module)] internal static extern int VeilCefReadyToClose(ulong id);
    [DllImport(Module)] internal static extern void VeilCefPump();
    [DllImport(Module)] internal static extern int VeilCefTryClose(ulong id);
    [DllImport(Module)] internal static extern int VeilCefIsMuted(ulong id);
    [DllImport(Module)] internal static extern void VeilCefSetMuted(ulong id, int muted);
    [DllImport(Module)] internal static extern int VeilCefCanGoBack(ulong id);
    [DllImport(Module)] internal static extern int VeilCefCanGoForward(ulong id);
    [DllImport(Module)] internal static extern void VeilCefGoBack(ulong id);
    [DllImport(Module)] internal static extern void VeilCefGoForward(ulong id);
    [DllImport(Module)] internal static extern void VeilCefReload(ulong id);
    [DllImport(Module)] internal static extern void VeilCefReloadIgnoringCache(ulong id);
    [DllImport(Module)] internal static extern void VeilCefStop(ulong id);
    [DllImport(Module)] internal static extern double VeilCefZoomLevel(ulong id);
    [DllImport(Module)] internal static extern void VeilCefSetZoomLevel(ulong id, double level);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern nint CreateWindowExW(uint exStyle, string className, string name,
        uint style, int x, int y, int width, int height, nint parent,
        nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] internal static extern int DestroyWindow(nint hwnd);
}
