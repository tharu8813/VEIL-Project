using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private bool SiteDataReady() => SettingsReady() && !session.Windows.Any(window => window.restoring);

    private async void SiteDataClick(object sender, RoutedEventArgs args)
    {
        if (FocusManagementPage("veil://settings/site-data")) return;
        if (!SiteDataReady() || session.SettingsUiOpen || session.PasswordUiOpen || session.PermissionUiOpen) return;
        if (session.ActiveDownloads.Count != 0)
        {
            Status.Text = "다운로드가 끝나면 사이트 데이터를 관리할 수 있습니다.";
            return;
        }
        CloseAddressSuggestions();
        var web = Active?.View.CoreWebView2;
        if (web is null) return;
        session.SettingsUiOpen = true;
        session.PermissionUiOpen = true;
        try
        {
            // Empty URI selects all cookies in this engine profile, without contacting websites.
            var domains = SiteDataPolicy.Summarize((await web.CookieManager.GetCookiesAsync(""))
                .Select(cookie => cookie.Domain));
            if (!SiteDataReady()) return;
            var urls = session.Windows.Where(window => !window.closing)
                .SelectMany(window => window.Tabs.Items.OfType<TabItem>())
                .Select(item => ((BrowserTab)item.Tag).Url)
                .Concat(data.Bookmarks.Select(entry => entry.Url))
                .Concat(data.History.Select(entry => entry.Url));
            var origins = SiteDataPolicy.Origins(urls);
            var dialog = new SiteDataDialog(this, domains, origins, SiteDataPolicy.Origin(Active?.Url));
            if (await ShowManagementPageAsync("veil://settings/site-data", dialog) != true || dialog.Result is not { } selection ||
                !SiteDataReady() || session.ActiveDownloads.Count != 0) return;

            // The manager never accepts arbitrary commands, URLs, scripts, cookie values or file paths.
            // Recheck the selected scope against the native snapshot before acting.
            if (selection.CookieDomain is { } domain)
            {
                if (!SiteDataPolicy.ValidDomain(domain) || !domains.Any(item => SiteDataPolicy.SameCookieDomain(item.Domain, domain))) return;
                var fresh = await web.CookieManager.GetCookiesAsync("");
                if (!SiteDataReady() || session.ActiveDownloads.Count != 0) return;
                int requested = 0;
                foreach (var cookie in fresh)
                {
                    if (!SiteDataPolicy.SameCookieDomain(cookie.Domain, domain)) continue;
                    web.CookieManager.DeleteCookie(cookie);
                    requested++;
                }
                var remaining = (await web.CookieManager.GetCookiesAsync("")).Count(cookie =>
                    SiteDataPolicy.SameCookieDomain(cookie.Domain, domain));
                if (SiteDataReady())
                    Status.Text = remaining == 0 ? $"선택 도메인 쿠키 삭제 확인 ({requested}개 요청)." :
                        "일부 쿠키가 남아 있거나 다시 생성되었습니다. 관련 탭을 닫고 다시 시도하세요.";
                return;
            }
            if (selection.Origin is { } origin)
            {
                if (SiteDataPolicy.Origin(origin) != origin || !origins.Contains(origin, StringComparer.Ordinal)) return;
                if (selection.InspectUsage)
                {
                    string response = await web.CallDevToolsProtocolMethodAsync("Storage.getUsageAndQuota",
                        JsonSerializer.Serialize(new { origin }));
                    if (!SiteDataReady()) return;
                    using var json = JsonDocument.Parse(response);
                    if (json.RootElement.TryGetProperty("usage", out var usage) &&
                        usage.TryGetDouble(out double bytes) && double.IsFinite(bytes) && bytes >= 0)
                        MessageBox.Show(this, $"{origin}\n엔진 보고 저장소 사용량: {bytes:N0}바이트\n쿠키 및 모든 저장 유형의 총량을 의미하지 않습니다.",
                            "사이트 저장소 사용량", MessageBoxButton.OK, MessageBoxImage.Information);
                    else Status.Text = "사이트 저장소 사용량을 확인할 수 없습니다.";
                    return;
                }
                await web.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
                    JsonSerializer.Serialize(new { origin, storageTypes = SiteDataPolicy.OriginStorageTypes }));
                if (SiteDataReady()) Status.Text = "선택 출처의 지원 저장소 삭제 요청을 완료했습니다. 열린 페이지에서 다시 생성될 수 있습니다.";
                return;
            }
            // Explicit profile-wide types only. Never use AllProfile or Settings.
            CoreWebView2BrowsingDataKinds kinds = 0;
            if (selection.ClearAllCookies) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
            if (selection.ClearAllStorage) kinds |= CoreWebView2BrowsingDataKinds.AllDomStorage;
            if (selection.ClearDiskCache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
            if (kinds == 0) return;
            await web.Profile.ClearBrowsingDataAsync(kinds);
            if (SiteDataReady()) Status.Text = "선택한 프로필 데이터의 삭제 요청을 완료했습니다. 열린 페이지에서 다시 생성될 수 있습니다.";
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or
            System.IO.IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            if (SiteDataReady())
                Status.Text = "사이트 데이터 작업을 완료하지 못했습니다. 일부 변경이 이미 적용되었을 수 있습니다. 범위를 넓혀 다시 삭제하지 않습니다.";
        }
        finally
        {
            session.SettingsUiOpen = false;
            session.PermissionUiOpen = false;
        }
    }
}
