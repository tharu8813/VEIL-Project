using System.Windows;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private bool SettingsReady() => !closing && !restoring && !session.IsEnding && !session.EngineLost;

    private async void SettingsClick(object sender, RoutedEventArgs args)
    {
        if (FocusManagementPage("veil://settings")) return;
        if (!SettingsReady() || session.SettingsUiOpen || session.PasswordUiOpen || session.PermissionUiOpen)
            return;
        session.SettingsUiOpen = true;
        session.PermissionUiOpen = true;
        SettingsDestination? destination = null;
        try
        {
            var settings = new BrowserSettingsDialog(this, BrowserSettingsCatalog.Build(session));
            if (await ShowManagementPageAsync("veil://settings", settings))
                destination = settings.Destination;
        }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            if (!closing) Status.Text = "설정을 열지 못했습니다.";
        }
        finally
        {
            session.SettingsUiOpen = false;
            session.PermissionUiOpen = false;
        }
        if (!SettingsReady() || destination is null) return;
        // Close the hub tab before opening an existing manager. The original browser tab is reselected,
        // so site-scoped actions still use the original HTTPS context. No web message or URI can invoke this.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!SettingsReady() || session.SettingsUiOpen || session.PasswordUiOpen || session.PermissionUiOpen) return;
            var click = new RoutedEventArgs();
            switch (destination)
            {
                case SettingsDestination.Search: SearchSettingsClick(this, click); break;
                case SettingsDestination.SiteData: SiteDataClick(this, click); break;
                case SettingsDestination.Security: SecurityClick(this, click); break;
                case SettingsDestination.SitePermissions: SitePermissionsClick(this, click); break;
                case SettingsDestination.History: HistoryClick(this, click); break;
                case SettingsDestination.Bookmarks: BookmarksClick(this, click); break;
                case SettingsDestination.Network: NetworkClick(this, click); break;
                case SettingsDestination.Passwords: PasswordsClick(this, click); break;
                case SettingsDestination.Downloads: DownloadsClick(this, click); break;
                case SettingsDestination.Vault: VaultClick(this, click); break;
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }
}
