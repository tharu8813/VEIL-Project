using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private readonly DispatcherTimer downloadRingTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private double ringAngle;
    private string[] popupRows = [];
    private sealed record RetryDownload(BrowserTab Tab, Uri Address, string Name, long Epoch, string Source, DateTimeOffset Expires);
    private RetryDownload? retryDownload;

    private bool CanRetry(RetryDownload item)
    {
        if (!SettingsReady() || !CanUseTab(item.Tab.Item) || item.Expires <= DateTimeOffset.UtcNow ||
            item.Tab.NavigationEpoch != item.Epoch || !DownloadFailurePolicy.SafeRetryAddress(item.Address)) return false;
        try
        {
            return item.Tab.View.CoreWebView2 is { } web && web.Source == item.Source &&
                session.Vault.Unlocked && session.Vault.Unsupported(item.Address) is null;
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { return false; }
    }

    private void RetryDownloadClick(object sender, RoutedEventArgs args)
    {
        if (retryDownload is not { } item || !CanRetry(item)) return;
        retryDownload = null; // single explicit retry; no duplicate clicks or repeated retry chains
        RetryDownloadButton.Visibility = Visibility.Collapsed;
        DownloadPopup.IsOpen = false;
        if (MessageBox.Show(this, $"{item.Name}을(를) 원래 서버에 다시 요청합니다. 이전 전송을 이어받지 않습니다. 계속할까요?",
            "다운로드 다시 시도", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes ||
            !CanRetry(item)) return;
        _ = CaptureAsync(item.Tab.View.CoreWebView2, item.Address, item.Name, 0, 0, item, retry: true);
    }

    // Weight known transfers by bytes, not by the number of files. An unknown or queued size
    // cannot be represented by an honest percentage. A full body is not yet an authenticated file.
    internal static (bool Indeterminate, double Fraction) AggregateDownloadProgress(IEnumerable<BrowserDownload> downloads)
    {
        var items = downloads.ToArray();
        if (items.Length == 0) return (false, 0);
        if (items.Any(item => item.TotalBytes is not > 0)) return (true, 0);
        double total = items.Sum(item => (double)item.TotalBytes!.Value);
        double bytes = items.Sum(item => (double)Math.Clamp(item.Bytes, 0, item.TotalBytes!.Value));
        return (false, Math.Clamp(bytes / total, 0, 1));
    }

    private void InitializeDownloadToolbar()
    {
        downloadRingTimer.Tick += (_, _) => UpdateDownloadToolbar();
    }

    private void UpdateDownloadToolbar()
    {
        if (closing || session.IsEnding || session.EngineLost)
        {
            downloadRingTimer.Stop();
            return;
        }
        if (retryDownload is { } retry && !CanRetry(retry)) retryDownload = null;
        RetryDownloadButton.Visibility = retryDownload is null ? Visibility.Collapsed : Visibility.Visible;
        RetryDownloadButton.ToolTip = retryDownload?.Name;
        bool active = session.ActiveDownloads.Count != 0;
        DownloadRingTrack.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        DownloadRingArc.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            var progress = AggregateDownloadProgress(session.ActiveDownloads);
            bool indeterminate = progress.Indeterminate || progress.Fraction >= 1;
            ringAngle = indeterminate && Motion.Enabled ? (ringAngle + 30) % 360 : 0;
            DownloadRingArc.Data = MakeDownloadArc(ringAngle, indeterminate ? 0.24 : Math.Max(0.015, progress.Fraction));
            string label = progress.Indeterminate ? "다운로드 진행 중" :
                progress.Fraction >= 1 ? "금고 저장 마무리 중" : $"다운로드 {progress.Fraction:P0}";
            DownloadButton.ToolTip = label + $" · {session.ActiveDownloads.Count}건";
            System.Windows.Automation.AutomationProperties.SetName(DownloadButton, label);
        }
        else
        {
            DownloadRingArc.Data = null;
            ringAngle = 0;
            DownloadButton.ToolTip = "최근 다운로드";
            System.Windows.Automation.AutomationProperties.SetName(DownloadButton, "최근 다운로드");
        }
        if (DownloadPopup.IsOpen) RefreshRecentDownloads();
        if (active || DownloadPopup.IsOpen || retryDownload is not null)
        {
            if (!downloadRingTimer.IsEnabled) downloadRingTimer.Start();
        }
        else downloadRingTimer.Stop();
    }

    private static Geometry MakeDownloadArc(double start, double fraction)
    {
        const double radius = 12;
        Point At(double degrees)
        {
            double radians = (degrees - 90) * Math.PI / 180;
            return new Point(14 + radius * Math.Cos(radians), 14 + radius * Math.Sin(radians));
        }
        double sweep = Math.Clamp(fraction, 0, 0.999) * 360;
        var figure = new PathFigure { StartPoint = At(start), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(start + sweep), new Size(radius, radius), 0,
            sweep > 180, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { figure });
    }

    private void DownloadToolbarClick(object sender, RoutedEventArgs args)
    {
        if (!SettingsReady() || fullScreen) return;
        CloseAddressSuggestions();
        DownloadPopup.IsOpen = !DownloadPopup.IsOpen;
    }

    private void DownloadPopupOpened(object? sender, EventArgs args) => UpdateDownloadToolbar();

    private void DownloadPopupClosed(object? sender, EventArgs args)
    {
        popupRows = [];
        RecentDownloadsList.ItemsSource = null;
        if (!closing) UpdateDownloadToolbar();
    }

    private void CloseDownloadPopupClick(object sender, RoutedEventArgs args) => DownloadPopup.IsOpen = false;

    private void DownloadPopupKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        DownloadPopup.IsOpen = false;
        DownloadButton.Focus();
    }

    private void PopupVaultClick(object sender, RoutedEventArgs args)
    {
        DownloadPopup.IsOpen = false;
        VaultClick(this, args);
    }

    private void RefreshRecentDownloads()
    {
        bool visible = session.VaultOpenGate.CurrentCount > 0 && session.Vault.Unlocked;
        var stored = visible ? session.Vault.Entries.Select(entry =>
            (At: entry.SavedAt, Text: $"{entry.Name} · 금고 보관 · {VaultPresentation.Size(entry.Bytes)}"))
            : Enumerable.Empty<(DateTimeOffset At, string Text)>();
        var rows = stored.Concat(session.RecentDownloadIssues.Select(issue =>
            (At: issue.At, Text: $"{issue.Name} · {issue.Outcome}" + (issue.Detail is null ? "" : $": {issue.Detail}"))))
            .OrderByDescending(item => item.At)
            .Take(8).Select(item => item.Text).ToArray();
        if (!popupRows.SequenceEqual(rows))
        {
            popupRows = rows;
            RecentDownloadsList.ItemsSource = rows;
        }
        DownloadPopupEmpty.Visibility = rows.Length == 0 && session.ActiveDownloads.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        DownloadPopupLocked.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshBookmarkBars(LibraryWindow? source = null, bool refreshLibraries = true)
    {
        foreach (var window in session.Windows.ToArray())
        {
            if (window.closing) continue;
            window.UpdateBookmarkGlyph();
            window.RefreshBookmarkBar();
            if (refreshLibraries && window.bookmarkLibrary is { } library && !ReferenceEquals(library, source))
                library.RefreshEntries();
        }
    }

    private sealed record BookmarkBarFolder(Guid Id, string Title)
    {
        public string Url => "북마크 폴더";
    }

    private void RefreshBookmarkBar()
    {
        BookmarkBarMenuItem.IsChecked = data.ShowBookmarksBar;
        BookmarkBar.Visibility = data.ShowBookmarksBar && !fullScreen ? Visibility.Visible : Visibility.Collapsed;
        // Local metadata only: no remote favicon or speculative requests.
        var items = data.BookmarkFolders.Where(folder => folder.ParentId is null)
            .Select(folder => (object)new BookmarkBarFolder(folder.Id, folder.Name))
            .Concat(data.Bookmarks.Where(entry => entry.FolderId is null && IsWebUrl(entry.Url))).ToArray();
        BookmarkBarItems.ItemsSource = items;
        BookmarkBarEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleBookmarkBarClick(object sender, RoutedEventArgs args)
    {
        if (!SettingsReady()) { BookmarkBarMenuItem.IsChecked = data.ShowBookmarksBar; return; }
        bool previous = data.ShowBookmarksBar;
        data.ShowBookmarksBar = !previous;
        try { data.Save(statePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            data.ShowBookmarksBar = previous;
            Status.Text = "북마크 바 설정을 저장하지 못했습니다.";
        }
        RefreshBookmarkBars(refreshLibraries: false);
    }

    private void BookmarkBarItemClick(object sender, RoutedEventArgs args)
    {
        if (!SettingsReady() || sender is not Button button) return;
        if (button.DataContext is BookmarkBarFolder folder && data.BookmarkFolders.Any(f => f.Id == folder.Id))
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            FillFolderMenu(menu.Items, folder.Id);
            menu.IsOpen = true;
            return;
        }
        if (button.DataContext is PageEntry entry) OpenBookmark(entry);
    }

    private void OpenBookmark(PageEntry entry)
    {
        if (!SettingsReady() || !data.Bookmarks.Contains(entry) || !IsWebUrl(entry.Url)) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _ = OpenTab(entry.Url);
        else NavigateAddress(entry.Url);
    }

    private void FillFolderMenu(ItemCollection items, Guid folderId)
    {
        foreach (var folder in data.BookmarkFolders.Where(f => f.ParentId == folderId))
        {
            var item = new MenuItem { Header = folder.Name };
            FillFolderMenu(item.Items, folder.Id);
            items.Add(item);
        }
        foreach (var entry in data.Bookmarks.Where(e => e.FolderId == folderId && IsWebUrl(e.Url)))
        {
            var item = new MenuItem { Header = entry.Title, ToolTip = entry.Url };
            item.Click += (_, _) => OpenBookmark(entry);
            items.Add(item);
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = "빈 폴더", IsEnabled = false });
    }
}
