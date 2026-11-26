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
    void Stop();
}

// The adapter applies Cancel synchronously before the engine commits navigation.
internal sealed class EngineNavigationStartingEventArgs(string uri, ulong navigationId, bool isRedirected)
{
    internal string Uri { get; } = uri;
    internal ulong NavigationId { get; } = navigationId;
    internal bool IsRedirected { get; } = isRedirected;
    internal bool Cancel { get; set; }
}

internal sealed class EngineNavigationCompletedEventArgs(
    ulong navigationId, bool isSuccess, bool isCanceled, int nativeErrorCode)
{
    internal ulong NavigationId { get; } = navigationId;
    internal bool IsSuccess { get; } = isSuccess;
    internal bool IsCanceled { get; } = isCanceled;
    internal int NativeErrorCode { get; } = nativeErrorCode;
}
