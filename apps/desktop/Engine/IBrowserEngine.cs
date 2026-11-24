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
