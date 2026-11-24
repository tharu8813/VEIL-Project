using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Veil.Desktop;

/// <summary>Bookmarks (editable) and history, searchable, in the shared dark dialog shell.</summary>
internal sealed class LibraryWindow : VeilDialog
{
    internal string? SelectedUrl { get; private set; }

    internal LibraryWindow(Window owner, string title, List<PageEntry> entries, bool editable, Func<bool> save)
        : base(owner, title, editable ? "" : "", 760, 540)
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

        // History scope is independent of the text query: bulk deletion always names its scope.
        var range = new ComboBox { Width = 130, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (string label in new[] { "전체 기간", "최근 1시간", "최근 24시간", "최근 7일", "최근 30일" })
            range.Items.Add(label);
        range.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(range, "방문 기록 기간");
        DateTimeOffset? Cutoff() => range.SelectedIndex switch
        {
            1 => DateTimeOffset.Now.AddHours(-1),
            2 => DateTimeOffset.Now.AddDays(-1),
            3 => DateTimeOffset.Now.AddDays(-7),
            4 => DateTimeOffset.Now.AddDays(-30),
            _ => null,
        };
        bool InRange(PageEntry entry, DateTimeOffset? cutoff) => cutoff is null || entry.Visited >= cutoff.Value;
        if (!editable)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var clear = new Button
            {
                Content = "선택 기간 삭제",
                Style = (Style)FindResource("GhostButton"),
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(8, 0, 0, 0),
            };
            clear.Click += (_, _) => ClearRange();
            bar.Children.Add(range);
            bar.Children.Add(clear);
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);
        }
        var listArea = new Grid { Children = { list, empty } };
        panel.Children.Add(listArea);
        SetBody(panel);

        PageEntry? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as PageEntry;

        void Refresh()
        {
            string filter = search.Text.Trim();
            var cutoff = editable ? null : Cutoff();
            var matches = entries.Where(entry => InRange(entry, cutoff) &&
                (entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                 entry.Url.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
            list.Items.Clear();
            foreach (var entry in matches)
            {
                var row = new ListBoxItem { Tag = entry, Content = Row(rowGlyph, entry.Title, entry.Url, When(entry.Visited)) };
                System.Windows.Automation.AutomationProperties.SetName(row, $"{entry.Title} {entry.Url}");
                list.Items.Add(row);
            }
            Subtitle.Text = filter.Length == 0 && cutoff is null ? $"{entries.Count}개 항목" : $"{matches.Length}개 일치 · 전체 {entries.Count}개";
            ((TextBlock)((StackPanel)empty.Child).Children[1]).Text = entries.Count == 0
                ? (editable ? "저장한 북마크가 없습니다.\nCtrl+D로 현재 페이지를 추가하세요." : "방문 기록이 없습니다.")
                : "선택한 기간 또는 검색어와 일치하는 항목이 없습니다.";
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

        void Commit(Action change)
        {
            // The list is shared by all windows. Do not report success if the protected state
            // could not be saved; restore the in-memory list before showing the error.
            var previous = entries.ToArray();
            change();
            if (!save())
            {
                entries.Clear();
                entries.AddRange(previous);
                Refresh();
                MessageBox.Show(this, "변경사항을 저장하지 못했습니다. 기존 목록을 유지합니다.",
                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Refresh();
        }

        void Remove()
        {
            if (Selected() is not { } entry) return;
            Commit(() => entries.Remove(entry));
        }

        void ClearRange()
        {
            if (editable) return;
            var cutoff = Cutoff();
            var targets = entries.Where(entry => InRange(entry, cutoff)).ToArray();
            if (targets.Length == 0) return;
            string scope = range.SelectedItem as string ?? "전체 기간";
            if (MessageBox.Show(this,
                $"{scope}의 VEIL 방문 기록 {targets.Length}개를 삭제할까요?\n검색어와 관계없이 이 기간의 항목을 삭제합니다. 쿠키, 사이트 데이터 및 엔진 기록은 지우지 않습니다.",
                "방문 기록 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            Commit(() =>
            {
                foreach (var entry in targets) entries.Remove(entry);
            });
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
                int index = entries.IndexOf(entry);
                if (index < 0) return;
                string newTitle = name.Text.Trim();
                Commit(() => entries[index] = entry with { Title = newTitle });
            });
        }
        AddFooterButton("닫기", Close);
        AddFooterButton("열기", Open, primary: true);

        search.TextChanged += (_, _) => Refresh();
        range.SelectionChanged += (_, _) => Refresh();
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
