using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Veil.Desktop;

/// <summary>Native settings index. Only typed destinations reach the existing native handlers.</summary>
internal sealed class BrowserSettingsDialog : VeilDialog
{
    internal SettingsDestination? Destination { get; private set; }

    internal BrowserSettingsDialog(Window owner, IReadOnlyList<SettingsItem> items)
        : base(owner, "설정", "", 860, 620)
    {
        MinWidth = 680;
        MinHeight = 460;
        Subtitle.Text = "VEIL 브라우저";
        var search = new TextBox
        {
            Style = (Style)FindResource("PillTextBox"), Height = 36, Tag = "",
            ToolTip = "설정 검색", Margin = new Thickness(0, 0, 0, 14),
        };
        System.Windows.Automation.AutomationProperties.SetName(search, "설정 검색");
        var nav = new ListBox { Style = (Style)FindResource("VeilList"), Margin = new Thickness(0, 0, 12, 0) };
        System.Windows.Automation.AutomationProperties.SetName(nav, "설정 분류");
        foreach (var section in Enum.GetValues<SettingsSection>())
            nav.Items.Add(new ListBoxItem { Content = BrowserSettingsCatalog.SectionName(section), Tag = section });

        var title = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        var rows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var right = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(title, Dock.Top);
        right.Children.Add(title);
        right.Children.Add(scroll);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        columns.Children.Add(nav);
        columns.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        root.Children.Add(columns);
        SetBody(root);

        TextBlock Paragraph(string text, bool muted = true) => new()
        {
            Text = text, Foreground = Res(muted ? "MutedBrush" : "TextBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0),
        };

        void Render()
        {
            string query = search.Text.Trim();
            var section = (nav.SelectedItem as ListBoxItem)?.Tag is SettingsSection selected
                ? selected : SettingsSection.General;
            bool filtering = query.Length != 0;
            var shown = items.Where(item => filtering ? item.Matches(query) : item.Section == section).ToList();
            title.Text = filtering ? $"검색 결과 ({shown.Count})" : BrowserSettingsCatalog.SectionName(section);
            rows.Children.Clear();
            foreach (var item in shown)
            {
                var content = new StackPanel();
                if (filtering)
                    content.Children.Add(Paragraph(BrowserSettingsCatalog.SectionName(item.Section)));
                content.Children.Add(new TextBlock
                {
                    Text = item.Title, FontWeight = FontWeights.SemiBold, FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(Paragraph(item.Description));
                if (item.State is not null)
                    content.Children.Add(Paragraph(item.State, muted: false));
                if (item.Destination is { } destination)
                {
                    var open = new Button
                    {
                        Content = "열기", Style = (Style)FindResource("GhostButton"),
                        HorizontalAlignment = HorizontalAlignment.Right, Height = 32,
                        Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 8, 0, 0),
                    };
                    System.Windows.Automation.AutomationProperties.SetName(open, item.Title + " 열기");
                    open.Click += (_, _) =>
                    {
                        Destination = destination;
                        Complete(true);
                    };
                    content.Children.Add(open);
                }
                rows.Children.Add(new Border
                {
                    Background = Res("SurfaceBrush"), BorderBrush = Res("LineBrush"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10),
                    Child = content,
                });
            }
            if (shown.Count == 0)
                rows.Children.Add(Paragraph("검색어에 해당하는 설정이 없습니다."));
            scroll.ScrollToTop();
        }

        nav.SelectionChanged += (_, _) =>
        {
            if (search.Text.Length != 0) search.Clear();
            else Render();
        };
        search.TextChanged += (_, _) => Render();
        nav.SelectedIndex = 0;
        AddFooterButton("닫기", Dismiss, primary: true);
        OnContentLoaded(() => Keyboard.Focus(search));
        Render();
    }
}
