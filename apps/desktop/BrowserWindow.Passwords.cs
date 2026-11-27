using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private bool PasswordWindowReady() => !session.IsPrivate && !closing && !restoring && !session.IsEnding && !session.EngineLost;

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
            string? page = tab?.Url;
            long epoch = tab?.NavigationEpoch ?? -1;
            long selectionEpoch = permissionSelectionEpoch;
            string? origin = SitePermissionPolicy.Origin(tab?.Url);
            if (tab is null || origin is null || tab.Url != page || !PermissionContextMatches(tab, origin, epoch, selectionEpoch))
                origin = null;

            bool exists = store.Exists;
            password = UserPrompts.Secret("비밀번호 관리자",
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
                confirmation = UserPrompts.Secret("비밀번호 관리자 암호 확인", "동일한 암호를 다시 입력하세요.", this);
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
            if (tab is null || origin is null || tab.Url != page || !PermissionContextMatches(tab, origin, epoch, selectionEpoch))
                origin = null;
            var dialog = new PasswordManagerDialog(this, origin, entries);
            if (await ShowManagementPageAsync("veil://passwords", dialog) != true || !PasswordWindowReady()) return;
            // The native manager intentionally switches tabs and then restores the original tab.
            // Its own confirmation guard verifies the intervening management context.
            selectionEpoch = permissionSelectionEpoch;
            if (dialog.Action is PasswordAction.Rotate or PasswordAction.Backup or PasswordAction.Restore)
            {
                await MaintainPasswordsAsync(store, password, entries, dialog.Action);
                return;
            }
            if (dialog.Selected is not { } selected) return;

            switch (dialog.Action)
            {
                case PasswordAction.Save:
                    if (tab is null || origin is null || page is null || tab.Url != page || selected.Origin != origin ||
                        !PermissionContextMatches(tab, origin, epoch, selectionEpoch)) return;
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
                    reauth = UserPrompts.Secret("비밀번호 열람 인증", "관리자 전용 암호를 다시 입력하세요.", this);
                    if (reauth is null || !PasswordWindowReady()) return;
                    var verified = await store.ReadAsync(reauth);
                    try
                    {
                        if (PasswordWindowReady() && verified.FirstOrDefault(entry =>
                                entry.Origin == selected.Origin && entry.UserName == selected.UserName) is { } login)
                            UserPrompts.ShowModal(new PasswordRevealDialog(this, login), "비밀번호 확인");
                    }
                    finally { verified.Clear(); }
                    break;
                case PasswordAction.Fill:
                    if (tab is null || origin is null || page is null || tab.Url != page || selected.Origin != origin ||
                        !PermissionContextMatches(tab, origin, epoch, selectionEpoch)) return;
                    await FillLoginAsync(tab, selected, page, epoch, selectionEpoch);
                    break;
            }
        }
        // InvalidDataException (wrong passphrase, tampered or invalid store) is not an IOException; an
        // unhandled exception in this async void handler would terminate the browser.
        catch (Exception error) when (RecoverableFailure.Is(error))
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

    private async Task MaintainPasswordsAsync(PasswordStore store, char[] current, List<SavedLogin> entries, PasswordAction action)
    {
        bool Ready() => PasswordWindowReady() && IsActive;
        if (!Ready()) return;
        char[]? secret = null, confirmation = null;
        List<SavedLogin>? imported = null, merged = null;
        try
        {
            if (action == PasswordAction.Rotate)
            {
                secret = UserPrompts.Secret("새 관리자 암호", "새 암호를 12자 이상으로 설정하세요. 이전 백업의 암호는 변경되지 않습니다.", this);
                if (secret is null || !Ready()) return;
                if (secret.Length < 12 || current.AsSpan().SequenceEqual(secret))
                { Status.Text = "기존과 다른 12자 이상의 암호를 입력하세요."; return; }
                confirmation = UserPrompts.Secret("새 관리자 암호 확인", "새 암호를 다시 입력하세요.", this);
                if (confirmation is null || !secret.AsSpan().SequenceEqual(confirmation) || !Ready()) return;
                await store.RotateAsync(current, secret);
                if (Ready()) Status.Text = "관리자 암호를 변경했습니다. 기존 백업은 이전 백업 암호로 여세요.";
            }
            else if (action == PasswordAction.Backup)
            {
                secret = UserPrompts.Secret("백업 전용 암호", "백업을 열 때 필요한 별도의 12자 이상 암호를 설정하세요. 잊으면 백업을 복원할 수 없습니다.", this);
                if (secret is null || !Ready()) return;
                if (secret.Length < 12) { Status.Text = "백업 암호는 12자 이상이어야 합니다."; return; }
                confirmation = UserPrompts.Secret("백업 암호 확인", "백업 암호를 다시 입력하세요.", this);
                if (confirmation is null || !secret.AsSpan().SequenceEqual(confirmation) || !Ready()) return;
                var picker = new SaveFileDialog { Filter = "VEIL 암호화 계정 백업 (*.age)|*.age", DefaultExt = ".age",
                    AddExtension = true, FileName = "veil-passwords-backup.age", OverwritePrompt = false };
                string? path = UserPrompts.PickFile(this, picker);
                if (path is null || !Ready()) return;
                await store.BackupAsync(path, secret, entries);
                if (Ready()) Status.Text = "암호화 백업을 생성했습니다. 백업 암호를 안전하게 보관하세요.";
            }
            else if (action == PasswordAction.Restore)
            {
                var picker = new OpenFileDialog { Filter = "VEIL 암호화 계정 백업 (*.age)|*.age", CheckFileExists = true };
                string? path = UserPrompts.PickFile(this, picker);
                if (path is null || !Ready()) return;
                secret = UserPrompts.Secret("백업 암호", "선택한 백업을 만들 때 사용한 암호를 입력하세요.", this);
                if (secret is null || !Ready()) return;
                imported = await store.ReadBackupAsync(path, secret);
                if (!Ready()) return;
                merged = PasswordStore.MergeBackup(entries, imported);
                int added = merged.Count - entries.Count;
                if (added == 0) { Status.Text = "추가할 계정이 없습니다. 기존 계정은 유지했습니다."; return; }
                if (!UserPrompts.Confirm(this, $"새 계정 {added}개를 추가합니다. 중복 {imported.Count - added}개는 기존 계정을 유지합니다. 복원할까요?",
                    "암호화 백업 복원", MessageBoxImage.Question) || !Ready()) return;
                await store.WriteAsync(current, merged, replace: true);
                if (Ready()) Status.Text = $"계정 {added}개를 복원했습니다. 기존 계정은 유지했습니다.";
            }
        }
        finally
        {
            ProtectedProfileSession.ClearPassword(secret);
            ProtectedProfileSession.ClearPassword(confirmation);
            imported?.Clear();
            merged?.Clear();
        }
    }

    private void UpdateLoginAction() =>
        LoginActionButton.Visibility = !session.IsPrivate && Active is { LoginFormDetected: true } tab && CanUseTab(tab.Item) &&
            tab.EngineView is IEngineLoginForms && SitePermissionPolicy.Origin(tab.Url) is not null
                ? Visibility.Visible : Visibility.Collapsed;

    // Automatic detection returns only a boolean. Values are read only after an explicit native command.
    private async Task ProbeLoginFormAsync(BrowserTab tab)
    {
        if (session.IsPrivate || tab.EngineView is not IEngineLoginForms forms) return;
        string page = tab.Url;
        string? origin = SitePermissionPolicy.Origin(page);
        long epoch = tab.NavigationEpoch;
        long selectionEpoch = permissionSelectionEpoch;
        if (origin is null) return;
        bool Current() => tab.Url == page && ReferenceEquals(tab.EngineView, forms) &&
            !session.IsPrivate && PermissionContextMatches(tab, origin, epoch, selectionEpoch);
        if (!Current()) return;
        try
        {
            bool detected = await forms.ProbeLoginFormAsync(origin, page, Current);
            if (!Current()) return;
            tab.LoginFormDetected = detected;
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
            Active is not { } tab || tab.EngineView is not IEngineLoginForms forms ||
            SitePermissionPolicy.Origin(tab.Url) is not { } origin) return;
        long epoch = tab.NavigationEpoch;
        long selectionEpoch = permissionSelectionEpoch;
        string page = tab.Url;
        bool Current() => PasswordWindowReady() && tab.Url == page && ReferenceEquals(tab.EngineView, forms) &&
            PermissionContextMatches(tab, origin, epoch, selectionEpoch);
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
                var login = await forms.ReadLoginFormAsync(origin, page, Current);
                if (!Current()) return;
                if (login is not null && login.Origin != origin) return;
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
                await FillLoginAsync(tab, selected, page, epoch, selectionEpoch);
            }
        }
        catch (Exception error) when (RecoverableFailure.Is(error))
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

    private async Task FillLoginAsync(BrowserTab tab, SavedLogin login, string page, long epoch, long selectionEpoch)
    {
        if (tab.EngineView is not IEngineLoginForms forms) return;
        bool Current() => PasswordWindowReady() && tab.Url == page && ReferenceEquals(tab.EngineView, forms) &&
            PermissionContextMatches(tab, login.Origin, epoch, selectionEpoch);
        if (!Current()) return;
        bool filled = await forms.FillLoginFormAsync(login, page, Current);
        if (Current())
            Status.Text = filled ? "로그인 정보를 채웠습니다. 제출은 직접 진행하세요." :
                "지원되는 로그인 폼을 찾지 못했습니다. 직접 입력하세요.";
    }
}
