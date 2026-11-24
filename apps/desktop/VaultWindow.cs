using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Veil.Desktop;

/// <summary>Native vault manager. Reuses the existing authenticated vault operations and gates.</summary>
internal sealed class VaultWindow : VeilDialog
{
    private readonly DownloadVault vault;
    private readonly Func<bool> sessionReady;
    private readonly ListBox list;
    private readonly Border empty;
    private readonly TextBox search;
    private readonly ComboBox sort;
    private readonly CheckBox recovered;
    private readonly TextBlock details;
    private readonly TextBlock feedback;
    private readonly List<Button> actions = [];
    private readonly Button preview, verify, delete, export;
    private bool busy, closed;

    internal VaultWindow(Window owner, DownloadVault vault, Func<bool> sessionReady)
        : base(owner, "다운로드 금고", "", 880, 650)
    {
        this.vault = vault;
        this.sessionReady = sessionReady;
        MinWidth = 680;
        MinHeight = 490;
        search = new TextBox { Style = (Style)FindResource("PillTextBox"), Height = 34, Tag = "",
            MinWidth = 160, Margin = new Thickness(0, 0, 10, 0), ToolTip = "파일 이름 또는 출처 검색" };
        System.Windows.Automation.AutomationProperties.SetName(search, "금고 검색");
        sort = new ComboBox { Width = 125, Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (string label in new[] { "최신순", "오래된 순", "이름순", "큰 파일순", "작은 파일순" }) sort.Items.Add(label);
        sort.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(sort, "파일 정렬");
        recovered = new CheckBox { Content = "복구된 항목만", Foreground = Res("TextBrush"),
            Margin = new Thickness(0, 9, 0, 8) };
        list = new ListBox { Style = (Style)FindResource("VeilList") };
        System.Windows.Automation.AutomationProperties.SetName(list, "금고 항목");
        empty = EmptyState("", "표시할 파일이 없습니다.");
        details = new TextBlock { Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 9, 0, 4) };
        feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush"),
            Margin = new Thickness(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(feedback, System.Windows.Automation.AutomationLiveSetting.Polite);

        var top = new DockPanel();
        DockPanel.SetDock(sort, Dock.Right);
        top.Children.Add(sort); top.Children.Add(search);
        var filters = new StackPanel { Children = { top, recovered } };
        var bottom = new StackPanel { Children = { details, feedback } };
        var root = new DockPanel();
        DockPanel.SetDock(filters, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(filters); root.Children.Add(bottom);
        root.Children.Add(new Grid { Children = { list, empty } });
        SetBody(root);

        Button Add(string label, Func<Task> action, bool primary = false)
        {
            var button = AddFooterButton(label, async () => await RunAsync(action), primary);
            actions.Add(button);
            return button;
        }
        var advanced = AddFooterButton("금고 관리", ShowAdvanced);
        actions.Add(advanced);
        preview = Add("미리보기", PreviewAsync);
        verify = Add("파일 검사", VerifySelectedAsync);
        delete = Add("삭제", DeleteAsync);
        export = Add("내보내기", ExportAsync, true);

        search.TextChanged += (_, _) => Refresh();
        sort.SelectionChanged += (_, _) => Refresh();
        recovered.Checked += (_, _) => Refresh();
        recovered.Unchecked += (_, _) => Refresh();
        list.SelectionChanged += (_, _) => UpdateSelection();
        list.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && preview.IsEnabled)
            {
                args.Handled = true;
                _ = RunAsync(PreviewAsync);
            }
        };
        OnContentLoaded(Refresh);
        OnContentVisible(Refresh);
        vault.Changed += Refresh;
        Closed += (_, _) => { closed = true; vault.Changed -= Refresh; };
        Refresh();
    }

    private VaultEntry? Selected => (list.SelectedItem as ListBoxItem)?.Tag as VaultEntry;
    private bool Ready => !closed && sessionReady() && vault.Unlocked;

    private void Refresh()
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        string? selected = Selected?.Id;
        list.Items.Clear();
        var entries = vault.Unlocked ? vault.Entries.ToList() : [];
        var shown = VaultPresentation.Filter(entries, search.Text, (VaultSort)Math.Max(0, sort.SelectedIndex), recovered.IsChecked == true);
        foreach (var entry in shown)
        {
            var item = new ListBoxItem
            {
                Tag = entry,
                Content = Row("", entry.Name, $"{VaultPresentation.Size(entry.Bytes)} · {entry.SavedAt.LocalDateTime:yyyy-MM-dd HH:mm}" +
                    (entry.SourceHost is null ? "" : $" · {entry.SourceHost}"), entry.Recovered ? "복구됨" : "금고 보관"),
            };
            list.Items.Add(item);
            if (entry.Id == selected) list.SelectedItem = item;
        }
        empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = vault.Unlocked ? $"{entries.Count:N0}개 파일 · 표시 {shown.Count:N0}개 · {VaultPresentation.Size(entries.Sum(entry => Math.Max(0, entry.Bytes)))}" : "금고 잠김";
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = Selected;
        bool enabled = Ready && !busy;
        foreach (var button in actions) button.IsEnabled = enabled;
        preview.IsEnabled = enabled && selected is not null && VaultPresentation.CanPreview(selected);
        verify.IsEnabled = delete.IsEnabled = export.IsEnabled = enabled && selected is not null;
        preview.ToolTip = selected is not null && VaultPresentation.CanPreview(selected) ?
            "인증 후 메모리에서 읽기 전용으로 표시합니다." : "복구되지 않은 TXT(256 KiB), PNG/JPEG(8 MiB 이하)" +
            (VaultPdfPreview.Enabled ? ", 개발용 PDF(8 MiB 이하)만 지원합니다." : "만 지원합니다.");
        details.Text = selected is null ? "파일을 선택하세요." :
            $"{selected.Name} · {VaultPresentation.Size(selected.Bytes)}\n" +
            (selected.Recovered ? "복구 항목 · 원래 메타데이터가 없을 수 있습니다." : "보관 상태 · 무결성은 파일 검사에서 확인할 수 있습니다.");
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || !Ready) return;
        busy = true;
        UpdateSelection();
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            NotSupportedException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or
            System.Text.Json.JsonException or OverflowException or FormatException)
        {
            if (!closed) feedback.Text = "작업을 완료하지 못했습니다. 금고 상태, 파일 무결성 및 저장 위치를 확인하세요. 일부 변경이 적용되었을 수 있습니다.";
        }
        finally
        {
            busy = false;
            if (!closed) Refresh();
        }
    }

    private async Task PreviewAsync()
    {
        if (Selected is not { } entry || !VaultPresentation.CanPreview(entry)) return;
        feedback.Text = "파일을 인증하고 있습니다.";
        byte[] bytes = await vault.ReadPreviewAsync(entry);
        try
        {
            if (!Ready) return;
            var kind = VaultPresentation.PreviewKind(entry);
            if (kind == VaultPreviewKind.Text)
            {
                string text = VaultPresentation.DecodeText(bytes);
                if (Ready) new VaultTextPreviewDialog(PromptOwner, entry.Name, text).ShowDialog();
            }
            else if (kind == VaultPreviewKind.Pdf)
            {
                if (Ready && Selected?.Id == entry.Id)
                    new VaultPdfPreviewDialog(PromptOwner, entry.Name, bytes, () => Ready && Selected?.Id == entry.Id).ShowDialog();
            }
            else
            {
                var bitmap = await Task.Run(() => VaultImagePreview.Decode(bytes, kind));
                if (Ready && Selected?.Id == entry.Id)
                    new VaultImagePreviewDialog(PromptOwner, entry.Name, bitmap).ShowDialog();
            }
            if (Ready) feedback.Text = "미리보기를 닫았습니다.";
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task ExportAsync()
    {
        if (Selected is not { } entry) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = VaultPresentation.ExportName(entry), Title = "금고에서 내보내기",
            OverwritePrompt = false, AddToRecent = false,
        };
        if (dialog.ShowDialog(PromptOwner) != true || !Ready) return;
        if (MessageBox.Show(PromptOwner, "선택한 위치에 암호화되지 않은 일반 파일을 만듭니다. 금고 밖에서는 VEIL의 보호가 적용되지 않을 수 있습니다. 계속할까요?",
            "파일 내보내기", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes || !Ready) return;
        if (File.Exists(dialog.FileName) || Directory.Exists(dialog.FileName))
        {
            feedback.Text = "같은 이름의 파일 또는 폴더가 있습니다. 다른 이름을 선택하세요.";
            return;
        }
        feedback.Text = "파일 인증 및 내보내기 중입니다.";
        long bytes = await vault.ExportAsync(entry, dialog.FileName);
        if (!closed) feedback.Text = $"내보내기 완료 · {VaultPresentation.Size(bytes)}";
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } entry) return;
        if (MessageBox.Show(PromptOwner, $"{entry.Name}을(를) 금고에서 삭제할까요? 백업이나 복구 가능한 디스크 흔적은 지우지 않습니다.",
            "금고 항목 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes || !Ready) return;
        await vault.DeleteAsync(entry);
        if (!closed) feedback.Text = "선택한 항목을 금고 목록에서 삭제했습니다.";
    }

    private async Task VerifySelectedAsync()
    {
        if (Selected is not { } entry) return;
        feedback.Text = "파일 인증 중입니다.";
        long size = await vault.VerifyAsync(entry);
        if (!closed) feedback.Text = !entry.Recovered && size != entry.Bytes
            ? "파일은 인증되었지만 저장된 크기와 일치하지 않습니다."
            : $"파일 인증 완료 · {VaultPresentation.Size(size)} (악성코드 안전 판정은 아님)";
    }

    private void ShowAdvanced()
    {
        if (!Ready || busy) return;
        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)) };
        void Item(string label, Func<Task> action)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await RunAsync(action);
            menu.Items.Add(item);
        }
        Item("전체 파일 검사", VerifyAllAsync);
        Item("암호화 백업", BackupAsync);
        if (!vault.UsesProfilePassword) Item("금고 암호 변경", () => RotateAsync(true));
        Item("복구 코드 재발급", () => RotateAsync(false));
        Item("내용 키 교체", RotateContentAsync);
        menu.IsOpen = true;
    }

    private async Task BackupAsync()
    {
        var destination = new Microsoft.Win32.SaveFileDialog
        {
            Title = "금고 백업 저장", FileName = "VEIL-" + DateTime.Today.ToString("yyyy-MM-dd"),
            DefaultExt = ".veil-backup", Filter = "VEIL 금고 백업 (*.veil-backup)|*.veil-backup",
            OverwritePrompt = false, AddToRecent = false,
        };
        if (destination.ShowDialog(PromptOwner) != true || !Ready) return;
        var setup = new VaultSetupDialog(PromptOwner, "금고 백업 암호", askPassphrase: true, issueCode: false,
            passwordPrompt: "백업 전용 암호를 정하세요. 복원할 때 이 암호가 필요합니다.");
        if (setup.ShowDialog() != true || !Ready) return;
        feedback.Text = "암호화 백업을 만드는 중입니다.";
        long count = await vault.BackupAsync(destination.FileName, setup.Passphrase!);
        if (!closed) feedback.Text = $"금고 항목 {count:N0}개를 백업했습니다.";
    }

    private async Task VerifyAllAsync()
    {
        var entries = vault.Entries.ToList();
        int ok = 0, failed = 0;
        for (int i = 0; i < entries.Count && Ready; i++)
        {
            feedback.Text = $"파일 검사 중 · {i + 1}/{entries.Count}";
            try
            {
                long size = await vault.VerifyAsync(entries[i]);
                if (!entries[i].Recovered && size != entries[i].Bytes) failed++;
                else ok++;
            }
            catch (IOException) { failed++; }
        }
        if (!closed) feedback.Text = $"인증 성공 {ok}개 · 실패 또는 확인 불가 {failed}개 · 미검사 {entries.Count - ok - failed}개";
    }

    private async Task RotateAsync(bool askPassphrase)
    {
        var dialog = new VaultSetupDialog(PromptOwner, askPassphrase ? "금고 암호 변경" : "복구 코드 재발급", askPassphrase, issueCode: !askPassphrase);
        if (dialog.ShowDialog() != true || !Ready) return;
        bool ok = askPassphrase ? await vault.ChangePassphraseAsync(dialog.Passphrase!) : await vault.ReissueRecoveryCodeAsync(dialog.RecoveryCode!);
        if (!closed) feedback.Text = ok ? (askPassphrase ? "금고 암호를 바꿨습니다." : "새 복구 코드로 바꿨습니다. 이전 코드는 더 이상 쓸 수 없습니다.") : "변경하지 못했습니다.";
    }

    private async Task RotateContentAsync()
    {
        if (MessageBox.Show(PromptOwner, "금고 파일을 새 키로 다시 암호화합니다. 금고 크기만큼의 추가 공간이 필요합니다. 계속할까요?",
            "금고 내용 키 교체", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes || !Ready) return;
        var setup = new VaultSetupDialog(PromptOwner, "새 금고 복구 코드", askPassphrase: false, issueCode: true);
        if (setup.ShowDialog() != true || !Ready) return;
        char[]? password = ProfileUnlockDialog.Prompt("금고 내용 키 교체", vault.UsesProfilePassword
            ? "현재 보호 프로필 암호를 입력하세요." : "현재 금고 암호를 입력하세요.", PromptOwner);
        if (password is null) return;
        try
        {
            if (!Ready) return;
            feedback.Text = "금고 내용을 새 키로 암호화하고 있습니다.";
            var result = await vault.RotateContentKeysAsync(password, setup.RecoveryCode!);
            if (!closed) feedback.Text = result switch
            {
                true => "내용 키와 복구 코드를 교체했습니다.",
                false => "키는 교체됐지만 이전 암호화 파일을 모두 정리하지 못했습니다.",
                null => "키를 교체하지 못했습니다. 금고 암호, 무결성과 남은 공간을 확인하세요.",
            };
        }
        finally { ProtectedProfileSession.ClearPassword(password); }
    }
}

internal sealed class VaultTextPreviewDialog : VeilDialog
{
    internal VaultTextPreviewDialog(Window owner, string name, string text)
        : base(owner, "텍스트 미리보기", "", 760, 580)
    {
        Subtitle.Text = "메모리에서 읽기 전용 표시 · 파일 실행 및 링크 열기 없음";
        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
        var content = new TextBlock { Text = text, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
        var root = new DockPanel();
        DockPanel.SetDock(label, Dock.Top);
        root.Children.Add(label);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        SetBody(root);
        AddFooterButton("닫기", Dismiss, primary: true);
        Closed += (_, _) => content.Text = "";
    }
}


internal sealed class VaultImagePreviewDialog : VeilDialog
{
    internal VaultImagePreviewDialog(Window owner, string name, System.Windows.Media.Imaging.BitmapSource bitmap)
        : base(owner, "이미지 미리보기", "", 860, 650)
    {
        Subtitle.Text = $"읽기 전용 · {bitmap.PixelWidth:N0} × {bitmap.PixelHeight:N0} · 외부 파일 열기 없음";
        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8) };
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        var root = new DockPanel();
        DockPanel.SetDock(label, Dock.Top);
        root.Children.Add(label);
        root.Children.Add(new Border { Background = Res("SurfaceBrush"), Padding = new Thickness(8), Child = image });
        SetBody(root);
        AddFooterButton("닫기", Dismiss, primary: true);
        Closed += (_, _) => image.Source = null;
    }
}
