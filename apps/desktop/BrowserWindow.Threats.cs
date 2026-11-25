using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

/// <summary>ADR-0008 VEIL threat engine on navigation: Malicious opens a warning page; only the native bar
/// can continue (web content cannot). Unknown and Error continue (fail-open); Error is shown in the status bar.</summary>
public partial class BrowserWindow
{
    internal const string ThreatWarningUrl = "veil://threat-warning";

    /// <summary>Last fail-open notice (a navigation that proceeded without a usable list).</summary>
    internal string? LastThreatNotice { get; private set; }

    /// <summary>Returns true when the navigation was cancelled and replaced by the warning page.</summary>
    private bool HandleThreat(BrowserTab tab, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsWebUrl(args.Uri) || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || session.ThreatAllowed.Contains(uri.IdnHost))
            return false;
        var check = session.Security.Threats.CheckAsync(uri, TimeSpan.FromMilliseconds(200));
        // Local engines answer synchronously. A slow engine cannot hold the navigation: continue (fail-open).
        if (!check.IsCompletedSuccessfully) return false;
        var result = check.Result;
        if (result.Verdict == ThreatVerdict.Error)
        {
            // The status-bar indicator keeps "위협 목록 오류" visible; this line explains the individual navigation.
            LastThreatNotice = $"VEIL 위협 검사를 할 수 없어 그대로 엽니다 ({result.Reason}).";
            UpdateSecurityIndicators();
            if (Active == tab) Status.Text = LastThreatNotice;
            return false;
        }
        if (result.Verdict != ThreatVerdict.Malicious) return false;
        args.Cancel = true;
        Dispatcher.BeginInvoke(() => ShowThreatWarning(tab, uri, result.Reason));
        return true;
    }

    private void ShowThreatWarning(BrowserTab tab, Uri blocked, string reason)
    {
        tab.PendingThreat = blocked;
        tab.Url = ThreatWarningUrl;
        tab.Title = InternalTitle(ThreatWarningUrl);
        tab.Header.Label.Text = tab.Title;
        UpdateTabToolTip(tab);
        SetTabLoading(tab, false);
        tab.View.CoreWebView2.NavigateToString($$"""
<!doctype html><html><head><meta charset="utf-8"><style>
body{margin:0;background:#0A0C13;color:#E9ECF5;font:14px Segoe UI,Malgun Gothic,sans-serif}
main{max-width:720px;margin:64px auto;padding:0 32px}h1{font-size:24px;margin:0 0 12px}
p{color:#B8BDD0;line-height:1.6}.host{color:#F2B84B;font-weight:600}
</style></head><body><main>
<h1>위험한 사이트로 알려진 주소입니다</h1>
<p><span class="host">{{EscapeHtml(blocked.Host)}}</span> — {{EscapeHtml(reason)}}.</p>
<p>VEIL의 로컬 위협 목록에 있는 주소라 열지 않았습니다. 이 검사는 이 컴퓨터 안에서만 이루어지며 주소를 어디에도 보내지 않습니다.</p>
<p>그래도 열려면 VEIL 창 위쪽 막대에서 직접 선택하세요. 페이지 안에서는 이 선택을 할 수 없습니다. 허용은 이번 실행 동안 이 사이트에만 적용됩니다.</p>
</main></body></html>
""");
        if (Active == tab) RefreshChrome();
        UpdateThreatBar();
    }

    /// <summary>Shows the native continue bar only while the active tab is the threat warning page.</summary>
    private void UpdateThreatBar()
    {
        bool show = Active is { Url: ThreatWarningUrl, PendingThreat: not null };
        ThreatBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) ThreatBarText.Text = $"{Active!.PendingThreat!.Host}은(는) VEIL 위협 목록에 있습니다. 계속하면 피싱이나 악성 코드에 노출될 수 있습니다.";
    }

    private void ProceedThreatClick(object sender, RoutedEventArgs args)
    {
        if (Active is not { Url: ThreatWarningUrl, PendingThreat: { } blocked } tab) return;
        session.ThreatAllowed.Add(blocked.IdnHost);
        tab.Url = blocked.AbsoluteUri;
        tab.PendingThreat = null;
        tab.View.CoreWebView2.Navigate(blocked.AbsoluteUri);
        UpdateThreatBar();
    }
}
