using System.Windows;
using System.Windows.Controls;

namespace Veil.Desktop;

internal sealed class SearchSettingsDialog : VeilDialog
{
    internal string? Engine { get; private set; }
    internal bool LocalSuggestions { get; private set; }

    internal SearchSettingsDialog(Window owner, string? current, bool localSuggestions)
        : base(owner, "검색 및 주소창", "", 560, 380)
    {
        Subtitle.Text = "검색어는 직접 검색할 때만 선택한 검색 엔진으로 전송합니다.";
        var providers = new ComboBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var provider in SearchPolicy.Providers) providers.Items.Add(provider.Name);
        providers.SelectedIndex = Array.FindIndex(SearchPolicy.Providers, provider => provider.Id == SearchPolicy.Normalize(current));
        System.Windows.Automation.AutomationProperties.SetName(providers, "기본 검색 엔진");
        var local = new CheckBox
        {
            Content = "방문 기록 및 북마크로 주소 추천", IsChecked = localSuggestions,
            Foreground = Res("TextBrush"), Margin = new Thickness(0, 18, 0, 8),
        };
        System.Windows.Automation.AutomationProperties.SetName(local, "로컬 주소 추천");
        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "기본 검색 엔진", Margin = new Thickness(0, 0, 0, 8) },
                providers, local,
                new TextBlock
                {
                    Text = "주소 추천은 이 프로필의 기록과 북마크만 사용합니다. 입력 중인 글자를 외부 서버로 전송하지 않습니다. 실제 검색 시 선택한 서비스의 개인정보 정책이 적용됩니다.\n\n? 검색어: 기본 엔진으로 강제 검색\n@google, @bing, @naver, @duckduckgo + 검색어: 이번 검색만 해당 엔진 사용",
                    TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush"),
                },
            },
        };
        SetBody(panel);
        AddFooterButton("취소", () => Complete(false));
        AddFooterButton("저장", () =>
        {
            if (providers.SelectedIndex < 0) return;
            Engine = SearchPolicy.Providers[providers.SelectedIndex].Id;
            LocalSuggestions = local.IsChecked == true;
            Complete(true);
        }, primary: true);
    }
}
