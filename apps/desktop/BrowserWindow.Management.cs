using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private readonly HashSet<string> openingManagement = new(StringComparer.Ordinal);
    private long managementFocusEpoch;

    internal static bool IsManagementUrl(string? url) => url is
        "veil://settings" or "veil://bookmarks" or "veil://history" or "veil://passwords" or "veil://vault" or "veil://downloads" or
        "veil://settings/network" or "veil://settings/security" or "veil://settings/search" or "veil://settings/site-data" or "veil://settings/permissions" or "veil://tabs";

    private BrowserTab? FindManagementTab(string route) => Tabs.Items.OfType<TabItem>()
        .Select(item => (BrowserTab)item.Tag).FirstOrDefault(tab => tab.Url == route && CanUseTab(tab.Item));

    private bool FocusManagementPage(string route)
    {
        if (!SettingsReady()) return false;
        // A pending native editor belongs to one window; focus it rather than opening another.
        foreach (var window in session.Windows.ToArray())
        {
            if (!window.SettingsReady()) continue;
            if (window.FindManagementTab(route) is { CancelManagement: not null } tab)
            {
                window.Tabs.SelectedItem = tab.Item;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                if (window != this) window.Activate();
                return true;
            }
            if (window.openingManagement.Contains(route)) return true;
        }
        return false;
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
            case "veil://passwords": PasswordsClick(this, args); break;
            case "veil://vault": VaultClick(this, args); break;
            case "veil://downloads": DownloadsClick(this, args); break;
            case "veil://settings/network": NetworkClick(this, args); break;
            case "veil://settings/security": SecurityClick(this, args); break;
            case "veil://settings/search": SearchSettingsClick(this, args); break;
            case "veil://settings/site-data": SiteDataClick(this, args); break;
            case "veil://settings/permissions": SitePermissionsClick(this, args); break;
            case "veil://tabs": TabSearchClick(this, args); break;
        }
    }

    private FrameworkElement ManagementLanding(BrowserTab tab)
    {
        // Download overview is native and needs no unlock or automatic privileged operation.
        if (tab.Url == "veil://downloads" && !session.IsPrivate)
            return new DownloadOverview(this, session, () => SettingsReady() && Active == tab &&
                tab.Url == "veil://downloads" && CanUseTab(tab.Item),
                ClearDownloadIssues);
        // Session restoration never repeats an authenticated action.
        var open = new Button
        {
            Content = InternalTitle(tab.Url) + " 열기", Style = (Style)FindResource("PrimaryButton"),
            Height = 36, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 0, 18, 0),
        };
        open.Click += (_, _) =>
        {
            if (Active == tab && CanUseTab(tab.Item))
                OpenManagementRoute(tab.Url);
        };
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
            if (management && tab.ManagementSurface is null && !tab.Closing && !closingTabs.Contains(item))
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
            if (tab is null || !SettingsReady() || tab.Url != route || !CanUseTab(tab.Item)) return false;
            ClearManagement(tab);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long epoch = tab.NavigationEpoch;
            bool ContextValid() => Active == tab && tab.ManagementSurface == page &&
                tab.Url == route && tab.NavigationEpoch == epoch && CanUseTab(tab.Item) && SettingsReady() && IsActive;
            long confirmedFocus = -1;
            page = dialog.TakePage(accepted =>
            {
                if (completion.Task.IsCompleted || !ContextValid()) return;
                confirmedFocus = managementFocusEpoch;
                completion.TrySetResult(accepted);
            });
            tab.ManagementSurface = page;
            tab.CancelManagement = () => completion.TrySetResult(false);
            Pages.Children.Add(page);
            Tabs.SelectedItem = tab.Item;
            RefreshChrome();
            bool accepted = await completion.Task;
            // Cancel if the page was detached or left between the native click and its continuation.
            accepted = accepted && ContextValid() && confirmedFocus == managementFocusEpoch;
            if (tab.ManagementSurface == page)
            {
                ClearManagement(tab);
                if (SettingsReady() && Active == tab && Tabs.Items.Contains(tab.Item))
                {
                    if (previous is not null && previous != tab && CanUseTab(previous.Item))
                    {
                        Tabs.SelectedItem = previous.Item;
                        // The user's confirmed action must not wait for a visual close animation.
                        CloseTab(tab.Item);
                    }
                    else RefreshChrome(); // Keep a harmless landing when no previous live tab exists.
                }
                else if (SettingsReady()) RefreshChrome();
            }
            return accepted && SettingsReady() && IsActive;
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
