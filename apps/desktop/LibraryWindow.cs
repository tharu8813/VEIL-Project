using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Veil.Desktop;

internal sealed class LibraryWindow : Window
{
    internal string? SelectedUrl { get; private set; }

    internal LibraryWindow(Window owner, string title, List<PageEntry> entries, bool editable, Action save)
    {
        Owner = owner;
        Title = title;
        Width = 640;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(16) };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6) };
        System.Windows.Automation.AutomationProperties.SetName(search, $"{title} 검색");
        DockPanel.SetDock(search, Dock.Top);
        panel.Children.Add(search);
        var list = new ListBox();
        System.Windows.Automation.AutomationProperties.SetName(list, title);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(commands, Dock.Bottom);
        panel.Children.Add(commands);
        panel.Children.Add(list);
        Content = panel;

        void Refresh()
        {
            string filter = search.Text.Trim();
            list.ItemsSource = entries.Where(entry => entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                entry.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        void Open()
        {
            if (list.SelectedItem is not PageEntry entry) return;
            SelectedUrl = entry.Url;
            DialogResult = true;
        }

        void AddButton(string label, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) => action();
            commands.Children.Add(button);
        }

        AddButton("열기", Open);
        AddButton("선택 항목 삭제", () =>
        {
            if (list.SelectedItem is not PageEntry entry) return;
            entries.Remove(entry);
            save();
            Refresh();
        });
        if (editable)
        {
            var name = new TextBox { Width = 170, Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(5) };
            System.Windows.Automation.AutomationProperties.SetName(name, "북마크 이름");
            commands.Children.Add(name);
            list.SelectionChanged += (_, _) => name.Text = (list.SelectedItem as PageEntry)?.Title ?? "";
            AddButton("이름 저장", () =>
            {
                if (list.SelectedItem is not PageEntry entry || string.IsNullOrWhiteSpace(name.Text)) return;
                entries[entries.IndexOf(entry)] = entry with { Title = name.Text.Trim() };
                save();
                Refresh();
            });
        }
        AddButton("닫기", Close);
        search.TextChanged += (_, _) => Refresh();
        list.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element &&
                ItemsControl.ContainerFromElement(list, element) is ListBoxItem) Open();
        };
        list.KeyDown += (_, args) => { if (args.Key == Key.Enter) Open(); };
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        Loaded += (_, _) => search.Focus();
        Refresh();
    }
}
