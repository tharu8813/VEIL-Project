using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Veil.Desktop;

/// <summary>Native, paged overview; no file metadata is passed into WebView.</summary>
internal sealed class DownloadOverview : Border
{
    private const int PageSize = 100;
    private readonly BrowserSession session;
    private readonly Func<bool> ready;
    private readonly TextBox search;
    private readonly ComboBox filter, sort;
    private readonly ListBox list;
    private readonly TextBlock summary;
    private readonly Button previous, next, clear;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int page;
    private bool? lastVisible;
    private int lastCount = -1, lastPage = -1;
    private DownloadOverviewRow[] lastRows = [];
    private List<VaultEntry>? indexedEntries;
    private DownloadIssue[] indexedIssues = [];
    private IReadOnlyList<DownloadOverviewRow>? matchedRows;

    internal DownloadOverview(Window owner, BrowserSession session, Func<bool> ready, Action clearIssues)
    {
        this.session = session;
        this.ready = ready;
        Background = (Brush)owner.FindResource("InkBrush");
        var root = new DockPanel { Margin = new Thickness(24, 18, 24, 18), MaxWidth = 1080 };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "다운로드", FontSize = 24, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)owner.FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 12) });
        search = new TextBox { Style = (Style)owner.FindResource("PillTextBox"), Height = 34, Tag = "",
            MinWidth = 120, Margin = new Thickness(0, 0, 8, 0), ToolTip = "파일 이름, 상태, 출처 검색" };
        filter = new ComboBox { Width = 112, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        foreach (string item in new[] { "전체", "보관됨", "실패", "차단", "취소", "복구됨" }) filter.Items.Add(item);
        filter.SelectedIndex = 0;
        sort = new ComboBox { Width = 112, Height = 34 };
        foreach (string item in new[] { "최신순", "오래된 순", "이름순", "큰 파일순", "작은 파일순" }) sort.Items.Add(item);
        sort.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(search, "다운로드 검색");
        System.Windows.Automation.AutomationProperties.SetName(filter, "다운로드 상태 필터");
        System.Windows.Automation.AutomationProperties.SetName(sort, "다운로드 정렬");
        var controls = new DockPanel();
        DockPanel.SetDock(sort, Dock.Right); controls.Children.Add(sort);
        DockPanel.SetDock(filter, Dock.Right); controls.Children.Add(filter);
        controls.Children.Add(search);
        top.Children.Add(controls);
        summary = new TextBlock { Foreground = (Brush)owner.FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10) };
        top.Children.Add(summary);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button ActionButton(string text, Action action)
        {
            var button = new Button { Content = text, Height = 32, Padding = new Thickness(10, 0, 10, 0),
                Style = (Style)owner.FindResource("GhostButton"), Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) => { if (ready()) action(); };
            return button;
        }
        clear = ActionButton("실패·취소·차단 기록 지우기", () => { clearIssues(); page = 0; Refresh(); });
        DockPanel.SetDock(clear, Dock.Left); footer.Children.Add(clear);
        next = ActionButton("다음", () => { page++; Refresh(); });
        previous = ActionButton("이전", () => { page = Math.Max(0, page - 1); Refresh(); });
        DockPanel.SetDock(next, Dock.Right); footer.Children.Add(next);
        DockPanel.SetDock(previous, Dock.Right); footer.Children.Add(previous);
        footer.LastChildFill = false;
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        list = new ListBox { Style = (Style)owner.FindResource("VeilList") };
        System.Windows.Automation.AutomationProperties.SetName(list, "다운로드 기록");
        root.Children.Add(list);
        Child = root;
        search.TextChanged += (_, _) => Reset();
        filter.SelectionChanged += (_, _) => Reset();
        sort.SelectionChanged += (_, _) => Reset();
        Loaded += (_, _) => { if (IsVisible) { Refresh(); timer.Start(); } };
        Unloaded += (_, _) => { timer.Stop(); ClearRows(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && IsLoaded) { Refresh(); timer.Start(); }
            else { timer.Stop(); ClearRows(); }
        };
        timer.Tick += (_, _) => Refresh();
    }

    private void Reset() { page = 0; matchedRows = null; Refresh(); }

    private void ClearRows()
    {
        list.Items.Clear();
        lastRows = [];
        indexedEntries = null;
        indexedIssues = [];
        matchedRows = null;
        lastCount = lastPage = -1;
        lastVisible = null;
        summary.Text = "";
        clear.IsEnabled = previous.IsEnabled = next.IsEnabled = false;
    }

    internal void Refresh()
    {
        if (!IsVisible || !ready() || session.IsEnding || session.EngineLost)
        {
            if (lastVisible is not null) ClearRows();
            return;
        }
        bool visible = session.VaultOpenGate.CurrentCount > 0 && session.Vault.Unlocked;
        // Vault entry lists are replaced on load/capture/delete/lock, not mutated in place.
        // Avoid repeatedly sorting the complete index during the visibility guard tick.
        var entries = visible ? session.Vault.Entries : null;
        if (matchedRows is null || !ReferenceEquals(indexedEntries, entries) ||
            !indexedIssues.SequenceEqual(session.RecentDownloadIssues))
        {
            indexedEntries = entries;
            indexedIssues = session.RecentDownloadIssues.ToArray();
            matchedRows = DownloadOverviewPolicy.Filter(entries ?? [], indexedIssues, search.Text,
                (DownloadFilter)Math.Max(0, filter.SelectedIndex), (VaultSort)Math.Max(0, sort.SelectedIndex));
        }
        var rows = matchedRows;
        page = Math.Clamp(page, 0, Math.Max(0, (rows.Count - 1) / PageSize));
        var shown = rows.Skip(page * PageSize).Take(PageSize).ToArray();
        clear.IsEnabled = session.RecentDownloadIssues.Count > 0;
        previous.IsEnabled = page > 0;
        next.IsEnabled = (page + 1) * PageSize < rows.Count;
        if (lastVisible == visible && lastCount == rows.Count && lastPage == page && lastRows.SequenceEqual(shown)) return;
        string? selected = (list.SelectedItem as ListBoxItem)?.Tag as string;
        list.Items.Clear();
        foreach (var row in shown)
        {
            var lines = new StackPanel { Margin = new Thickness(6, 5, 6, 5) };
            lines.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis });
            lines.Children.Add(new TextBlock { Text = row.Summary, FontSize = 12,
                Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap });
            var item = new ListBoxItem { Content = lines, Tag = row.Id };
            System.Windows.Automation.AutomationProperties.SetName(item, row.Name + " " + row.Summary);
            list.Items.Add(item);
            if (row.Id == selected) list.SelectedItem = item;
        }
        lastVisible = visible; lastCount = rows.Count; lastPage = page; lastRows = shown;
        summary.Text = $"검색 결과 {rows.Count:N0}개 · {page + 1}/{Math.Max(1, (rows.Count + PageSize - 1) / PageSize)} 페이지" +
            (visible ? " · 파일 작업은 다운로드 금고에서" : " · 금고 잠김: 보관 목록 숨김") +
            (rows.Count == 0 ? "\n표시할 다운로드가 없습니다." : "");
    }
}
