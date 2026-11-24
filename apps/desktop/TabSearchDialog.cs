using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Veil.Desktop;

internal sealed record TabSearchRow(int Id, string Title, string Url, int Window, bool Pinned);

internal sealed class TabSearchDialog : VeilDialog
{
    internal int? SelectedId { get; private set; }

    internal TabSearchDialog(Window owner, IReadOnlyList<TabSearchRow> tabs)
        : base(owner, "탭 검색", "", 700, 510)
    {
        var search = new TextBox
        {
            Style = (Style)FindResource("PillTextBox"), Height = 34, Tag = "",
            Margin = new Thickness(0, 0, 0, 12),
        };
        var list = new ListBox { Style = (Style)FindResource("VeilList") };
        System.Windows.Automation.AutomationProperties.SetName(search, "탭 검색어");
        System.Windows.Automation.AutomationProperties.SetName(list, "열린 탭");
        var empty = new TextBlock { Text = "일치하는 탭이 없습니다.", Foreground = Res("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false };
        var root = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        root.Children.Add(new Grid { Children = { list, empty } });
        SetBody(root);

        void Refresh()
        {
            string query = search.Text.Trim();
            var matches = tabs.Where(tab => tab.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                tab.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Items.Clear();
            foreach (var tab in matches)
            {
                var item = new ListBoxItem { Tag = tab, Content = Row(tab.Pinned ? "" : "",
                    tab.Title, tab.Url, $"창 {tab.Window}" + (tab.Pinned ? " · 고정" : "")) };
                System.Windows.Automation.AutomationProperties.SetName(item, $"{tab.Title} 창 {tab.Window}" + (tab.Pinned ? " 고정" : ""));
                list.Items.Add(item);
            }
            list.SelectedIndex = matches.Count == 0 ? -1 : 0;
            empty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Subtitle.Text = $"열린 탭 {tabs.Count}개 · 검색 결과 {matches.Count}개";
        }

        void Open()
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is not TabSearchRow tab) return;
            SelectedId = tab.Id;
            Complete(true);
        }

        search.TextChanged += (_, _) => Refresh();
        search.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { args.Handled = true; Open(); }
            else if (args.Key is Key.Up or Key.Down && list.Items.Count > 0)
            {
                args.Handled = true;
                list.SelectedIndex = Math.Clamp(list.SelectedIndex + (args.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
            }
        };
        list.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { args.Handled = true; Open(); }
        };
        list.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element &&
                ItemsControl.ContainerFromElement(list, element) is ListBoxItem) Open();
        };
        AddFooterButton("닫기", Dismiss);
        AddFooterButton("탭으로 이동", Open, primary: true);
        OnContentLoaded(() => search.Focus());
        Refresh();
    }
}
