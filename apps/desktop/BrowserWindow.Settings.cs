using System.Windows;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private bool SettingsReady() => !session.IsPrivate && !closing && !restoring && !session.IsEnding && !session.EngineLost;

    private void OpenDefaultBrowserSettings()
    {
        if (!SettingsReady()) return;
        if (!ExternalAddress.Installed)
        {
            Status.Text = "기본 브라우저 등록은 보호 프로필이 등록된 설치본에서만 지원합니다.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ms-settings:defaultapps?registeredAppMachine=VEIL") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Status.Text = "Windows 기본 앱 설정을 열지 못했습니다."; }
    }

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
        if (!SettingsReady() || destination is null || Active is not { } source || !CanUseTab(source.Item)) return;
        long epoch = source.NavigationEpoch, focus = managementFocusEpoch;
        // The original browser tab is reselected if it still exists. The queued command must not
        // operate on another site or window if the user switches context before it runs.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!SettingsReady() || !IsActive || Active != source || !CanUseTab(source.Item) ||
                source.NavigationEpoch != epoch || managementFocusEpoch != focus ||
                session.SettingsUiOpen || session.PasswordUiOpen || session.PermissionUiOpen) return;
            var click = new RoutedEventArgs();
            switch (destination)
            {
                case SettingsDestination.DefaultBrowser: OpenDefaultBrowserSettings(); break;
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
