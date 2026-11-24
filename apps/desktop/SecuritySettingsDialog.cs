using System.Windows;
using System.Windows.Controls;

namespace Veil.Desktop;

/// <summary>Security settings (ADR-0007): Microsoft SmartScreen on/off. Applied to open tabs without a restart.</summary>
internal sealed class SecuritySettingsDialog : VeilDialog
{
    internal SecuritySettings? Result { get; private set; }

    internal SecuritySettingsDialog(Window owner, SecurityManager security) : base(owner, "보안", "", 540, 520)
    {
        Subtitle.Text = "변경 사항은 열려 있는 모든 탭과 창에 다음 탐색부터 적용됩니다.";
        var toggle = new CheckBox
        {
            Content = "Microsoft SmartScreen 사용 (권장)",
            IsChecked = security.Saved.SmartScreen,
            Foreground = Res("TextBrush"),
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Microsoft SmartScreen 사용");
        var about = new TextBlock
        {
            Text = "SmartScreen은 피싱·악성 사이트와 위험한 다운로드를 경고합니다. 이를 위해 방문하는 주소와 다운로드 정보가 " +
                   "Microsoft로 전송됩니다. VEIL은 이 판정을 자체 결과로 바꾸거나 기록하지 않습니다.",
            TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush"), Margin = new Thickness(0, 10, 0, 0),
        };
        var limits = new TextBlock
        {
            Text = "이 설정은 VEIL 브라우저에만 적용됩니다. Windows Defender와 Windows의 다른 SmartScreen 보호는 바뀌지 않습니다. " +
                   "Windows 설정(앱 및 브라우저 컨트롤)이 SmartScreen을 끄면 여기서 켜도 동작하지 않습니다. " +
                   "SmartScreen을 꺼도 WebView2 엔진의 다른 Microsoft 연결(구성·업데이트 등)이 모두 멈추는 것은 아닙니다.",
            TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush"), Margin = new Thickness(0, 10, 0, 0),
        };
        var risk = new TextBlock
        {
            Text = "주의: SmartScreen을 끄면 알려진 피싱·악성 사이트와 위험한 다운로드에 대한 경고를 받지 못합니다. " +
                   "VEIL 1.0에는 이를 대신하는 자체 탐지 기능이 없습니다.",
            TextWrapping = TextWrapping.Wrap, Foreground = Res("AmberBrush"), Margin = new Thickness(0, 12, 0, 0),
        };
        var state = new TextBlock { Text = $"현재 상태: {security.Describe()}", TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush"), Margin = new Thickness(0, 14, 0, 0) };
        var list = security.ThreatList;
        var veil = new TextBlock
        {
            Text = "VEIL 위협 목록(이 컴퓨터 안에서만 검사, 주소를 보내지 않음): " + (list switch
            {
                { Present: false } => "없음. 목록이 없으면 VEIL 자체 검사는 하지 않습니다.",
                { Valid: true } => $"사용 중 · {list.Detail}" + (list.Expires is { } expires ? $" · 만료 {expires.LocalDateTime:yyyy-MM-dd}" : ""),
                _ => $"오류({list.Detail}). 검사 없이 탐색하며 SmartScreen은 그대로 동작합니다.",
            }),
            TextWrapping = TextWrapping.Wrap, Foreground = list is { Present: true, Valid: false } ? Res("AmberBrush") : Res("MutedBrush"), Margin = new Thickness(0, 10, 0, 0),
        };
        void Sync() => risk.Visibility = toggle.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        toggle.Checked += (_, _) => Sync();
        toggle.Unchecked += (_, _) => Sync();
        Sync();
        SetBody(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { toggle, about, limits, risk, state, veil } } });
        AddFooterButton("취소", () => DialogResult = false);
        AddFooterButton("저장", () =>
        {
            Result = new SecuritySettings(SmartScreen: toggle.IsChecked == true);
            DialogResult = true;
        }, primary: true);
    }
}
