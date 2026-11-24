using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private bool suppressAddressSuggestions;
    private BrowserTab? suggestionTab;

    private void InitializeAddressSuggestions()
    {
        UpdateSearchHint();
        Address.TextChanged += (_, _) => RefreshAddressSuggestions();
        Address.LostKeyboardFocus += (_, _) => CloseAddressSuggestions();
        Deactivated += (_, _) => CloseAddressSuggestions();
        AddressSuggestionsList.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.OriginalSource is not DependencyObject element ||
                ItemsControl.ContainerFromElement(AddressSuggestionsList, element) is not ListBoxItem row ||
                row.Content is not AddressSuggestion selected) return;
            args.Handled = true;
            OpenAddressSuggestion(selected, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        };
    }

    private void UpdateSearchHint() =>
        Address.ToolTip = $"주소 또는 {SearchPolicy.Name(data.SearchEngine)} 검색 (Ctrl+L)";

    private void SetAddressText(string text)
    {
        suppressAddressSuggestions = true;
        try { Address.Text = text; }
        finally { suppressAddressSuggestions = false; }
    }

    private void RefreshAddressSuggestions()
    {
        if (suppressAddressSuggestions) return;
        if (closing || restoring || session.IsEnding || session.EngineLost || !IsActive ||
            !Address.IsKeyboardFocusWithin || Active is not { } tab || closingTabs.Contains(tab.Item))
        {
            CloseAddressSuggestions();
            return;
        }
        var matches = SearchPolicy.Suggest(data, Address.Text);
        if (matches.Count == 0)
        {
            CloseAddressSuggestions();
            return;
        }
        suggestionTab = tab;
        AddressSuggestionsList.ItemsSource = matches;
        AddressSuggestionsList.SelectedIndex = -1; // never auto-select or overwrite typed text
        AddressSuggestionsPopup.IsOpen = true;
    }

    private void CloseAddressSuggestions()
    {
        AddressSuggestionsPopup.IsOpen = false;
        AddressSuggestionsList.ItemsSource = null;
        suggestionTab = null;
    }

    private void MoveAddressSuggestion(int direction)
    {
        int count = AddressSuggestionsList.Items.Count;
        if (count == 0) return;
        int selected = AddressSuggestionsList.SelectedIndex;
        int index = selected < 0 ? (direction > 0 ? 0 : count - 1) : selected + direction;
        // Moving past either end returns to the original typed input (no auto-selection).
        AddressSuggestionsList.SelectedIndex = index < 0 || index >= count ? -1 : index;
        if (AddressSuggestionsList.SelectedIndex >= 0)
            AddressSuggestionsList.ScrollIntoView(AddressSuggestionsList.Items[index]);
    }

    private void OpenAddressSuggestion(AddressSuggestion selected, bool newTab)
    {
        var tab = suggestionTab;
        bool valid = !closing && !restoring && !session.IsEnding && !session.EngineLost && IsActive &&
            tab is not null && tab == Active && tab.Owner == this && !closingTabs.Contains(tab.Item) && data.LocalAddressSuggestions != false &&
            SearchPolicy.Suggest(data, Address.Text).Any(item => item.Url == selected.Url);
        CloseAddressSuggestions();
        if (!valid || tab is null || !IsWebUrl(selected.Url)) return;
        if (newTab) _ = OpenTab(selected.Url, index: Tabs.Items.IndexOf(tab.Item) + 1);
        else NavigateAddress(selected.Url);
    }

    private async void SearchSettingsClick(object sender, RoutedEventArgs args)
    {
        if (FocusManagementPage("veil://settings/search")) return;
        if (!SettingsReady() || session.SettingsUiOpen || session.PasswordUiOpen || session.PermissionUiOpen) return;
        session.SettingsUiOpen = true;
        session.PermissionUiOpen = true;
        try
        {
            var dialog = new SearchSettingsDialog(this, data.SearchEngine, data.LocalAddressSuggestions != false);
            if (await ShowManagementPageAsync("veil://settings/search", dialog) != true || !SettingsReady() || !SearchPolicy.IsKnown(dialog.Engine)) return;
            var previous = data.SearchEngine;
            var previousLocal = data.LocalAddressSuggestions;
            data.SearchEngine = dialog.Engine == SearchPolicy.Default ? null : dialog.Engine;
            data.LocalAddressSuggestions = dialog.LocalSuggestions ? null : false;
            try { data.Save(statePath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                data.SearchEngine = previous;
                data.LocalAddressSuggestions = previousLocal;
                Status.Text = "검색 설정을 저장하지 못했습니다. 이전 설정을 유지합니다.";
                return;
            }
            foreach (var window in session.Windows.ToArray())
            {
                if (window.closing) continue;
                window.CloseAddressSuggestions();
                window.UpdateSearchHint();
            }
            Status.Text = "검색 및 주소창 설정을 저장했습니다.";
        }
        finally
        {
            session.SettingsUiOpen = false;
            session.PermissionUiOpen = false;
        }
    }
}
