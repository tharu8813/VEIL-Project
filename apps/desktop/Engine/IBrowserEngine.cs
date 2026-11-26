using System.IO;
using System.Windows;

namespace Veil.Desktop;

/// <summary>
/// First migration seam, not a complete browser engine contract. All calls are on the WPF dispatcher.
/// Implementations must not navigate, fetch, or publish data before the host installs mandatory policy.
/// No security guarantee is inferred from implementing this interface.
/// </summary>
internal interface IBrowserEngine
{
    string DisplayName { get; }
    string? RuntimeVersion { get; }
    bool IsReady { get; }
    Task InitializeAsync(string protectedProfileRoot, NetworkSettings network);
    IBrowserEngineView CreateView(string? privateProfileName = null);
}

/// <summary>Common presentation and navigation operations; privileged native features are intentionally absent.</summary>
internal interface IBrowserEngineView : IDisposable
{
    event EventHandler<EngineNavigationStartingEventArgs>? NavigationStarting;
    event EventHandler<EngineNavigationCompletedEventArgs>? NavigationCompleted;
    // Read-only page metadata and history availability, not authorization signals.
    event EventHandler? SourceChanged;
    event EventHandler? TitleChanged;
    event EventHandler? HistoryChanged;
    string Source { get; }
    string DocumentTitle { get; }
    // The adapter supplies an already available image, never a host-side URL fetch.
    event EventHandler? FaviconChanged;
    Task<Stream?> GetFaviconPngAsync();
    event EventHandler? AudioStateChanged;
    // Unexpected process failure only. A normal view close is not an engine failure.
    event EventHandler<EngineProcessFailedEventArgs>? ProcessFailed;
    // Single asynchronous host handler. An unsupported or unhandled popup is denied.
    event Func<EngineNewWindowRequest, Task>? NewWindowRequested;
    event EventHandler? WindowCloseRequested;
    // Read-only page fullscreen state; null means unavailable, not fullscreen permission.
    event EventHandler? FullScreenChanged;
    bool? IsPageFullScreen { get; }
    // Request exit only; false means unsupported. Actual state remains event-driven.
    Task<bool> TryExitPageFullScreenAsync();
    // Null means unavailable or unverified; it is not equivalent to silence/unmuted.
    bool? IsDocumentPlayingAudio { get; }
    bool? IsMuted { get; }
    void SetMuted(bool muted);
    FrameworkElement Element { get; }
    Task InitializeAsync();
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    double ZoomFactor { get; set; }
    void Navigate(string address);
    void ShowInternalHtml(string trustedHostHtml);
    void GoBack();
    void GoForward();
    void Reload();
    Task ReloadIgnoringCacheAsync();
    // Null means the engine cannot report a search result yet.
    Task<bool?> FindTextAsync(string query, bool backwards);
    void Stop();
}

// Optional read-only capability. No native cookie objects, values, arbitrary
// CDP commands, or scripts cross this boundary. An absent capability is unsupported.
internal interface IEngineSiteDataReader
{
    Task<IReadOnlyList<string>> GetCookieDomainsAsync();
    // Null means no valid usage observation, not zero bytes.
    Task<double?> GetOriginStorageUsageAsync(string origin);
}

[Flags]
internal enum EngineProfileDataKinds
{
    None = 0,
    Cookies = 1,
    DomStorage = 2,
    DiskCache = 4,
}

// Optional destructive capability for trusted host code only. Recheck authorization
// after any asynchronous preparation and before issuing a native mutation.
// No arbitrary storage types, commands, scripts, cookie objects or file paths.
internal interface IEngineSiteDataWriter
{
    // Null means authorization expired before deletion; zero is a valid request count.
    Task<int?> DeleteCookieDomainAsync(string domain, Func<bool> stillAuthorized);
    Task<bool> ClearOriginStorageAsync(string origin, Func<bool> stillAuthorized);
    Task<bool> ClearProfileDataAsync(EngineProfileDataKinds kinds, Func<bool> stillAuthorized);
}

// The adapter applies Cancel synchronously before the engine commits navigation.
internal sealed class EngineNavigationStartingEventArgs(string uri, ulong navigationId, bool isRedirected)
{
    internal string Uri { get; } = uri;
    internal ulong NavigationId { get; } = navigationId;
    internal bool IsRedirected { get; } = isRedirected;
    internal bool Cancel { get; set; }
}

internal enum EngineErrorDomain { WebView2, Cef }

// Diagnostic only. Native codes from different engines must never be interpreted interchangeably.
internal sealed record EngineNavigationError(EngineErrorDomain Domain, int NativeCode, string Name)
{
    public override string ToString() => Name;
}

internal sealed class EngineNavigationCompletedEventArgs(
    ulong navigationId, bool isSuccess, bool isCanceled, EngineNavigationError nativeResult)
{
    internal ulong NavigationId { get; } = navigationId;
    internal bool IsSuccess { get; } = isSuccess;
    internal bool IsCanceled { get; } = isCanceled;
    internal EngineNavigationError NativeResult { get; } = nativeResult;
    internal int NativeErrorCode => NativeResult.NativeCode;
    internal EngineNavigationError? Error => IsSuccess || IsCanceled ? null : NativeResult;
}

internal enum EngineFailureScope { PageOrChildProcess, BrowserSession }

// NativeCode is diagnostic only and must never be interpreted as another engine's error enum.
internal sealed class EngineProcessFailedEventArgs(EngineFailureScope scope, int nativeCode) : EventArgs
{
    internal EngineFailureScope Scope { get; } = scope;
    internal int NativeCode { get; } = nativeCode;
}

internal sealed class EngineNewWindowRequest(string uri, bool isUserInitiated,
    Func<IBrowserEngineView, bool> attach)
{
    private Func<IBrowserEngineView, bool>? pending = attach;
    internal string Uri { get; } = uri;
    internal bool IsUserInitiated { get; } = isUserInitiated;

    // Only one attempt is allowed, and the native adapter invalidates the request when its deferral ends.
    internal bool TryAttach(IBrowserEngineView target)
    {
        var callback = pending;
        pending = null;
        return callback?.Invoke(target) == true;
    }

    internal void Complete() => pending = null;
}
