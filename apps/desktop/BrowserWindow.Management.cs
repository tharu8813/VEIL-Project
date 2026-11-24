using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private readonly HashSet<string> openingManagement = new(StringComparer.Ordinal);

    internal static bool IsManagementUrl(string? url) => url is
        "veil://settings" or "veil://bookmarks" or "veil://history";

    private BrowserTab? FindManagementTab(string route) => Tabs.Items.OfType<TabItem>()
        .Select(item => (BrowserTab)item.Tag).FirstOrDefault(tab => tab.Url == route && !closingTabs.Contains(tab.Item));

    private bool FocusManagementPage(string route)
    {
        if (!SettingsReady()) return false;
        if (FindManagementTab(route) is not { CancelManagement: not null } tab)
            return openingManagement.Contains(route);
        Tabs.SelectedItem = tab.Item;
        return true;
    }

    private void OpenManagementRoute(string route)
    {
        if (!SettingsReady() || FocusManagementPage(route)) return;
        var args = new RoutedEventArgs();
        switch (route)
        {
            case "veil://settings": SettingsClick(this, args); break;
            case "veil://bookmarks": BookmarksClick(this, args); break;
            case "veil://history": HistoryClick(this, args); break;
        }
    }

    private FrameworkElement ManagementLanding(BrowserTab tab)
    {
        // Session restoration never repeats an authenticated action.
        var open = new Button
        {
            Content = InternalTitle(tab.Url) + " 열기", Style = (Style)FindResource("PrimaryButton"),
            Height = 36, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 0, 18, 0),
        };
        open.Click += (_, _) => { if (Active == tab && tab.Owner == this) OpenManagementRoute(tab.Url); };
        var panel = new StackPanel { Margin = new Thickness(32), MaxWidth = 900 };
        panel.Children.Add(new TextBlock
        {
            Text = InternalTitle(tab.Url), FontSize = 24, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 20),
        });
        panel.Children.Add(open);
        return new Border { Background = (Brush)FindResource("InkBrush"), Child = panel };
    }

    private void ClearManagement(BrowserTab tab)
    {
        var cancel = tab.CancelManagement;
        var page = tab.ManagementSurface;
        tab.CancelManagement = null;
        tab.ManagementSurface = null;
        if (page is not null) Pages.Children.Remove(page);
        cancel?.Invoke();
    }

    private void UpdateManagementVisibility()
    {
        foreach (TabItem item in Tabs.Items)
        {
            var tab = (BrowserTab)item.Tag;
            bool selected = item == Tabs.SelectedItem;
            bool management = IsManagementUrl(tab.Url);
            if (management && tab.ManagementSurface is null)
            {
                tab.ManagementSurface = ManagementLanding(tab);
                Pages.Children.Add(tab.ManagementSurface);
            }
            if (tab.ManagementSurface is { } page)
                page.Visibility = selected && management ? Visibility.Visible : Visibility.Collapsed;
            tab.View.Visibility = selected && !management && tab.Url != "about:blank"
                ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private async Task<bool> ShowManagementPageAsync(string route, VeilDialog dialog)
    {
        BrowserTab? tab = null;
        FrameworkElement? page = null;
        var previous = Active;
        bool reserved = false;
        try
        {
            if (!SettingsReady() || !IsManagementUrl(route) || FocusManagementPage(route)) return false;
            reserved = openingManagement.Add(route);
            if (!reserved) return false;
            tab = FindManagementTab(route) ?? await OpenTab(route);
            if (tab is null || !SettingsReady() || tab.Owner != this || !Tabs.Items.Contains(tab.Item)) return false;
            ClearManagement(tab);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            page = dialog.TakePage(accepted =>
            {
                if (Active == tab && SettingsReady() && IsActive) completion.TrySetResult(accepted);
            });
            tab.ManagementSurface = page;
            tab.CancelManagement = () => completion.TrySetResult(false);
            Pages.Children.Add(page);
            Tabs.SelectedItem = tab.Item;
            RefreshChrome();
            bool accepted = await completion.Task;
            if (tab.ManagementSurface == page)
            {
                ClearManagement(tab);
                if (SettingsReady() && Active == tab && Tabs.Items.Contains(tab.Item))
                {
                    if (previous is not null && previous != tab && previous.Owner == this &&
                        Tabs.Items.Contains(previous.Item) && !closingTabs.Contains(previous.Item))
                        Tabs.SelectedItem = previous.Item;
                    await CloseTabAsync(tab.Item);
                }
                else if (SettingsReady()) RefreshChrome();
            }
            return accepted && SettingsReady();
        }
        finally
        {
            if (reserved) openingManagement.Remove(route);
            if (tab is not null && page is not null && tab.ManagementSurface == page)
                ClearManagement(tab);
            dialog.Close();
        }
    }
}
