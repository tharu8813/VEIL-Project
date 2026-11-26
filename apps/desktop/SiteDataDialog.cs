using System.Windows;
using System.Windows.Controls;

namespace Veil.Desktop;

/// <summary>Shows metadata only, never cookie names/values or site storage contents.</summary>
internal sealed class SiteDataDialog : VeilDialog
{
    internal SiteDataSelection? Result { get; private set; }

    internal SiteDataDialog(Window owner, IReadOnlyList<CookieDomainSummary> domains,
        IReadOnlyList<string> origins, string? currentOrigin, SiteDataCapabilities support)
        : base(owner, "쿠키 및 사이트 데이터", "", 760, 650)
    {
        MinWidth = 600;
        Subtitle.Text = "로그인과 사이트 데이터는 직접 삭제할 때까지 기존 정책대로 유지합니다.";
        var body = new StackPanel();
        TextBlock Heading(string value) => new() { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6), TextWrapping = TextWrapping.Wrap };
        TextBlock Note(string value) => new() { Text = value, Foreground = Res("MutedBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        Button Action(string label, Action action)
        {
            var button = new Button { Content = label, Style = (Style)FindResource("GhostButton"),
                Height = 32, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 4, 0, 4) };
            button.Click += (_, _) => action();
            return button;
        }
        void Choose(SiteDataSelection value, string message)
        {
            if (!support.Supports(value)) return;
            if (!value.InspectUsage && !UserPrompts.Confirm(PromptOwner, message, "사이트 데이터 삭제")) return;
            Result = value;
            Complete(true);
        }
        var search = new TextBox { Style = (Style)FindResource("PillTextBox"), Tag = "", Height = 34,
            ToolTip = "도메인 또는 출처 검색" };
        System.Windows.Automation.AutomationProperties.SetName(search, "도메인 또는 출처 검색");
        body.Children.Add(search);
        body.Children.Add(Heading("쿠키 저장 도메인"));
        body.Children.Add(Note("쿠키의 개수만 표시합니다. 앞에 점이 붙은 공유 도메인은 하위 사이트 로그인에도 영향을 줄 수 있습니다."));
        var cookies = new ListBox { Style = (Style)FindResource("VeilList"), MaxHeight = 155, MinHeight = 70 };
        System.Windows.Automation.AutomationProperties.SetName(cookies, "쿠키 저장 도메인 목록");
        body.Children.Add(cookies);
        var deleteCookies = Action("선택 도메인 쿠키 삭제", () =>
        {
            if ((cookies.SelectedItem as ListBoxItem)?.Tag is not CookieDomainSummary selected) return;
            Choose(new(null, selected.Domain, false, false, false),
                $"{selected.Domain}에 저장된 쿠키를 삭제할까요? 공유 도메인은 여러 사이트에서 로그아웃될 수 있습니다. 다른 저장 도메인은 선택하지 않습니다.");
        });
        deleteCookies.IsEnabled = false;
        cookies.SelectionChanged += (_, _) => deleteCookies.IsEnabled =
            support.ReadCookieDomains && support.DeleteCookieDomain && cookies.SelectedItem is ListBoxItem;
        if (!support.ReadCookieDomains || !support.DeleteCookieDomain)
            body.Children.Add(Note("현재 엔진은 선택 도메인 쿠키 삭제를 지원하지 않습니다."));
        body.Children.Add(deleteCookies);
        body.Children.Add(Heading("사이트 저장소"));
        body.Children.Add(Note("열린 탭·방문 기록·북마크에서 확인한 출처입니다. 실제 모든 저장소의 목록이 아니며, 스킴과 포트를 구분합니다."));
        var sites = new ListBox { Style = (Style)FindResource("VeilList"), MaxHeight = 155, MinHeight = 70 };
        System.Windows.Automation.AutomationProperties.SetName(sites, "사이트 저장소 출처 목록");
        body.Children.Add(sites);
        var usage = Action("선택 출처 사용량 확인", () =>
        {
            if ((sites.SelectedItem as ListBoxItem)?.Tag is string origin)
                Choose(new(origin, null, false, false, false, InspectUsage: true), "");
        });
        var clearSite = Action("선택 출처 저장소 삭제", () =>
        {
            if ((sites.SelectedItem as ListBoxItem)?.Tag is not string origin) return;
            Choose(new(origin, null, false, false, false),
                $"{origin}의 LocalStorage, IndexedDB, CacheStorage, Service Worker 등 지원 저장소를 삭제할까요? 쿠키는 별도로 관리됩니다. 열린 페이지는 데이터를 다시 만들 수 있습니다.");
        });
        usage.IsEnabled = clearSite.IsEnabled = false;
        sites.SelectionChanged += (_, _) =>
        {
            usage.IsEnabled = support.InspectOrigin && sites.SelectedItem is ListBoxItem;
            clearSite.IsEnabled = support.ClearOrigin && sites.SelectedItem is ListBoxItem;
        };
        if (!support.InspectOrigin || !support.ClearOrigin)
            body.Children.Add(Note("현재 엔진에서 지원하지 않는 출처 작업은 비활성화됩니다."));
        var actions = new WrapPanel { Children = { usage, clearSite } };
        usage.Margin = new Thickness(0, 4, 8, 4);
        body.Children.Add(actions);

        void Refresh()
        {
            string filter = search.Text.Trim();
            cookies.Items.Clear();
            sites.Items.Clear();
            foreach (var domain in domains.Where(item => item.Domain.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                cookies.Items.Add(new ListBoxItem { Tag = domain, Content = Row("", domain.Domain, $"{domain.Count}개 쿠키") });
            foreach (var origin in origins.Where(item => item.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new ListBoxItem { Tag = origin, Content = origin };
                sites.Items.Add(row);
                if (origin == currentOrigin) sites.SelectedItem = row;
            }
            if (sites.SelectedItem is null && sites.Items.Count > 0) sites.SelectedIndex = 0;
            Subtitle.Text = $"쿠키 도메인 {cookies.Items.Count}개 · 확인된 출처 {sites.Items.Count}개";
        }
        search.TextChanged += (_, _) => Refresh();
        Refresh();

        body.Children.Add(Heading("전체 프로필 데이터 삭제"));
        body.Children.Add(Note("선택한 유형만 삭제합니다. 모든 창에 영향을 주며 로그인이나 오프라인 데이터가 사라질 수 있습니다. 북마크·VEIL 방문 기록·비밀번호 관리자·다운로드 금고는 삭제하지 않습니다."));
        var allCookies = new CheckBox { Content = "모든 쿠키", Foreground = Res("TextBrush") };
        var allStorage = new CheckBox { Content = "모든 사이트 DOM 저장소", Foreground = Res("TextBrush"), Margin = new Thickness(0, 8, 0, 0) };
        var cache = new CheckBox { Content = "디스크 캐시", Foreground = Res("TextBrush"), Margin = new Thickness(0, 8, 0, 0) };
        allCookies.IsEnabled = support.SupportsProfile(EngineProfileDataKinds.Cookies);
        allStorage.IsEnabled = support.SupportsProfile(EngineProfileDataKinds.DomStorage);
        cache.IsEnabled = support.SupportsProfile(EngineProfileDataKinds.DiskCache);
        body.Children.Add(allCookies);
        body.Children.Add(allStorage);
        body.Children.Add(cache);
        if (!allCookies.IsEnabled || !allStorage.IsEnabled || !cache.IsEnabled)
            body.Children.Add(Note("현재 엔진에서 지원하지 않는 삭제 유형은 선택할 수 없습니다."));
        if (!support.AllowMixedProfileKinds)
            body.Children.Add(Note("현재 엔진은 한 번에 하나의 프로필 삭제 유형만 지원합니다."));
        body.Children.Add(Note("삭제하기 전에 관련 사이트의 탭을 닫는 것을 권장합니다. 삭제는 디스크 복구 흔적의 완전 제거를 보장하지 않습니다."));
        SetBody(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        AddFooterButton("닫기", Dismiss);
        var clearAll = AddFooterButton("선택 유형 전체 삭제", () =>
        {
            var value = new SiteDataSelection(null, null, allCookies.IsChecked == true, allStorage.IsChecked == true, cache.IsChecked == true);
            if (!support.Supports(value)) return;
            var names = new List<string>();
            if (value.ClearAllCookies) names.Add("모든 쿠키");
            if (value.ClearAllStorage) names.Add("모든 사이트 DOM 저장소");
            if (value.ClearDiskCache) names.Add("디스크 캐시");
            Choose(value, "현재 프로필 전체에서 다음 데이터를 삭제할까요?\n" + string.Join(", ", names) +
                "\n다른 창에도 적용되며 되돌릴 수 없습니다.");
        }, primary: true);
        clearAll.IsEnabled = false;
        void Sync() => clearAll.IsEnabled = support.Supports(new SiteDataSelection(null, null,
            allCookies.IsChecked == true, allStorage.IsChecked == true, cache.IsChecked == true));
        foreach (var check in new[] { allCookies, allStorage, cache })
        {
            check.Checked += (_, _) => Sync();
            check.Unchecked += (_, _) => Sync();
        }
        OnContentLoaded(() => search.Focus());
    }
}
