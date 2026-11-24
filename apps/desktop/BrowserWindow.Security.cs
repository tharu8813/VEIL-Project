using System.IO;
using System.Windows;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private async void SecurityClick(object sender, RoutedEventArgs args)
    {
        if (FocusManagementPage("veil://settings/security")) return;
        if (closing || restoring || session.IsEnding || session.EngineLost) return;
        if (session.SettingsUiOpen || session.PasswordUiOpen || session.PermissionUiOpen) return;
        session.SettingsUiOpen = true;
        session.PermissionUiOpen = true;
        try
        {
            var dialog = new SecuritySettingsDialog(this, session.Security);
            if (await ShowManagementPageAsync("veil://settings/security", dialog) != true || dialog.Result is not { } chosen ||
                closing || session.IsEnding || session.EngineLost) return;
            ChangeSecurity(chosen);
        }
        finally
        {
            session.SettingsUiOpen = false;
            session.PermissionUiOpen = false;
        }
    }

    /// <summary>Saves first, then applies to every open view; failures are shown, never ignored.</summary>
    private bool ChangeSecurity(SecuritySettings chosen)
    {
        try
        {
            session.Security.Change(chosen, settings =>
            {
                var before = data.Security;
                data.Security = settings == SecuritySettings.Default ? null : settings;
                try { data.Save(statePath); }
                catch { data.Security = before; throw; }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "보안 설정을 저장하지 못했습니다. 이전 설정이 그대로 적용됩니다.";
            UpdateSecurityIndicators();
            return false;
        }
        UpdateSecurityIndicators();
        Status.Text = session.Security.State == SmartScreenState.Failed
            ? session.Security.Describe()
            : chosen.SmartScreen ? "SmartScreen을 켰습니다." : "SmartScreen을 껐습니다. 피싱·악성 사이트 경고를 받지 못합니다.";
        return session.Security.State != SmartScreenState.Failed;
    }

    private void UpdateSecurityIndicators()
    {
        foreach (var window in session.Windows) window.UpdateNetworkIndicator();
    }

    /// <summary>Status-bar suffix: shown only when SmartScreen is off or could not be applied.</summary>
    private string SecuritySuffix() => session.Security switch
    {
        { State: SmartScreenState.Failed } => " · SmartScreen 적용 실패",
        { Saved.SmartScreen: false } => " · SmartScreen 꺼짐",
        { Saved.Recovered: true } => " · 보안 설정 복원됨(기본값)",
        _ => "",
    } + session.Security.ThreatListSuffix();
}
