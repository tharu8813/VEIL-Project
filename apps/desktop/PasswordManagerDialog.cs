using System.Windows;
using System.Windows.Controls;

namespace Veil.Desktop;

internal enum PasswordAction { None, Save, Remove, Reveal, Fill, Rotate, Backup, Restore }

/// <summary>Native manager; passwords are never included in list rows, search, or tooltips.</summary>
internal sealed class PasswordManagerDialog : VeilDialog
{
    internal PasswordAction Action { get; private set; }
    internal SavedLogin? Selected { get; private set; }

    internal PasswordManagerDialog(Window owner, string? origin, List<SavedLogin> entries)
        : base(owner, "비밀번호 관리자", "", 740, 570)
    {
        Subtitle.Text = "저장된 계정 · 비밀번호는 별도 암호로 보호됩니다.";
        var list = new ListBox { Style = (Style)FindResource("VeilList"), MinHeight = 90, MaxHeight = 210 };
        foreach (var entry in entries.OrderByDescending(entry => entry.Origin == origin)
                     .ThenBy(entry => entry.Origin).ThenBy(entry => entry.UserName))
            list.Items.Add(new ListBoxItem { Tag = entry, Content = Row("", entry.UserName, entry.Origin) });
        System.Windows.Automation.AutomationProperties.SetName(list, "저장된 계정");
        var user = new TextBox { MaxLength = 512, Height = 32, Style = (Style)FindResource("PillTextBox"), Tag = "" };
        var password = new PasswordBox { MaxLength = 2048, Height = 32 };
        System.Windows.Automation.AutomationProperties.SetName(user, "사용자 이름");
        System.Windows.Automation.AutomationProperties.SetName(password, "웹사이트 비밀번호");
        var panel = new StackPanel { Children = { list } };
        TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap,
            Foreground = Res("MutedBrush"), Margin = new Thickness(0, 8, 0, 5) };
        panel.Children.Add(Text(origin is null ? "계정을 등록하려면 HTTPS 사이트를 먼저 여세요." : $"현재 사이트: {origin}"));
        panel.Children.Add(Text("사용자 이름"));
        panel.Children.Add(user);
        panel.Children.Add(Text("비밀번호 (직접 입력)"));
        panel.Children.Add(password);
        var save = new Button { Content = "현재 사이트에 저장 / 갱신", Height = 32,
            Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 8, 0, 0),
            IsEnabled = origin is not null };
        save.Click += (_, _) =>
        {
            using var entered = password.SecurePassword;
            if (origin is null || string.IsNullOrWhiteSpace(user.Text) || entered.Length == 0) return;
            Selected = new SavedLogin(origin, user.Text, password.Password);
            Action = PasswordAction.Save;
            Complete(true);
        };
        panel.Children.Add(save);
        panel.Children.Add(Text("자동완성은 현재 사이트와 출처가 일치하는 단일 로그인 폼만 지원합니다. 비밀번호를 자동 제출하거나 다른 사이트로 보내지 않습니다."));
        var maintenance = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Maintenance(string label, PasswordAction action)
        {
            var button = new Button { Content = label, Height = 32, Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 6), Style = (Style)FindResource("GhostButton") };
            button.Click += (_, _) => { Action = action; Complete(true); };
            maintenance.Children.Add(button);
        }
        Maintenance("관리자 암호 변경", PasswordAction.Rotate);
        Maintenance("암호화 백업", PasswordAction.Backup);
        Maintenance("백업 복원", PasswordAction.Restore);
        panel.Children.Add(maintenance);
        SetBody(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Choose(PasswordAction action)
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is not SavedLogin entry) return;
            if (action == PasswordAction.Fill && entry.Origin != origin) return;
            if (action == PasswordAction.Remove && MessageBox.Show(PromptOwner, "선택한 계정을 삭제할까요?",
                    "비밀번호 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Selected = entry;
            Action = action;
            Complete(true);
        }
        AddFooterButton("삭제", () => Choose(PasswordAction.Remove));
        AddFooterButton("비밀번호 보기", () => Choose(PasswordAction.Reveal));
        var fill = AddFooterButton("현재 페이지 채우기", () => Choose(PasswordAction.Fill), primary: true);
        fill.IsEnabled = false;
        list.SelectionChanged += (_, _) =>
        {
            fill.IsEnabled = (list.SelectedItem as ListBoxItem)?.Tag is SavedLogin entry && entry.Origin == origin;
        };
        AddFooterButton("닫기", Dismiss);
        Closed += (_, _) => password.Clear();
    }
}

/// <summary>Reveal only after a separate successful authentication; no explicit clipboard action.</summary>
internal sealed class PasswordRevealDialog : VeilDialog
{
    internal PasswordRevealDialog(Window owner, SavedLogin login)
        : base(owner, "비밀번호 확인", "", 480, 300)
    {
        Subtitle.Text = login.Origin;
        var secret = new TextBlock { Text = login.Password, TextWrapping = TextWrapping.Wrap, FontSize = 17 };
        SetBody(new StackPanel { Children = {
            new TextBlock { Text = login.UserName, Margin = new Thickness(0, 0, 0, 12) }, secret
        } });
        AddFooterButton("닫기", Dismiss, primary: true);
        Closed += (_, _) => secret.Text = "";
    }
}


/// <summary>Native selection, current origin only. Never displays password values.</summary>
internal sealed class LoginPickerDialog : VeilDialog
{
    internal SavedLogin? Selected { get; private set; }

    internal LoginPickerDialog(Window owner, string origin, List<SavedLogin> entries)
        : base(owner, "로그인 정보 채우기", "", 500, 360)
    {
        Subtitle.Text = origin;
        var list = new ListBox { Style = (Style)FindResource("VeilList") };
        foreach (var entry in entries.Where(e => e.Origin == origin).OrderBy(e => e.UserName))
            list.Items.Add(new ListBoxItem { Tag = entry, Content = Row("", entry.UserName, origin) });
        System.Windows.Automation.AutomationProperties.SetName(list, "현재 사이트 계정");
        SetBody(list);
        AddFooterButton("취소", Dismiss);
        var fill = AddFooterButton("채우기", () =>
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is SavedLogin login && login.Origin == origin)
            {
                Selected = login;
                Complete(true);
            }
        }, primary: true);
        fill.IsEnabled = false;
        list.SelectionChanged += (_, _) => fill.IsEnabled = list.SelectedItem is not null;
    }
}
