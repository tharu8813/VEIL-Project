using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Veil.Desktop;

/// <summary>Bookmarks (editable) and history, searchable, in the shared dark dialog shell.</summary>
internal sealed class LibraryWindow : VeilDialog
{
    internal string? SelectedUrl { get; private set; }
    private Action? refreshEntries;
    internal void RefreshEntries() => refreshEntries?.Invoke();

    private sealed record FolderChoice(Guid? Id, string Label)
    {
        public override string ToString() => Label;
    }

    internal LibraryWindow(Window owner, string title, List<PageEntry> entries, bool editable, Func<bool> save,
        List<BookmarkFolder>? folders = null, Func<bool>? canEdit = null, Action? published = null)
        : base(owner, title, editable ? "" : "", 760, 540)
    {
        string rowGlyph = editable ? "" : "";
        var search = new TextBox
        {
            Style = (Style)FindResource("PillTextBox"),
            Height = 36,
            Tag = "",
            ToolTip = $"{title} 검색",
            Margin = new Thickness(0, 0, 0, 12),
        };
        System.Windows.Automation.AutomationProperties.SetName(search, $"{title} 검색");
        var list = new ListBox { Style = (Style)FindResource("VeilList") };
        System.Windows.Automation.AutomationProperties.SetName(list, title);
        var empty = EmptyState(editable ? "" : "", "");
        var panel = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        panel.Children.Add(search);

        // Folder controls are only present for bookmarks, not the history library.
        var folderView = new ComboBox { MinWidth = 170, MaxWidth = 320, Height = 32 };
        var destination = new ComboBox { MinWidth = 170, MaxWidth = 320, Height = 32 };
        var folderName = new TextBox { Width = 145, Height = 32, Style = (Style)FindResource("PillTextBox"),
            ToolTip = "폴더 이름" };
        System.Windows.Automation.AutomationProperties.SetName(folderView, "현재 폴더");
        System.Windows.Automation.AutomationProperties.SetName(destination, "이동할 폴더");
        System.Windows.Automation.AutomationProperties.SetName(folderName, "폴더 이름");
        Guid? currentFolder = null;
        bool refreshing = false;

        // History scope is independent of the text query: bulk deletion always names its scope.
        var range = new ComboBox { Width = 130, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (string label in new[] { "전체 기간", "최근 1시간", "최근 24시간", "최근 7일", "최근 30일" })
            range.Items.Add(label);
        range.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(range, "방문 기록 기간");
        DateTimeOffset? Cutoff() => range.SelectedIndex switch
        {
            1 => DateTimeOffset.Now.AddHours(-1),
            2 => DateTimeOffset.Now.AddDays(-1),
            3 => DateTimeOffset.Now.AddDays(-7),
            4 => DateTimeOffset.Now.AddDays(-30),
            _ => null,
        };
        bool InRange(PageEntry entry, DateTimeOffset? cutoff) => cutoff is null || entry.Visited >= cutoff.Value;
        if (!editable)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var clear = new Button
            {
                Content = "선택 기간 삭제",
                Style = (Style)FindResource("GhostButton"),
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(8, 0, 0, 0),
            };
            clear.Click += (_, _) => ClearRange();
            bar.Children.Add(range);
            bar.Children.Add(clear);
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);
        }
        if (folders is not null)
        {
            var controls = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var first = new WrapPanel();
            var second = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            void AddControl(Panel row, FrameworkElement element)
            {
                element.Margin = new Thickness(0, 0, 5, 4);
                row.Children.Add(element);
            }
            void ActionButton(Panel row, string text, Action action)
            {
                var button = new Button { Content = text, Height = 32, Padding = new Thickness(8, 0, 8, 0),
                    Style = (Style)FindResource("GhostButton") };
                button.Click += (_, _) => action();
                AddControl(row, button);
            }
            AddControl(first, folderView);
            AddControl(first, folderName);
            ActionButton(first, "폴더 추가", CreateFolder);
            ActionButton(first, "이름 변경", RenameFolder);
            ActionButton(first, "폴더 삭제", DeleteFolder);
            ActionButton(first, "폴더 ↑", () => ShiftFolder(-1));
            ActionButton(first, "폴더 ↓", () => ShiftFolder(1));
            AddControl(second, destination);
            ActionButton(second, "북마크 이동", MoveBookmark);
            ActionButton(second, "폴더 이동", MoveCurrentFolder);
            ActionButton(second, "북마크 ↑", () => ShiftBookmark(-1));
            ActionButton(second, "북마크 ↓", () => ShiftBookmark(1));
            controls.Children.Add(first);
            controls.Children.Add(second);
            var interchange = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            ActionButton(interchange, "HTML 가져오기", ImportBookmarks);
            ActionButton(interchange, "HTML 내보내기", ExportBookmarks);
            controls.Children.Add(interchange);
            DockPanel.SetDock(controls, Dock.Top);
            panel.Children.Add(controls);
            folderView.SelectionChanged += (_, _) =>
            {
                if (refreshing) return;
                currentFolder = (folderView.SelectedItem as FolderChoice)?.Id;
                Refresh();
            };
        }
        var listArea = new Grid { Children = { list, empty } };
        panel.Children.Add(listArea);
        SetBody(panel);

        PageEntry? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as PageEntry;

        void Refresh()
        {
            string filter = search.Text.Trim();
            var cutoff = editable ? null : Cutoff();
            if (folders is not null)
            {
                if (currentFolder is Guid id && !folders.Any(f => f.Id == id)) currentFolder = null;
                var target = (destination.SelectedItem as FolderChoice)?.Id;
                var choices = new[] { new FolderChoice(null, "최상위") }.Concat(
                    folders.Select(f => new FolderChoice(f.Id, BookmarkPolicy.Path(folders, f.Id)))).ToArray();
                refreshing = true;
                try
                {
                    folderView.ItemsSource = choices;
                    folderView.SelectedItem = choices.First(c => c.Id == currentFolder);
                    destination.ItemsSource = choices;
                    destination.SelectedItem = choices.FirstOrDefault(c => c.Id == target) ?? choices[0];
                }
                finally { refreshing = false; }
            }
            var matches = entries.Where(entry => (folders is null || filter.Length > 0 || entry.FolderId == currentFolder) &&
                InRange(entry, cutoff) &&
                (entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                 entry.Url.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
            var selected = Selected();
            list.Items.Clear();
            foreach (var entry in matches)
            {
                string detail = folders is not null && filter.Length > 0
                    ? entry.Url + " · " + BookmarkPolicy.Path(folders, entry.FolderId) : entry.Url;
                var row = new ListBoxItem { Tag = entry, Content = Row(rowGlyph, entry.Title, detail, When(entry.Visited)) };
                System.Windows.Automation.AutomationProperties.SetName(row, $"{entry.Title} {entry.Url}");
                list.Items.Add(row);
                if (selected is not null && entry == selected) list.SelectedItem = row;
            }
            Subtitle.Text = filter.Length == 0 && cutoff is null ? $"{entries.Count}개 항목" : $"{matches.Length}개 일치 · 전체 {entries.Count}개";
            ((TextBlock)((StackPanel)empty.Child).Children[1]).Text = entries.Count == 0
                ? (editable ? "저장한 북마크가 없습니다.\nCtrl+D로 현재 페이지를 추가하세요." : "방문 기록이 없습니다.")
                : "선택한 기간 또는 검색어와 일치하는 항목이 없습니다.";
            empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (matches.Length == 0) Motion.Enter(empty, 0, dy: 10, scale: 0.96, ms: 500);
            Cascade(list);
        }

        void Open()
        {
            if (canEdit?.Invoke() != true || Selected() is not { } entry || !entries.Contains(entry)) return;
            SelectedUrl = entry.Url;
            Complete(true);
        }

        bool Commit(Action change)
        {
            if (canEdit?.Invoke() != true) return false;
            // The list is shared by all windows. Do not report success if the protected state
            // could not be saved; restore the in-memory list before showing the error.
            var previous = entries.ToArray();
            var previousFolders = folders?.ToArray();
            bool committed;
            try
            {
                change();
                committed = (folders is null || BookmarkPolicy.Valid(folders, entries)) && save();
            }
            catch (Exception error) when (RecoverableFailure.Is(error))
            {
                committed = false;
            }
            if (!committed)
            {
                entries.Clear();
                entries.AddRange(previous);
                if (folders is not null && previousFolders is not null)
                {
                    folders.Clear();
                    folders.AddRange(previousFolders);
                }
                Refresh();
                UserPrompts.Notice(PromptOwner, "변경사항을 저장하지 못했습니다. 기존 목록을 유지합니다.", title);
                return false;
            }
            // The durable save has succeeded. A rendering failure must not roll it back in memory.
            published?.Invoke();
            Refresh();
            return true;
        }

        void ImportBookmarks()
        {
            if (folders is null || canEdit?.Invoke() != true) return;
            var picker = new OpenFileDialog { Filter = "북마크 HTML (*.html;*.htm)|*.html;*.htm", CheckFileExists = true };
            string? path = UserPrompts.PickFile(PromptOwner, picker);
            if (path is null || canEdit?.Invoke() != true) return;
            try
            {
                var imported = BookmarkHtml.Read(path, entries);
                if (imported.Entries.Count == 0 && imported.Folders.Count == 0)
                {
                    UserPrompts.Notice(PromptOwner, "가져올 새 북마크가 없습니다.", title, MessageBoxImage.None);
                    return;
                }
                if (Commit(() => { folders.AddRange(imported.Folders); entries.AddRange(imported.Entries); }))
                    UserPrompts.Notice(PromptOwner, $"북마크 {imported.Entries.Count}개, 폴더 {imported.Folders.Count}개 가져옴. 건너뜀 {imported.Skipped}개.", title, MessageBoxImage.None);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or RegexMatchTimeoutException)
            {
                UserPrompts.Notice(PromptOwner, "북마크 HTML을 읽지 못했습니다. UTF-8 북마크 파일과 크기를 확인하세요.", title);
            }
        }

        void ExportBookmarks()
        {
            if (folders is null || canEdit?.Invoke() != true) return;
            if (!UserPrompts.Confirm(PromptOwner, "내보낸 HTML에는 북마크 주소와 이름이 평문으로 저장됩니다. 계속할까요?",
                "북마크 내보내기") ||
                canEdit?.Invoke() != true) return;
            var picker = new SaveFileDialog { Filter = "북마크 HTML (*.html)|*.html", DefaultExt = ".html",
                FileName = "veil-bookmarks.html", AddExtension = true, OverwritePrompt = false };
            string? path = UserPrompts.PickFile(PromptOwner, picker);
            if (path is null || canEdit?.Invoke() != true) return;
            try
            {
                BookmarkHtml.WriteNew(path, BookmarkHtml.Export(folders, entries));
                UserPrompts.Notice(PromptOwner, "북마크를 내보냈습니다.", title, MessageBoxImage.None);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                UserPrompts.Notice(PromptOwner, "내보내지 못했습니다. 기존 파일을 덮어쓰지 않습니다. 다른 이름과 저장 위치를 확인하세요.", title);
            }
        }

        BookmarkFolder? Current() => folders?.FirstOrDefault(f => f.Id == currentFolder);
        void CreateFolder()
        {
            string name = folderName.Text.Trim();
            if (folders is null || !BookmarkPolicy.ValidName(name)) return;
            var folder = new BookmarkFolder(Guid.NewGuid(), name, currentFolder);
            Commit(() => folders.Add(folder));
            if (folders.Contains(folder)) { currentFolder = folder.Id; folderName.Clear(); Refresh(); }
        }

        void RenameFolder()
        {
            string name = folderName.Text.Trim();
            if (folders is null || Current() is not { } folder || !BookmarkPolicy.ValidName(name)) return;
            Commit(() => folders[folders.IndexOf(folder)] = folder with { Name = name });
        }

        void DeleteFolder()
        {
            if (folders is null || Current() is not { } folder) return;
            if (!UserPrompts.Confirm(PromptOwner, $"'{folder.Name}' 폴더를 삭제할까요? 북마크와 하위 폴더는 상위 폴더로 이동합니다.",
                "폴더 삭제", MessageBoxImage.Question)) return;
            if (!folders.Contains(folder)) return;
            Commit(() => BookmarkPolicy.RemoveFolder(folders, entries, folder.Id));
            if (!folders.Contains(folder)) { currentFolder = folder.ParentId; Refresh(); }
        }

        void MoveBookmark()
        {
            if (folders is null || Selected() is not { } entry || !entries.Contains(entry) ||
                destination.SelectedItem is not FolderChoice target || entry.FolderId == target.Id) return;
            Commit(() => entries[entries.IndexOf(entry)] = entry with { FolderId = target.Id });
        }

        void MoveCurrentFolder()
        {
            if (folders is null || Current() is not { } folder || destination.SelectedItem is not FolderChoice target ||
                folder.ParentId == target.Id) return;
            bool moved = false;
            Commit(() => moved = BookmarkPolicy.MoveFolder(folders, entries, folder.Id, target.Id));
            if (!moved) UserPrompts.Notice(PromptOwner, "자기 자신 또는 하위 폴더로 이동할 수 없으며 최대 깊이는 16단계입니다.", title, MessageBoxImage.None);
        }

        void ShiftBookmark(int direction)
        {
            if (Selected() is not { } entry || !entries.Contains(entry)) return;
            Commit(() => BookmarkPolicy.Shift(entries, entry, e => e.FolderId == entry.FolderId, direction));
        }

        void ShiftFolder(int direction)
        {
            if (folders is null || Current() is not { } folder) return;
            Commit(() => BookmarkPolicy.Shift(folders, folder, f => f.ParentId == folder.ParentId, direction));
        }

        void Remove()
        {
            if (Selected() is not { } entry || !entries.Contains(entry)) return;
            Commit(() => entries.Remove(entry));
        }

        void ClearRange()
        {
            if (editable || canEdit?.Invoke() != true) return;
            var cutoff = Cutoff();
            var targets = entries.Where(entry => InRange(entry, cutoff)).ToArray();
            if (targets.Length == 0) return;
            string scope = range.SelectedItem as string ?? "전체 기간";
            if (!UserPrompts.Confirm(PromptOwner,
                $"{scope}의 VEIL 방문 기록 {targets.Length}개를 삭제할까요?\n검색어와 관계없이 이 기간의 항목을 삭제합니다. 쿠키, 사이트 데이터 및 엔진 기록은 지우지 않습니다.",
                "방문 기록 삭제"))
                return;
            // Delete only the exact visits the user confirmed, not equal newly added records.
            var confirmed = new HashSet<PageEntry>(targets, ReferenceEqualityComparer.Instance);
            Commit(() => entries.RemoveAll(confirmed.Contains));
        }

        AddFooterButton("선택 항목 삭제", Remove);
        if (editable)
        {
            var name = new TextBox
            {
                Style = (Style)FindResource("PillTextBox"),
                Width = 190,
                Height = 32,
                Tag = "",
                ToolTip = "북마크 이름",
            };
            System.Windows.Automation.AutomationProperties.SetName(name, "북마크 이름");
            AddFooterElement(name);
            list.SelectionChanged += (_, _) => name.Text = Selected()?.Title ?? "";
            AddFooterButton("이름 저장", () =>
            {
                if (Selected() is not { } entry || string.IsNullOrWhiteSpace(name.Text)) return;
                int index = entries.IndexOf(entry);
                if (index < 0) return;
                string newTitle = name.Text.Trim();
                Commit(() => entries[index] = entry with { Title = newTitle });
            });
        }
        AddFooterButton("닫기", Dismiss);
        AddFooterButton("열기", Open, primary: true);

        search.TextChanged += (_, _) => Refresh();
        range.SelectionChanged += (_, _) => Refresh();
        list.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element &&
                ItemsControl.ContainerFromElement(list, element) is ListBoxItem) Open();
        };
        list.KeyDown += (_, args) => { if (args.Key == Key.Enter) Open(); };
        OnContentLoaded(() => { Refresh(); search.Focus(); });
        OnContentVisible(Refresh);
        refreshEntries = Refresh;
        Refresh();
    }

    private static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("M월 d일");
    }
}

/// <summary>Cancelled download requests, explained until the vault exists.</summary>
internal sealed class DownloadsWindow : VeilDialog
{
    internal DownloadsWindow(Window owner, IReadOnlyList<string> downloads)
        : base(owner, "다운로드", "", 540, 420)
    {
        Subtitle.Text = $"취소한 요청 {downloads.Count}건";
        var notice = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 12),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x14, 0xF2, 0xB8, 0x4B)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xF2, 0xB8, 0x4B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "금고 검증 전까지 파일 다운로드를 취소합니다.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xD0, 0x8A)),
                FontSize = 12,
            },
        };
        var list = new ListBox { Style = (Style)FindResource("VeilList") };
        System.Windows.Automation.AutomationProperties.SetName(list, "다운로드");
        foreach (string entry in downloads)
        {
            string[] parts = entry.Split(" · ", 2);
            list.Items.Add(new ListBoxItem
            {
                Content = Row("", parts[0], "다운로드 요청 차단", parts.Length > 1 ? parts[1] : null, Res("AmberBrush")),
            });
        }
        var panel = new DockPanel();
        DockPanel.SetDock(notice, Dock.Top);
        panel.Children.Add(notice);
        var area = new Grid { Children = { list } };
        if (downloads.Count == 0) area.Children.Add(EmptyState("", "취소한 다운로드가 없습니다."));
        panel.Children.Add(area);
        SetBody(panel);
        AddFooterButton("닫기", Dismiss, primary: true);
        Loaded += (_, _) =>
        {
            Motion.Wipe(notice, 220, 700);
            Cascade(list);
        };
    }
}
