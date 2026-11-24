using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private bool PasswordWindowReady() => !closing && !restoring && !session.IsEnding && !session.EngineLost;

    private async void PasswordsClick(object sender, RoutedEventArgs args)
    {
        if (FocusManagementPage("veil://passwords")) return;
        if (!PasswordWindowReady() || session.PasswordUiOpen || session.PermissionUiOpen) return;
        session.PasswordUiOpen = true;
        session.PermissionUiOpen = true; // no concurrent site permission prompts while entering credentials
        char[]? password = null;
        char[]? confirmation = null;
        char[]? reauth = null;
        List<SavedLogin>? entries = null;
        try
        {
            var store = new PasswordStore(session.Root);
            if (!store.Available)
            {
                Status.Text = "비밀번호 저장소를 사용할 수 없습니다. 암호화 코어를 설치하세요.";
                return;
            }
            var tab = Active;
            long epoch = tab?.NavigationEpoch ?? -1;
            string? origin = SitePermissionPolicy.Origin(tab?.Url);
            if (tab is null || origin is null || !PermissionContextMatches(tab, origin, epoch))
                origin = null;

            bool exists = store.Exists;
            password = ProfileUnlockDialog.Prompt("비밀번호 관리자",
                exists ? "비밀번호 관리자 전용 암호를 입력하세요." :
                "별도의 긴 암호(12자 이상)를 설정하세요. 잊으면 저장된 계정을 복구할 수 없습니다.", this);
            if (password is null || !PasswordWindowReady()) return;
            if (!exists)
            {
                if (password.Length < 12)
                {
                    Status.Text = "비밀번호 관리자 암호는 12자 이상이어야 합니다.";
                    return;
                }
                confirmation = ProfileUnlockDialog.Prompt("비밀번호 관리자 암호 확인", "동일한 암호를 다시 입력하세요.", this);
                if (confirmation is null || !password.AsSpan().SequenceEqual(confirmation) || !PasswordWindowReady())
                {
                    Status.Text = "암호가 일치하지 않거나 설정이 취소되었습니다.";
                    return;
                }
                ProtectedProfileSession.ClearPassword(confirmation);
                confirmation = null;
                await store.WriteAsync(password, [], replace: false);
            }
            if (!PasswordWindowReady()) return;
            entries = await store.ReadAsync(password);
            if (!PasswordWindowReady()) return;
            if (tab is null || origin is null || !PermissionContextMatches(tab, origin, epoch))
                origin = null;
            var dialog = new PasswordManagerDialog(this, origin, entries);
            if (await ShowManagementPageAsync("veil://passwords", dialog) != true || !PasswordWindowReady() || dialog.Selected is not { } selected) return;

            switch (dialog.Action)
            {
                case PasswordAction.Save:
                    if (tab is null || origin is null || selected.Origin != origin ||
                        !PermissionContextMatches(tab, origin, epoch)) return;
                    entries.RemoveAll(entry => entry.Origin == selected.Origin && entry.UserName == selected.UserName);
                    entries.Add(selected);
                    await store.WriteAsync(password, entries, replace: true);
                    if (PasswordWindowReady()) Status.Text = "계정을 암호화해 저장했습니다.";
                    break;
                case PasswordAction.Remove:
                    entries.Remove(selected);
                    await store.WriteAsync(password, entries, replace: true);
                    if (PasswordWindowReady()) Status.Text = "선택한 계정을 삭제했습니다.";
                    break;
                case PasswordAction.Reveal:
                    reauth = ProfileUnlockDialog.Prompt("비밀번호 열람 인증", "관리자 전용 암호를 다시 입력하세요.", this);
                    if (reauth is null || !PasswordWindowReady()) return;
                    var verified = await store.ReadAsync(reauth);
                    try
                    {
                        if (PasswordWindowReady() && verified.FirstOrDefault(entry =>
                                entry.Origin == selected.Origin && entry.UserName == selected.UserName) is { } login)
                            new PasswordRevealDialog(this, login).ShowDialog();
                    }
                    finally { verified.Clear(); }
                    break;
                case PasswordAction.Fill:
                    if (tab is null || origin is null || selected.Origin != origin ||
                        !PermissionContextMatches(tab, origin, epoch)) return;
                    await FillLoginAsync(tab, selected, epoch);
                    break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or COMException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if (PasswordWindowReady())
                Status.Text = "비밀번호 작업을 완료하지 못했습니다. 암호, 저장소 상태 및 현재 페이지를 확인하세요.";
        }
        finally
        {
            ProtectedProfileSession.ClearPassword(password);
            ProtectedProfileSession.ClearPassword(confirmation);
            ProtectedProfileSession.ClearPassword(reauth);
            entries?.Clear();
            session.PasswordUiOpen = false;
            session.PermissionUiOpen = false;
        }
    }

    private void UpdateLoginAction() =>
        LoginActionButton.Visibility = Active is { LoginFormDetected: true } tab && !closingTabs.Contains(tab.Item) &&
            SitePermissionPolicy.Origin(tab.Url) is not null ? Visibility.Visible : Visibility.Collapsed;

    // Automatic detection returns only a boolean. Values are read only after an explicit native command.
    private async Task ProbeLoginFormAsync(BrowserTab tab)
    {
        string? origin = SitePermissionPolicy.Origin(tab.Url);
        long epoch = tab.NavigationEpoch;
        if (origin is null || !PermissionContextMatches(tab, origin, epoch)) return;
        try
        {
            string result = await tab.View.CoreWebView2.ExecuteScriptAsync(LoginFormPolicy.Probe(origin));
            if (!PermissionContextMatches(tab, origin, epoch)) return;
            tab.LoginFormDetected = result == "true";
            UpdateLoginAction();
        }
        catch (Exception error) when (error is InvalidOperationException or COMException) { }
    }

    private void LoginActionClick(object sender, RoutedEventArgs args)
    {
        if (!PasswordWindowReady() || sender is not System.Windows.Controls.Button button || button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private async void SavePageLoginClick(object sender, RoutedEventArgs args) => await PageLoginAsync(save: true);
    private async void FillPageLoginClick(object sender, RoutedEventArgs args) => await PageLoginAsync(save: false);

    private async Task PageLoginAsync(bool save)
    {
        if (!PasswordWindowReady() || session.PasswordUiOpen || session.PermissionUiOpen || session.SettingsUiOpen ||
            Active is not { } tab || SitePermissionPolicy.Origin(tab.Url) is not { } origin) return;
        long epoch = tab.NavigationEpoch;
        string page = tab.Url;
        bool Current() => tab.Url == page && PermissionContextMatches(tab, origin, epoch);
        if (!Current()) return;
        session.PasswordUiOpen = true;
        session.PermissionUiOpen = true;
        char[]? password = null, confirmation = null;
        List<SavedLogin>? entries = null;
        try
        {
            var store = new PasswordStore(session.Root);
            if (!store.Available || (!save && !store.Exists))
            {
                Status.Text = "저장된 계정 또는 암호화 코어가 없습니다.";
                return;
            }
            bool exists = store.Exists;
            password = ProfileUnlockDialog.Prompt("비밀번호 관리자",
                exists ? "관리자 전용 암호를 입력하세요." : "별도의 긴 암호(12자 이상)를 설정하세요. 잊으면 복구할 수 없습니다.", this);
            if (password is null || !Current()) return;
            if (!exists)
            {
                if (password.Length < 12) { Status.Text = "관리자 암호는 12자 이상이어야 합니다."; return; }
                confirmation = ProfileUnlockDialog.Prompt("비밀번호 관리자 암호 확인", "동일한 암호를 다시 입력하세요.", this);
                if (confirmation is null || !password.AsSpan().SequenceEqual(confirmation) || !Current()) return;
                ProtectedProfileSession.ClearPassword(confirmation);
                confirmation = null;
            }
            entries = exists ? await store.ReadAsync(password) : [];
            if (!Current()) return;
            if (save)
            {
                string json = await tab.View.CoreWebView2.ExecuteScriptAsync(LoginFormPolicy.Read(origin));
                if (!Current()) return;
                var login = LoginFormPolicy.Decode(origin, json);
                if (login is null)
                {
                    Status.Text = "지원되는 로그인 폼에 계정과 비밀번호를 먼저 입력하세요.";
                    return;
                }
                bool replace = entries.Any(e => e.Origin == origin && e.UserName == login.UserName);
                if (MessageBox.Show(this, origin + "\n" + (replace ? "이 계정의 비밀번호를 갱신할까요?" : "현재 폼의 계정과 비밀번호를 암호화해 저장할까요?"),
                    "비밀번호 저장", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes || !Current()) return;
                entries.RemoveAll(e => e.Origin == origin && e.UserName == login.UserName);
                entries.Add(login);
                await store.WriteAsync(password, entries, replace: exists);
                if (PasswordWindowReady()) Status.Text = "계정을 암호화해 저장했습니다.";
            }
            else
            {
                var matches = entries.Where(e => e.Origin == origin).ToList();
                if (matches.Count == 0) { Status.Text = "현재 사이트에 저장된 계정이 없습니다."; return; }
                var picker = new LoginPickerDialog(this, origin, matches);
                if (picker.ShowDialog() != true || !Current() || picker.Selected is not { } selected) return;
                await FillLoginAsync(tab, selected, epoch);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or COMException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if (PasswordWindowReady()) Status.Text = "비밀번호 작업을 완료하지 못했습니다. 현재 페이지와 저장소를 확인하세요.";
        }
        finally
        {
            ProtectedProfileSession.ClearPassword(password);
            ProtectedProfileSession.ClearPassword(confirmation);
            entries?.Clear();
            session.PasswordUiOpen = false;
            session.PermissionUiOpen = false;
        }
    }

    private async Task FillLoginAsync(BrowserTab tab, SavedLogin login, long epoch)
    {
        if (!PermissionContextMatches(tab, login.Origin, epoch)) return;
        string result = await tab.View.CoreWebView2.ExecuteScriptAsync(LoginFormPolicy.Fill(login));
        if (PasswordWindowReady())
            Status.Text = result == "true" ? "로그인 정보를 채웠습니다. 제출은 직접 진행하세요." :
                "지원되는 로그인 폼을 찾지 못했습니다. 직접 입력하세요.";
    }
}
