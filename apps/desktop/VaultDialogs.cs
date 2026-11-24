using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Veil.Desktop;

/// <summary>
/// Vault passphrase and recovery-code wizard (ADR-0005). Depending on the mode it asks for a new
/// passphrase (twice), and/or shows a fresh recovery code once and requires it to be typed back,
/// which is the confirmation that the code was backed up outside this computer.
/// </summary>
internal sealed class VaultSetupDialog : VeilDialog
{
    internal string? Passphrase { get; private set; }
    internal string? RecoveryCode { get; private set; }
    internal VaultBackupSource? Backup { get; private set; }

    internal VaultSetupDialog(Window? owner, string title, bool askPassphrase, bool issueCode,
        bool allowRestore = false, string? passwordPrompt = null)
        : base(owner, title, "", 520, 440)
    {
        var steps = new List<Func<UIElement>>();
        var first = new PasswordBox { Height = 34, MaxLength = 256 };
        var second = new PasswordBox { Height = 34, MaxLength = 256, Margin = new Thickness(0, 10, 0, 0) };
        string code = DownloadVault.NewRecoveryCode();
        var confirm = new TextBox { Height = 34, Style = (Style)FindResource("PillTextBox"), ToolTip = "복구 코드 다시 입력", Tag = "" };
        var error = new TextBlock { Foreground = Res("AmberBrush"), Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        int index = 0;

        if (askPassphrase)
            steps.Add(() => Page(passwordPrompt ?? "금고 암호를 정하세요. 8자 이상, 다른 곳에서 쓰지 않는 암호를 권장합니다.",
                Labeled("새 암호", first), Labeled("새 암호 확인", second), error));
        if (issueCode)
        {
            steps.Add(() => Page("복구 코드입니다. 암호를 잊으면 이 코드로만 금고를 열 수 있습니다. 종이나 별도 장치 등 이 컴퓨터 밖에 보관하세요. 이 화면을 닫으면 다시 표시되지 않으며 VEIL은 이 코드를 저장하지 않습니다.",
                new TextBox
                {
                    Text = code, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 17,
                    Padding = new Thickness(12), Background = Res("RaisedBrush"), Foreground = Res("TextBrush"),
                    BorderBrush = Res("LineBrush"), TextWrapping = TextWrapping.Wrap,
                }));
            steps.Add(() => Page("보관한 복구 코드를 그대로 입력해 확인하세요.", confirm, error));
        }

        var host = new ContentControl();
        SetBody(host);
        Button? next = null;
        void Show()
        {
            error.Text = "";
            host.Content = steps[index]();
            next!.Content = index == steps.Count - 1 ? "완료" : "다음";
            Subtitle.Text = $"{index + 1} / {steps.Count}";
        }
        AddFooterButton("취소", () => DialogResult = false);
        if (allowRestore)
            AddFooterButton("백업에서 복원", () =>
            {
                var source = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "금고 백업 선택", Filter = "VEIL 금고 백업 (*.veil-backup)|*.veil-backup",
                    AddToRecent = false,
                };
                if (source.ShowDialog(this) != true) return;
                var restore = new VaultSetupDialog(this, "금고 백업 복원", askPassphrase: true, issueCode: true,
                    passwordPrompt: "백업을 만들 때 정한 암호를 입력하고 새 금고 복구 코드를 보관하세요.");
                if (restore.ShowDialog() != true) return;
                Backup = new VaultBackupSource(source.FileName, restore.Passphrase!, restore.RecoveryCode!);
                DialogResult = true;
            });
        next = AddFooterButton("다음", () =>
        {
            if (askPassphrase && index == 0)
            {
                if (first.Password.Length < 8) { error.Text = "8자 이상 입력하세요."; return; }
                if (first.Password != second.Password) { error.Text = "두 암호가 다릅니다."; return; }
                if (first.Password.IndexOfAny(['\t', '\r', '\n']) >= 0) { error.Text = "탭이나 줄바꿈은 쓸 수 없습니다."; return; }
            }
            if (issueCode && index == steps.Count - 1 && DownloadVault.NormalizeCode(confirm.Text) != code)
            {
                error.Text = "복구 코드가 일치하지 않습니다. 보관한 코드를 확인하세요.";
                return;
            }
            if (index < steps.Count - 1) { index++; Show(); return; }
            Passphrase = askPassphrase ? first.Password : null;
            RecoveryCode = issueCode ? code : null;
            DialogResult = true;
        }, primary: true);
        Show();
    }

    private UIElement Page(string text, params UIElement[] children)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush"), Margin = new Thickness(0, 0, 0, 14) });
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private UIElement Labeled(string label, UIElement field) => new StackPanel
    {
        Margin = new Thickness(0, 6, 0, 0),
        Children = { new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) }, field },
    };
}

/// <summary>Opens the vault with its passphrase, or with the recovery code.</summary>
internal sealed class VaultUnlockDialog : VeilDialog
{
    internal string? Passphrase { get; private set; }
    internal string? RecoveryCode { get; private set; }

    internal VaultUnlockDialog(Window? owner, string? error = null, string? prompt = null) : base(owner, "다운로드 금고 열기", "", 460, 330)
    {
        Subtitle.Text = prompt ?? "다운로드를 보관하거나 내보내려면 금고를 열어야 합니다.";
        var password = new PasswordBox { Height = 34, MaxLength = 256 };
        var code = new TextBox { Height = 34, Style = (Style)FindResource("PillTextBox"), ToolTip = "복구 코드", Tag = "", Visibility = Visibility.Collapsed };
        var message = new TextBlock { Text = error ?? "", Foreground = Res("AmberBrush"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        var toggle = new Button { Content = "암호를 잊었습니다 — 복구 코드로 열기", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        toggle.Click += (_, _) =>
        {
            bool recover = code.Visibility != Visibility.Visible;
            code.Visibility = recover ? Visibility.Visible : Visibility.Collapsed;
            password.Visibility = recover ? Visibility.Collapsed : Visibility.Visible;
            toggle.Content = recover ? "암호로 열기" : "암호를 잊었습니다 — 복구 코드로 열기";
        };
        SetBody(new StackPanel { Children = { password, code, toggle, message } });
        AddFooterButton("취소", () => DialogResult = false);
        AddFooterButton("열기", () =>
        {
            if (code.Visibility == Visibility.Visible) RecoveryCode = code.Text;
            else Passphrase = password.Password;
            if (string.IsNullOrEmpty(Passphrase) && string.IsNullOrWhiteSpace(RecoveryCode)) return;
            DialogResult = true;
        }, primary: true);
        Loaded += (_, _) => password.Focus();
    }
}
