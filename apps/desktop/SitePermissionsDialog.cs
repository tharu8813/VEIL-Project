using System.Windows;
using System.Windows.Controls;

namespace Veil.Desktop;

/// <summary>Only the browser chrome can create or change site rules. Nothing is sent to the page.</summary>
internal sealed class SitePermissionsDialog : VeilDialog
{
    internal List<SitePermissionRule> Result { get; }

    internal SitePermissionsDialog(Window owner, string? currentOrigin, List<SitePermissionRule>? saved)
        : base(owner, "사이트 권한", "", 680, 540)
    {
        Result = saved?.ToList() ?? [];
        Subtitle.Text = "HTTPS 사이트별로 권한을 관리합니다. 기본값은 차단입니다.";
        var sites = new ListBox { Style = (Style)FindResource("VeilList"), MinHeight = 80, MaxHeight = 170 };
        System.Windows.Automation.AutomationProperties.SetName(sites, "사이트 목록");
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "현재 사이트 또는 이전에 설정한 사이트를 선택하세요.",
            Foreground = Res("MutedBrush"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(sites);
        var options = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(options);
        var notice = new TextBlock
        {
            Text = "허용 및 요청 시 확인은 선택한 HTTPS 출처의 활성 탭에서 사용자 동작으로 발생한 요청에만 적용됩니다. " +
                   "변경은 다음 권한 요청부터 적용됩니다. 이미 사용 중인 카메라·마이크·위치 등을 중지하려면 해당 탭을 닫으세요. " +
                   "파일 접근 등 나머지 권한은 계속 차단합니다.",
            Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        };
        body.Children.Add(notice);
        SetBody(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });

        var origins = Result.Select(rule => rule.Origin).Distinct(StringComparer.Ordinal).OrderBy(origin => origin, StringComparer.Ordinal).ToList();
        if (currentOrigin is not null)
        {
            origins.Remove(currentOrigin);
            origins.Insert(0, currentOrigin);
        }
        foreach (var origin in origins)
            sites.Items.Add(new ListBoxItem { Content = origin, Tag = origin, ToolTip = origin });

        void Render()
        {
            options.Children.Clear();
            if ((sites.SelectedItem as ListBoxItem)?.Tag is not string origin)
            {
                options.Children.Add(new TextBlock
                {
                    Text = "관리할 사이트가 없습니다. HTTPS 페이지를 열고 다시 시도하세요.",
                    Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap,
                });
                return;
            }
            foreach (var kind in SitePermissionPolicy.Kinds)
            {
                var choice = new ComboBox { Width = 170, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
                choice.Items.Add("차단 (기본)");
                choice.Items.Add("요청 시 확인");
                choice.Items.Add("허용");
                choice.SelectedIndex = SitePermissionPolicy.ModeFor(Result, origin, kind.Key) switch
                {
                    SitePermissionPolicy.Ask => 1,
                    SitePermissionPolicy.Allow => 2,
                    _ => 0,
                };
                System.Windows.Automation.AutomationProperties.SetName(choice, $"{kind.Label} 권한");
                choice.SelectionChanged += (_, _) =>
                {
                    Result.RemoveAll(rule => rule.Origin == origin && rule.Kind == kind.Key);
                    if (choice.SelectedIndex is 1 or 2)
                        Result.Add(new SitePermissionRule(origin, kind.Key,
                            choice.SelectedIndex == 1 ? SitePermissionPolicy.Ask : SitePermissionPolicy.Allow));
                };
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                DockPanel.SetDock(choice, Dock.Right);
                row.Children.Add(choice);
                row.Children.Add(new TextBlock { Text = kind.Label, VerticalAlignment = VerticalAlignment.Center });
                options.Children.Add(row);
            }
        }

        sites.SelectionChanged += (_, _) => Render();
        if (sites.Items.Count > 0) sites.SelectedIndex = 0;
        else Render();
        AddFooterButton("선택 사이트 초기화", () =>
        {
            if ((sites.SelectedItem as ListBoxItem)?.Tag is not string origin) return;
            Result.RemoveAll(rule => rule.Origin == origin);
            Render();
        });
        AddFooterButton("취소", () => DialogResult = false);
        AddFooterButton("저장", () =>
        {
            bool newAllow = Result.Any(rule => rule.Mode == SitePermissionPolicy.Allow &&
                SitePermissionPolicy.ModeFor(saved, rule.Origin, rule.Kind) != SitePermissionPolicy.Allow);
            if (newAllow && MessageBox.Show(this,
                "선택한 사이트에 반복적으로 권한을 허용합니다. 신뢰하는 사이트인지 확인하세요.",
                "사이트 권한 허용", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            DialogResult = true;
        }, primary: true);
    }
}
