using System.Windows;
using System.Windows.Controls;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    internal int PinnedTabCount => Tabs.Items.Cast<TabItem>().TakeWhile(item => item.Tag is BrowserTab { Pinned: true }).Count();

    private int TabInsertionIndex(bool pinned, int requested)
    {
        int boundary = PinnedTabCount;
        return pinned ? Math.Clamp(requested, 0, boundary) : Math.Clamp(requested, boundary, Tabs.Items.Count);
    }

    private bool CanSwapTabs(TabItem first, TabItem second) =>
        !closingTabs.Contains(first) && !closingTabs.Contains(second) &&
        first.Tag is BrowserTab a && second.Tag is BrowserTab b && a.Pinned == b.Pinned;

    private static void UpdatePinnedHeader(BrowserTab tab)
    {
        tab.Header.Label.Visibility = tab.Pinned ? Visibility.Collapsed : Visibility.Visible;
        tab.Header.Close.Visibility = tab.Pinned ? Visibility.Collapsed : Visibility.Visible;
        tab.Header.Pin.Visibility = tab.Pinned ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabToolTip(tab);
    }

    private void SetTabPinned(TabItem item, bool pinned)
    {
        if (closing || session.IsEnding || !Tabs.Items.Contains(item) || closingTabs.Contains(item) ||
            item.Tag is not BrowserTab tab || tab.Pinned == pinned) return;
        // Remove before changing the flag so the remaining prefix is valid throughout.
        var selected = Tabs.SelectedItem;
        Tabs.Items.Remove(item);
        int boundary = PinnedTabCount;
        tab.Pinned = pinned;
        Tabs.Items.Insert(boundary, item);
        UpdatePinnedHeader(tab);
        if (selected is not null && Tabs.Items.Contains(selected)) Tabs.SelectedItem = selected;
        LayoutTabs(animate: false);
        SaveTabs();
    }

    private void TabSearchClick(object sender, RoutedEventArgs args)
    {
        if (!SettingsReady() || session.SettingsUiOpen || session.PermissionUiOpen || session.PasswordUiOpen) return;
        CloseAddressSuggestions();
        var live = session.Windows.Where(window => !window.closing && !window.restoring)
            .SelectMany((window, index) => window.Tabs.Items.OfType<TabItem>()
                .Where(item => !window.closingTabs.Contains(item))
                .Select(item => (Tab: (BrowserTab)item.Tag, Window: index + 1))).ToList();
        var rows = live.Select((entry, index) =>
            new TabSearchRow(index, entry.Tab.Title, entry.Tab.Url, entry.Window, entry.Tab.Pinned)).ToList();
        session.SettingsUiOpen = true;
        session.PermissionUiOpen = true;
        int? selected = null;
        try
        {
            var dialog = new TabSearchDialog(this, rows);
            if (dialog.ShowDialog() == true) selected = dialog.SelectedId;
        }
        finally
        {
            session.SettingsUiOpen = false;
            session.PermissionUiOpen = false;
        }
        if (!SettingsReady() || selected is not int id || id < 0 || id >= live.Count) return;
        var tab = live[id].Tab;
        var owner = tab.Owner;
        if (owner.closing || owner.restoring || owner.closingTabs.Contains(tab.Item) ||
            !session.Windows.Contains(owner) || !owner.Tabs.Items.Contains(tab.Item)) return;
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        owner.Tabs.SelectedItem = tab.Item;
        owner.Activate();
    }
}
