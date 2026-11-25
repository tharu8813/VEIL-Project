using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

/// <summary>ADR-0006 network guard: HTTPS-first navigation, the HTTPS warning page, and the network
/// mode indicator / settings.</summary>
public partial class BrowserWindow
{
    internal const string HttpsWarningUrl = "veil://https-warning";
    private const string AllowHttpUrl = "veil://allow-http";
#if DEBUG
    private BrowserTab? smokeRedirectTab;
    private Uri? smokeExpectedHttpRedirect;
    private int smokeCancelledHttpRedirects;
#endif

    /// <summary>Returns true when the navigation was handled here (cancelled and replaced).</summary>
    private bool HandleHttpsFirst(BrowserTab tab, CoreWebView2NavigationStartingEventArgs args)
    {
        // Web content can never downgrade a connection: a page-triggered veil://allow-http (including one a
        // site pushes into a popup it opened) is always dropped. The only way is the native HttpsBar button.
        if (args.Uri.StartsWith(AllowHttpUrl, StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            return true;
        }
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || NetworkPolicy.HttpsUpgrade(uri, session.HttpAllowed) is not { } secure)
            return false;
        args.Cancel = true;
#if DEBUG
        if (args.IsRedirected && tab == smokeRedirectTab && uri.Equals(smokeExpectedHttpRedirect))
            smokeCancelledHttpRedirects++;
#endif
        // A site that redirects its HTTPS address back to HTTP cannot be upgraded: warn instead of looping.
        if (args.IsRedirected && tab.UpgradedFrom?.IdnHost == uri.IdnHost)
        {
            tab.UpgradedFrom = null;
            QueueNavigationAction(tab, () => ShowHttpsWarning(tab, uri, "HTTPS 주소가 다시 HTTP로 전환했습니다."));
            return true;
        }
        tab.UpgradedFrom = uri;
        tab.ExpectedUpgrade = secure;
        QueueNavigationAction(tab, () => tab.View.CoreWebView2.Navigate(secure.AbsoluteUri));
        return true;
    }

    private void QueueNavigationAction(BrowserTab tab, Action action)
    {
        long epoch = tab.NavigationEpoch;
        Dispatcher.BeginInvoke(() =>
        {
            if (!CanUseTab(tab.Item) || tab.NavigationEpoch != epoch) return;
            try { action(); }
            catch (Exception error) when (RecoverableFailure.Is(error))
            {
                tab.ExpectedUpgrade = null;
                tab.UpgradedFrom = null;
                SetTabLoading(tab, false);
                if (Active == tab) Status.Text = "보호 탐색을 완료하지 못했습니다. 주소를 다시 입력하세요.";
            }
        });
    }

    private void ShowHttpsWarning(BrowserTab tab, Uri plain, string reason)
    {
        if (!CanUseTab(tab.Item)) return;
        tab.ExpectedUpgrade = null;
        tab.UpgradedFrom = null;
        tab.PendingThreat = null;
        tab.PendingHttp = plain;
        tab.Url = HttpsWarningUrl;
        tab.Title = InternalTitle(HttpsWarningUrl);
        tab.Header.Label.Text = tab.Title;
        UpdateTabToolTip(tab);
        tab.View.CoreWebView2.NavigateToString($$"""
<!doctype html><html><head><meta charset="utf-8"><style>
body{margin:0;background:#0A0C13;color:#E9ECF5;font:14px Segoe UI,Malgun Gothic,sans-serif}
main{max-width:720px;margin:64px auto;padding:0 32px}h1{font-size:24px;margin:0 0 12px}
p{color:#B8BDD0;line-height:1.6}.host{color:#F2B84B;font-weight:600}
</style></head><body><main>
<h1>안전한 연결(HTTPS)을 사용할 수 없습니다</h1>
<p><span class="host">{{EscapeHtml(plain.Host)}}</span>에 HTTPS로 연결하지 못했습니다. {{EscapeHtml(reason)}}</p>
<p>HTTP로 계속하면 이 사이트와 주고받는 내용(입력한 정보 포함)을 네트워크의 다른 사람이 보거나 바꿀 수 있습니다. 허용은 이번 실행 동안 이 사이트에만 적용됩니다.</p>
<p>계속하려면 VEIL 창 위쪽의 빨간 막대에서 직접 선택하세요. 페이지 안에서는 이 선택을 할 수 없습니다.</p>
</main></body></html>
""");
        if (Active == tab) RefreshChrome();
        UpdateHttpsBar();
    }

    /// <summary>Shows the native downgrade bar only while the active tab is the HTTPS warning page.</summary>
    private void UpdateHttpsBar()
    {
        bool show = Active is { Url: HttpsWarningUrl, PendingHttp: not null };
        HttpsBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) HttpsBarText.Text = $"{Active!.PendingHttp!.Host}은(는) 안전한 연결(HTTPS)을 사용할 수 없습니다. HTTP로 계속하면 주고받는 내용이 노출될 수 있습니다.";
    }

    private void AllowHttpClick(object sender, RoutedEventArgs args)
    {
        if (Active is not { Url: HttpsWarningUrl, PendingHttp: { } plain } tab || !CanUseTab(tab.Item)) return;
        tab.NavigationEpoch++;
        tab.NavigationId = null;
        session.HttpAllowed.Add(plain.IdnHost);
        tab.Url = plain.AbsoluteUri;
        tab.PendingHttp = null;
        tab.View.CoreWebView2.Navigate(plain.AbsoluteUri);
        UpdateHttpsBar();
    }

    /// <summary>Selected vs applied vs verified/blocked, as required by the network guard design.</summary>
    internal void UpdateNetworkIndicator()
    {
        var network = session.Network;
        string saved = (data.Network ?? NetworkSettings.Standard) == network ? "" : " · 변경 사항은 다시 시작하면 적용";
        string dns = (network.DnsTemplate is null ? "" : " · 엔진 보안 DNS 설정") + SecuritySuffix();
        NetworkIndicator.Text = network.Mode == NetworkMode.Standard
            ? $"표준 보호 · HTTPS 우선 · 추적 방지 엄격{dns}{saved}"
            : $"{NetworkPolicy.ProxyName} · {NetworkPolicy.ProxyStatusText(session.ProxyReachable, session.ProxyCheckedAt)}{dns}{saved}";
    }

    private async void NetworkClick(object sender, RoutedEventArgs args)
    {
        if (FocusManagementPage("veil://settings/network")) return;
        if (!SettingsReady()) return;
        if (session.SettingsUiOpen || session.PasswordUiOpen || session.PermissionUiOpen) return;
        session.SettingsUiOpen = true;
        session.PermissionUiOpen = true;
        try
        {
            var dialog = new NetworkSettingsDialog(this, data.Network ?? NetworkSettings.Standard);
            if (await ShowManagementPageAsync("veil://settings/network", dialog) != true || dialog.Result is not { } chosen ||
                closing || session.IsEnding || session.EngineLost) return;
            if (chosen.Problem is not null)
            {
                Status.Text = "네트워크 설정이 올바르지 않습니다.";
                return;
            }
            var previous = data.Network;
            data.Network = chosen == NetworkSettings.Standard ? null : chosen;
            try { data.Save(statePath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                data.Network = previous;
                Status.Text = "네트워크 설정을 저장하지 못했습니다. 이전 설정을 유지합니다.";
                return;
            }
            foreach (var window in session.Windows.ToArray())
                if (!window.closing) window.UpdateNetworkIndicator();
            Status.Text = chosen == session.Network
                ? "현재 적용된 네트워크 설정을 유지합니다."
                : "네트워크 설정을 저장했습니다. VEIL을 다시 시작하면 적용됩니다.";
        }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            if (!closing) Status.Text = "네트워크 설정 작업을 완료하지 못했습니다.";
        }
        finally
        {
            session.SettingsUiOpen = false;
            session.PermissionUiOpen = false;
        }
    }
}
