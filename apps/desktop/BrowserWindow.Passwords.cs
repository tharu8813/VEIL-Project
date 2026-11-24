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

    private async Task FillLoginAsync(BrowserTab tab, SavedLogin login, long epoch)
    {
        if (!PermissionContextMatches(tab, login.Origin, epoch)) return;
        // Serialize as JSON, not JavaScript interpolation of raw values. The JS checks the origin
        // again at execution time and never submits a form or traverses frames.
        string origin = JsonSerializer.Serialize(login.Origin);
        string username = JsonSerializer.Serialize(login.UserName);
        string secret = JsonSerializer.Serialize(login.Password);
        string script = $$"""
            (() => {
                if (window !== window.top || location.origin !== {{origin}} || document.visibilityState !== 'visible') return false;
                const visible = el => !el.disabled && !el.readOnly && el.getClientRects().length > 0 &&
                    getComputedStyle(el).visibility === 'visible';
                const passwords = [...document.querySelectorAll('input[type="password"]')].filter(visible);
                if (passwords.length !== 1) return false;
                const pass = passwords[0], form = pass.form;
                if (!form || pass.value || pass.autocomplete === 'new-password' ||
                    new URL(form.action, location.href).origin !== location.origin ||
                    [...form.querySelectorAll('[formaction]')].some(el =>
                        new URL(el.getAttribute('formaction'), location.href).origin !== location.origin)) return false;
                const users = [...form.elements].filter(el => el instanceof HTMLInputElement && visible(el) &&
                    ['text', 'email', 'tel'].includes(el.type) && el.autocomplete !== 'one-time-code');
                const explicit = users.filter(el => el.autocomplete.split(/\s+/).includes('username'));
                const user = explicit.length === 1 ? explicit[0] : explicit.length === 0 && users.length === 1 ? users[0] : null;
                if (!user || (user.value && user.value !== {{username}})) return false;
                const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                if (location.origin !== {{origin}}) return false;
                set.call(user, {{username}});
                set.call(pass, {{secret}});
                for (const el of [user, pass]) {
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                }
                return true;
            })()
            """;
        string result = await tab.View.CoreWebView2.ExecuteScriptAsync(script);
        if (PasswordWindowReady())
            Status.Text = result == "true" ? "로그인 정보를 채웠습니다. 제출은 직접 진행하세요." :
                "지원되는 로그인 폼을 찾지 못했습니다. 직접 입력하세요.";
    }
}
