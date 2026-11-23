using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Veil.Desktop;

/// <summary>Bookmarks (editable) and history, searchable, in the shared dark dialog shell.</summary>
internal sealed class LibraryWindow : VeilDialog
{
    internal string? SelectedUrl { get; private set; }

    internal LibraryWindow(Window owner, string title, List<PageEntry> entries, bool editable, Action save)
        : base(owner, title, editable ? "" : "", 680, 500)
    {
        string rowGlyph = editable ? "" : "";
        var search = new TextBox
        {
            Style = (Style)FindResource("PillTextBox"),
            Height = 36,
            Tag = "",
            ToolTip = $"{title} 검색",
            Margin = new Thickness(0, 0, 0, 12),
        };
        System.Windows.Automation.AutomationProperties.SetName(search, $"{title} 검색");
        var list = new ListBox { Style = (Style)FindResource("VeilList") };
        System.Windows.Automation.AutomationProperties.SetName(list, title);
        var empty = EmptyState(editable ? "" : "", "");
        var panel = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        panel.Children.Add(search);
        var listArea = new Grid { Children = { list, empty } };
        panel.Children.Add(listArea);
        SetBody(panel);

        PageEntry? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as PageEntry;

        void Refresh()
        {
            string filter = search.Text.Trim();
            var matches = entries.Where(entry => entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                entry.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.Items.Clear();
            foreach (var entry in matches)
            {
                var row = new ListBoxItem { Tag = entry, Content = Row(rowGlyph, entry.Title, entry.Url, When(entry.Visited)) };
                System.Windows.Automation.AutomationProperties.SetName(row, $"{entry.Title} {entry.Url}");
                list.Items.Add(row);
            }
            Subtitle.Text = filter.Length == 0 ? $"{entries.Count}개 항목" : $"{matches.Length}개 일치 · 전체 {entries.Count}개";
            ((TextBlock)((StackPanel)empty.Child).Children[1]).Text = entries.Count == 0
                ? (editable ? "저장한 북마크가 없습니다.\nCtrl+D로 현재 페이지를 추가하세요." : "방문 기록이 없습니다.")
                : "검색어와 일치하는 항목이 없습니다.";
            empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (matches.Length == 0) Motion.Enter(empty, 0, dy: 10, scale: 0.96, ms: 500);
            Cascade(list);
        }

        void Open()
        {
            if (Selected() is not { } entry) return;
            SelectedUrl = entry.Url;
            DialogResult = true;
        }

        async void Remove()
        {
            if (Selected() is not { } entry || list.SelectedItem is not ListBoxItem row) return;
            if (Motion.Enabled)
            {
                var (_, move) = Motion.Transforms(row);
                move.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, Motion.To(40, 200));
                row.BeginAnimation(OpacityProperty, Motion.To(0, 180));
                await Task.Delay(190);
            }
            entries.Remove(entry);
            save();
            Refresh();
        }

        AddFooterButton("선택 항목 삭제", Remove);
        if (editable)
        {
            var name = new TextBox
            {
                Style = (Style)FindResource("PillTextBox"),
                Width = 190,
                Height = 32,
                Tag = "",
                ToolTip = "북마크 이름",
            };
            System.Windows.Automation.AutomationProperties.SetName(name, "북마크 이름");
            AddFooterElement(name);
            list.SelectionChanged += (_, _) => name.Text = Selected()?.Title ?? "";
            AddFooterButton("이름 저장", () =>
            {
                if (Selected() is not { } entry || string.IsNullOrWhiteSpace(name.Text)) return;
                entries[entries.IndexOf(entry)] = entry with { Title = name.Text.Trim() };
                save();
                Refresh();
            });
        }
        AddFooterButton("닫기", Close);
        AddFooterButton("열기", Open, primary: true);

        search.TextChanged += (_, _) => Refresh();
        list.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element &&
                ItemsControl.ContainerFromElement(list, element) is ListBoxItem) Open();
        };
        list.KeyDown += (_, args) => { if (args.Key == Key.Enter) Open(); };
        Loaded += (_, _) => search.Focus();
        Refresh();
    }

    private static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("M월 d일");
    }
}

/// <summary>Cancelled download requests, explained until the vault exists.</summary>
internal sealed class DownloadsWindow : VeilDialog
{
    internal DownloadsWindow(Window owner, IReadOnlyList<string> downloads)
        : base(owner, "다운로드", "", 540, 420)
    {
        Subtitle.Text = $"취소한 요청 {downloads.Count}건";
        var notice = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 12),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x14, 0xF2, 0xB8, 0x4B)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xF2, 0xB8, 0x4B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "금고 검증 전까지 파일 다운로드를 취소합니다.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xD0, 0x8A)),
                FontSize = 12,
            },
        };
        var list = new ListBox { Style = (Style)FindResource("VeilList") };
        System.Windows.Automation.AutomationProperties.SetName(list, "다운로드");
        foreach (string entry in downloads)
        {
            string[] parts = entry.Split(" · ", 2);
            list.Items.Add(new ListBoxItem
            {
                Content = Row("", parts[0], "다운로드 요청 차단", parts.Length > 1 ? parts[1] : null, Res("AmberBrush")),
            });
        }
        var panel = new DockPanel();
        DockPanel.SetDock(notice, Dock.Top);
        panel.Children.Add(notice);
        var area = new Grid { Children = { list } };
        if (downloads.Count == 0) area.Children.Add(EmptyState("", "취소한 다운로드가 없습니다."));
        panel.Children.Add(area);
        SetBody(panel);
        AddFooterButton("닫기", Close, primary: true);
        Loaded += (_, _) =>
        {
            Motion.Wipe(notice, 220, 700);
            Cascade(list);
        };
    }
}
