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

/// <summary>Vault manager: stored items with export, delete and integrity check, plus key rotation.</summary>
internal sealed class VaultWindow : VeilDialog
{
    private readonly DownloadVault vault;
    private readonly ListBox list;
    private readonly Border empty;

    internal VaultWindow(Window owner, DownloadVault vault) : base(owner, "다운로드 금고", "", 740, 520)
    {
        this.vault = vault;
        list = new ListBox { Style = (Style)FindResource("VeilList") };
        System.Windows.Automation.AutomationProperties.SetName(list, "금고 항목");
        empty = EmptyState("", "금고에 보관한 파일이 없습니다.");
        SetBody(new Grid { Children = { list, empty } });
        if (!vault.UsesProfilePassword) AddFooterButton("암호 변경", async () => await RotateAsync(askPassphrase: true));
        AddFooterButton("복구 코드 재발급", async () => await RotateAsync(askPassphrase: false));
        AddFooterButton("내용 키 교체", async () => await RotateContentAsync());
        AddFooterButton("전체 검사", async () => await VerifyAllAsync());
        AddFooterButton("백업", async () => await BackupAsync());
        AddFooterButton("삭제", async () => await DeleteAsync());
        AddFooterButton("내보내기", async () => await ExportAsync(), primary: true);
        vault.Changed += Refresh;
        Closed += (_, _) => vault.Changed -= Refresh;
        Refresh();
    }

    private VaultEntry? Selected => (list.SelectedItem as ListBoxItem)?.Tag as VaultEntry;

    private void Refresh() => Dispatcher.Invoke(() =>
    {
        list.Items.Clear();
        foreach (var entry in vault.Entries)
            list.Items.Add(new ListBoxItem
            {
                Tag = entry,
                Content = Row("", entry.Name, $"{entry.Bytes / 1024d / 1024d:0.00} MB · {entry.SavedAt.LocalDateTime:yyyy-MM-dd HH:mm}" +
                    (entry.SourceHost is null ? "" : $" · {entry.SourceHost}"), entry.Recovered ? "복구됨" : "암호화"),
            });
        empty.Visibility = vault.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = $"{vault.Entries.Count}개 항목 · 파일은 내보낼 때만 평문으로 만들어집니다.";
    });

    private async Task ExportAsync()
    {
        if (Selected is not { } entry) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = entry.Name, Title = "금고에서 내보내기", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            long bytes = await vault.ExportAsync(entry, dialog.FileName);
            Subtitle.Text = $"{entry.Name}을(를) 내보냈습니다 ({bytes:N0} bytes).";
        }
        catch (IOException error) { Subtitle.Text = error.Message; }
        catch (UnauthorizedAccessException) { Subtitle.Text = "선택한 위치에 쓸 수 없습니다."; }
    }

    private async Task BackupAsync()
    {
        var destination = new Microsoft.Win32.SaveFileDialog
        {
            Title = "금고 백업 저장", FileName = "VEIL-" + DateTime.Today.ToString("yyyy-MM-dd"),
            DefaultExt = ".veil-backup", Filter = "VEIL 금고 백업 (*.veil-backup)|*.veil-backup", OverwritePrompt = false,
        };
        if (destination.ShowDialog(this) != true) return;
        var setup = new VaultSetupDialog(this, "금고 백업 암호", askPassphrase: true, issueCode: false,
            passwordPrompt: "백업 전용 암호를 정하세요. 복원할 때 이 암호가 필요합니다.");
        if (setup.ShowDialog() != true) return;
        try
        {
            Subtitle.Text = "암호화 백업을 만드는 중입니다.";
            long count = await vault.BackupAsync(destination.FileName, setup.Passphrase!);
            Subtitle.Text = $"금고 항목 {count:N0}개를 백업했습니다.";
        }
        catch (IOException error) { Subtitle.Text = error.Message; }
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } entry) return;
        if (MessageBox.Show(this, $"{entry.Name}을(를) 금고에서 영구 삭제할까요? 되돌릴 수 없습니다.", "금고 항목 삭제",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await vault.DeleteAsync(entry);
    }

    private async Task VerifyAllAsync()
    {
        int bad = 0;
        foreach (var entry in vault.Entries.ToList())
            try { await vault.VerifyAsync(entry); } catch (IOException) { bad++; }
        Subtitle.Text = bad == 0 ? $"{vault.Entries.Count}개 항목 모두 무결성 검사를 통과했습니다." : $"{bad}개 항목이 손상되었습니다. 내보낼 수 없습니다.";
    }

    private async Task RotateAsync(bool askPassphrase)
    {
        var dialog = new VaultSetupDialog(this, askPassphrase ? "금고 암호 변경" : "복구 코드 재발급", askPassphrase, issueCode: !askPassphrase);
        if (dialog.ShowDialog() != true) return;
        bool ok = askPassphrase ? await vault.ChangePassphraseAsync(dialog.Passphrase!) : await vault.ReissueRecoveryCodeAsync(dialog.RecoveryCode!);
        Subtitle.Text = ok ? (askPassphrase ? "금고 암호를 바꿨습니다." : "새 복구 코드로 바꿨습니다. 이전 코드는 더 이상 쓸 수 없습니다.") : "변경하지 못했습니다.";
    }

    private async Task RotateContentAsync()
    {
        if (MessageBox.Show(this, "금고 파일을 새 키로 다시 암호화합니다. 금고 크기만큼의 추가 공간이 필요합니다. 계속할까요?",
            "금고 내용 키 교체", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var setup = new VaultSetupDialog(this, "새 금고 복구 코드", askPassphrase: false, issueCode: true);
        if (setup.ShowDialog() != true) return;
        char[]? password = ProfileUnlockDialog.Prompt("금고 내용 키 교체", vault.UsesProfilePassword
            ? "현재 보호 프로필 암호를 입력하세요." : "현재 금고 암호를 입력하세요.", this);
        if (password is null) return;
        try
        {
            Subtitle.Text = "금고 내용을 새 키로 암호화하고 있습니다.";
            Subtitle.Text = (await vault.RotateContentKeysAsync(password, setup.RecoveryCode!)) switch
            {
                true => "내용 키와 복구 코드를 교체했습니다.",
                false => "키는 교체됐지만 이전 암호화 파일을 모두 정리하지 못했습니다.",
                null => "키를 교체하지 못했습니다. 금고 암호, 무결성과 남은 공간을 확인하세요.",
            };
        }
        catch (IOException)
        {
            Subtitle.Text = "교체 결과를 확인하지 못했습니다. 금고를 다시 열어 확인하세요.";
        }
        finally { ProtectedProfileSession.ClearPassword(password); }
    }
}
