using System.Windows;
using System.Windows.Controls;

namespace Veil.Desktop;

/// <summary>Chooses standard protection or a proxy (HTTP / SOCKS5). Applied after restart (ADR-0006).</summary>
internal sealed class NetworkSettingsDialog : VeilDialog
{
    internal NetworkSettings? Result { get; private set; }

    internal NetworkSettingsDialog(Window owner, NetworkSettings current) : base(owner, "네트워크 보호", "", 540, 600)
    {
        Subtitle.Text = "변경 사항은 VEIL을 다시 시작하면 적용됩니다.";
        var standard = new RadioButton { Content = "표준 보호 — 직접 연결, HTTPS 우선, 추적 방지. IP 주소를 숨기지 않습니다.", Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 8) };
        var proxy = new RadioButton { Content = "프록시 보호 — 지정한 프록시로만 연결합니다. 프록시가 끊기면 직접 연결하지 않고 차단합니다.", Foreground = Res("TextBrush") };
        foreach (var option in new[] { standard, proxy }) option.Resources.Add(typeof(TextBlock), new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } });
        var scheme = new ComboBox { ItemsSource = new[] { "socks5", "http" }, SelectedItem = current.ProxyScheme ?? "socks5", Width = 110, Margin = new Thickness(0, 0, 8, 0) };
        var host = new TextBox { Text = current.ProxyHost ?? "127.0.0.1", Style = (Style)FindResource("PillTextBox"), Height = 32, Width = 220, ToolTip = "프록시 주소", Tag = "" };
        var port = new TextBox { Text = current.ProxyPort > 0 ? current.ProxyPort.ToString() : "9050", Style = (Style)FindResource("PillTextBox"), Height = 32, Width = 100, ToolTip = "포트", Tag = "", Margin = new Thickness(8, 0, 0, 0) };
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 10, 0, 0), Children = { scheme, host, port } };
        var note = new TextBlock
        {
            Text = "WebRTC는 프록시를 거치지 않는 UDP를 쓰지 않고, QUIC은 꺼집니다. 다운로드 금고의 요청도 같은 프록시만 사용합니다. 인증이 필요한 프록시와 DNS·IPv6 누출 검증은 아직 지원하지 않습니다.",
            TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush"), Margin = new Thickness(0, 14, 0, 0),
        };
        // Secure DNS (DoH): system resolver, a listed provider, or a custom https:// template.
        var dnsChoices = new List<string> { "시스템 DNS (보호 안 함)" };
        dnsChoices.AddRange(NetworkPolicy.DnsProviders.Select(p => $"{p.Name} 보안 DNS"));
        dnsChoices.Add("직접 입력");
        var dns = new ComboBox { ItemsSource = dnsChoices, Width = 220 };
        var dnsCustom = new TextBox { Style = (Style)FindResource("PillTextBox"), Height = 32, Width = 300, ToolTip = "https://.../dns-query", Tag = "", Margin = new Thickness(0, 8, 0, 0) };
        int provider = Array.FindIndex(NetworkPolicy.DnsProviders, p => p.Template == current.DnsTemplate);
        dns.SelectedIndex = current.DnsTemplate is null ? 0 : provider >= 0 ? provider + 1 : dnsChoices.Count - 1;
        if (provider < 0 && current.DnsTemplate is not null) dnsCustom.Text = current.DnsTemplate;
        void SyncDns() => dnsCustom.Visibility = dns.SelectedIndex == dnsChoices.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        dns.SelectionChanged += (_, _) => SyncDns();
        SyncDns();
        var dnsPanel = new StackPanel
        {
            Margin = new Thickness(0, 16, 0, 0),
            Children =
            {
                new TextBlock { Text = "보안 DNS — 선택하면 이 DoH 서버로만 주소를 찾고, 실패해도 일반 DNS로 되돌아가지 않습니다.", TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 6) },
                dns, dnsCustom,
            },
        };
        var error = new TextBlock { Foreground = Res("AmberBrush"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        standard.IsChecked = current.Mode == NetworkMode.Standard;
        proxy.IsChecked = current.Mode == NetworkMode.Proxy;
        void Sync() => fields.IsEnabled = proxy.IsChecked == true;
        standard.Checked += (_, _) => Sync();
        proxy.Checked += (_, _) => Sync();
        Sync();
        SetBody(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { standard, proxy, fields, dnsPanel, note, error } } });
        AddFooterButton("취소", () => DialogResult = false);
        AddFooterButton("저장", () =>
        {
            string? template = dns.SelectedIndex == 0 ? null
                : dns.SelectedIndex <= NetworkPolicy.DnsProviders.Length ? NetworkPolicy.DnsProviders[dns.SelectedIndex - 1].Template
                : dnsCustom.Text.Trim();
            var chosen = proxy.IsChecked == true
                ? new NetworkSettings(NetworkMode.Proxy, (string)scheme.SelectedItem, host.Text.Trim(), int.TryParse(port.Text, out int p) ? p : 0, template)
                : new NetworkSettings(DnsTemplate: template);
            if (chosen.Problem is { } problem) { error.Text = problem; return; }
            Result = chosen;
            DialogResult = true;
        }, primary: true);
    }
}
