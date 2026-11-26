using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Veil.Desktop;

/// <summary>Browser conveniences: keyboard shortcuts, tab menu, drag to reorder, drag out to a new
/// window and back, full screen and Windows 11 window corners.</summary>
public partial class BrowserWindow
{
    private TabItem? pressedTab;
    private Point pressPoint;
    private bool draggingTab;
    private double grabOffset;
    private bool fullScreen;
    private bool fullScreenFromPage;
    private Rect restoreBounds;
    private WindowState restoreState;
    private bool roundedCorners;

    private void InitializeWindowFeatures()
    {
        SourceInitialized += (_, _) =>
        {
            roundedCorners = WindowCorners.Apply(this);
            SyncWindowState();
        };
        Tabs.PreviewMouseLeftButtonDown += TabPress;
        Tabs.PreviewMouseMove += TabDrag;
        Tabs.PreviewMouseLeftButtonUp += TabRelease;
        Tabs.LostMouseCapture += (_, _) => { if (draggingTab) FinishTabDrag(); };
        Tabs.PreviewMouseUp += (_, args) =>
        {
            // Middle-click closes a tab, as in Chrome and Edge.
            if (args.ChangedButton == MouseButton.Middle && TabAt(args.OriginalSource) is { } item)
            {
                args.Handled = true;
                if (item.Tag is BrowserTab { Pinned: false }) CloseTab(item);
            }
        };
        Tabs.PreviewMouseRightButtonUp += (_, args) =>
        {
            if (TabAt(args.OriginalSource) is not { } item || !CanUseTab(item)) return;
            args.Handled = true;
            ShowTabMenu(item);
        };
        // Mouse back/forward buttons over the browser chrome.
        PreviewMouseDown += (_, args) =>
        {
            if (args.ChangedButton == MouseButton.XButton1) { args.Handled = true; BackClick(this, args); }
            else if (args.ChangedButton == MouseButton.XButton2) { args.Handled = true; ForwardClick(this, args); }
        };
    }

    private TabItem? TabAt(object source)
    {
        for (var node = source as DependencyObject; node is not null && node != Tabs; node = Parent(node))
        {
            if (node is ButtonBase) return null; // close / mute buttons keep their own click
            if (node is TabItem item && Tabs.Items.Contains(item)) return item;
        }
        return null;

        static DependencyObject? Parent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    }

    // ── Keyboard shortcuts ──────────────────────────────────────────────────

    // Leave IME/dead-key processing to WPF rather than interpreting a composition as a browser command.
    private static bool IsCompositionKey(Key key) => key is Key.ImeProcessed or Key.DeadCharProcessed;

    private void BrowserKeyDown(object sender, KeyEventArgs args)
    {
        if (closing || restoring || session.IsEnding || session.EngineLost || args.Handled) return;
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (IsCompositionKey(key)) return;
        var modifiers = Keyboard.Modifiers;
        if ((key == Key.Apps && modifiers == ModifierKeys.None ||
             key == Key.F10 && modifiers == ModifierKeys.Shift) &&
            Keyboard.FocusedElement is DependencyObject focused && TabAt(focused) is { } focusedTab && CanUseTab(focusedTab))
        {
            args.Handled = true;
            long focus = managementFocusEpoch;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsActive && managementFocusEpoch == focus && CanUseTab(focusedTab) &&
                    Keyboard.FocusedElement is DependencyObject currentFocus && TabAt(currentFocus) == focusedTab)
                    ShowTabMenu(focusedTab, keyboard: true);
            }));
            return;
        }
        const ModifierKeys Ctrl = ModifierKeys.Control, Shift = ModifierKeys.Shift, Alt = ModifierKeys.Alt;
        bool editing = Address.IsKeyboardFocusWithin || FindText.IsKeyboardFocusWithin ||
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;
        var originalTab = Active;
        long originalEpoch = originalTab?.NavigationEpoch ?? -1;
        long originalFocus = managementFocusEpoch;
        // Tab cycling must remain repeatable. Page commands, however, must not act on a replacement tab.
        bool windowCommand = (modifiers, key) is
            (Ctrl, Key.T or Key.N or Key.Tab or Key.PageDown or Key.PageUp) or
            (Ctrl | Shift, Key.T or Key.N or Key.Tab or Key.W or Key.A) or
            (Ctrl, >= Key.D1 and <= Key.D9) or (Ctrl, >= Key.NumPad1 and <= Key.NumPad9) or
            (ModifierKeys.None, Key.F11);
        Action? command = (modifiers, key) switch
        {
            // Tabs and windows
            (Ctrl, Key.T) => () => NewTabClick(this, args),
            (Ctrl, Key.N) => () => NewWindowClick(this, args),
            (Ctrl, Key.W) or (Ctrl, Key.F4) => () => { if (originalTab is not null && CanUseTab(originalTab.Item)) CloseTab(originalTab.Item); },
            (Ctrl | Shift, Key.W) => Close,
            (Ctrl | Shift, Key.T) => () => RestoreClick(this, args),
            (Ctrl | Shift, Key.A) => () => TabSearchClick(this, args),
            (Ctrl, Key.Tab) or (Ctrl, Key.PageDown) => () => CycleTab(1),
            (Ctrl | Shift, Key.Tab) or (Ctrl, Key.PageUp) => () => CycleTab(-1),
            (Ctrl, >= Key.D1 and <= Key.D8) => () => SelectTab(key - Key.D1),
            (Ctrl, >= Key.NumPad1 and <= Key.NumPad8) => () => SelectTab(key - Key.NumPad1),
            (Ctrl, Key.D9 or Key.NumPad9) => () => SelectTab(Tabs.Items.Count - 1),
            (Ctrl | Shift, Key.PageUp) => () => MoveSelectedTab(-1),
            (Ctrl | Shift, Key.PageDown) => () => MoveSelectedTab(1),
            // Address bar and navigation
            (Ctrl, Key.L) or (Alt, Key.D) or (ModifierKeys.None, Key.F6) => FocusAddress,
            (Ctrl, Key.K) or (Ctrl, Key.E) => () => { FocusAddress(); Address.Clear(); },
            (Alt, Key.Left) => () => BackClick(this, args),
            (Alt, Key.Right) => () => ForwardClick(this, args),
            (Alt, Key.Home) => GoHome,
            (Ctrl, Key.R) or (ModifierKeys.None, Key.F5) => () => ReloadClick(this, args),
            (Ctrl, Key.F5) or (Shift, Key.F5) or (Ctrl | Shift, Key.R) => HardReload,
            (ModifierKeys.None, Key.Escape) when fullScreen && fullScreenFromPage => () => SetFullScreen(false),
            (ModifierKeys.None, Key.Escape) when !editing && Active is { Loading: true } => StopLoading,
            (ModifierKeys.None, Key.F11) => () => SetFullScreen(!fullScreen),
            // Page
            (Ctrl, Key.F) => () => FindClick(this, args),
            (ModifierKeys.None, Key.F3) or (Ctrl, Key.G) => () => _ = Find(false),
            (Shift, Key.F3) or (Ctrl | Shift, Key.G) => () => _ = Find(true),
            (Ctrl, Key.Add or Key.OemPlus) => () => Zoom(0.1),
            (Ctrl, Key.Subtract or Key.OemMinus) => () => Zoom(-0.1),
            (Ctrl, Key.D0 or Key.NumPad0) => () => Zoom(null),
            // Library
            (Ctrl, Key.D) => () => BookmarkClick(this, args),
            (Ctrl, Key.B) or (Ctrl | Shift, Key.O) => () => BookmarksClick(this, args),
            (Ctrl | Shift, Key.B) => () => ToggleBookmarkBarClick(this, args),
            (Ctrl, Key.H) => () => HistoryClick(this, args),
            (Ctrl, Key.J) => () => DownloadsClick(this, args),
            (Ctrl | Shift, Key.J) => () => VaultClick(this, args),
            (Ctrl, Key.OemComma) => () => SettingsClick(this, args),
            (Ctrl | Shift, Key.Delete) => () => SiteDataClick(this, args),
            (Alt, Key.F) or (Alt, Key.E) => () => MenuClick(this, args),
            // Deliberately unavailable in this developer build
            (Ctrl, Key.P) => () => Status.Text = "인쇄와 PDF 저장은 다운로드 금고 검증 전까지 사용할 수 없습니다.",
            (Ctrl, Key.S) => () => Status.Text = "페이지 저장은 다운로드 금고 검증 전까지 사용할 수 없습니다.",
            (Ctrl, Key.U) => () => Status.Text = "소스 보기는 개발자용 빌드에서 사용할 수 없습니다.",
            (Ctrl | Shift, Key.N) => () => PrivateWindowClick(this, args),
            _ => null,
        };
        if (command is null) return;
        args.Handled = true;
        // The WebView2 accelerator callback can block the browser until input routing returns.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closing || restoring || session.IsEnding || session.EngineLost || !IsActive) return;
            if (!windowCommand && (managementFocusEpoch != originalFocus || Active != originalTab ||
                originalTab is not null && (!CanUseTab(originalTab.Item) || originalTab.NavigationEpoch != originalEpoch))) return;
            try { command(); }
            catch (Exception error) when (RecoverableFailure.Is(error))
            { if (!closing) Status.Text = "단축키 작업을 완료하지 못했습니다."; }
        }));
    }

    // ── Tab menu ─────────────────────────────────────────────────────────────

    private bool CanUseTab(TabItem item) => !closing && !restoring && !session.IsEnding && !session.EngineLost &&
        Tabs.Items.Contains(item) && !closingTabs.Contains(item) && item.Tag is BrowserTab { Closing: false } tab && tab.Owner == this;

    private bool CanMoveTo(TabItem item, BrowserWindow target) =>
        CanUseTab(item) && !session.IsPrivate && target != this && ReferenceEquals(session, target.session) &&
        !target.closing && !target.restoring && !target.session.IsEnding && !target.session.EngineLost;

    private bool MoveToExistingWindow(TabItem item, BrowserWindow target, int? index = null)
    {
        if (!CanMoveTo(item, target)) return false;
        if (!TransferTabTo(item, target, index ?? target.Tabs.Items.Count)) return false;
        if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
        target.Activate();
        return true;
    }

    private void ShowTabMenu(TabItem item, bool keyboard = false)
    {
        if (!CanUseTab(item)) return;
        var tab = (BrowserTab)item.Tag;
        int index = Tabs.Items.IndexOf(item);
        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)), PlacementTarget = item,
            Placement = keyboard ? PlacementMode.Bottom : PlacementMode.MousePoint };
        menu.Opened += MenuOpened;
        MenuItem Entry(string header, string gesture, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled,
                Style = (Style)FindResource(typeof(MenuItem)) };
            entry.Click += (_, _) => { if (CanUseTab(item)) action(); };
            return entry;
        }
        void Add(string header, string gesture, Action action, bool enabled = true) => menu.Items.Add(Entry(header, gesture, action, enabled));
        void Separate() => menu.Items.Add(new Separator { Style = (Style)FindResource(typeof(Separator)) });
        bool CanBulk(TabItem candidate) => candidate != item && CanUseTab(candidate) && candidate.Tag is BrowserTab { Pinned: false };
        TabItem[] Scope(int side)
        {
            int current = Tabs.Items.IndexOf(item);
            if (current < 0) return [];
            return Tabs.Items.OfType<TabItem>().Where((candidate, position) => CanBulk(candidate) &&
                (side == 0 || (side < 0 ? position < current : position > current))).ToArray();
        }

        Add("오른쪽에 새 탭", "", () => _ = OpenTab("about:blank", index: Tabs.Items.IndexOf(item) + 1));
        Separate();
        Add("새로고침", "", () => { if (IsWebUrl(tab.Url)) tab.EngineView.Reload(); }, IsWebUrl(tab.Url));
        Add("탭 복제", "", () => { if (IsWebUrl(tab.Url)) _ = OpenTab(tab.Url, index: Tabs.Items.IndexOf(item) + 1); }, IsWebUrl(tab.Url));
        Add(tab.View.CoreWebView2?.IsMuted == true ? "탭 음소거 해제" : "탭 음소거", "", () => ToggleMute(tab));
        Add(tab.Pinned ? "탭 고정 해제" : "탭 고정", "", () => SetTabPinned(item, !tab.Pinned));
        if (!session.IsPrivate)
        {
            var move = new MenuItem { Header = "탭을 다른 창으로 이동", Style = (Style)FindResource(typeof(MenuItem)) };
            move.Items.Add(Entry("새 창", "", () => { if (Tabs.Items.Count > 1) MoveTabToNewWindow(item); }, Tabs.Items.Count > 1));
            int number = 0;
            foreach (var target in session.Windows.ToArray())
            {
                number++;
                if (!CanMoveTo(item, target)) continue;
                var destination = target;
                move.Items.Add(Entry($"창 {number} · 탭 {target.Tabs.Items.Count}개", "",
                    () => MoveToExistingWindow(item, destination)));
            }
            move.IsEnabled = move.Items.OfType<MenuItem>().Any(entry => entry.IsEnabled);
            menu.Items.Add(move);
        }
        Separate();
        Add("탭 닫기", "", () => CloseTab(item));
        Add("다른 탭 닫기", "", () => CloseTabs(Scope(0)), Scope(0).Length > 0);
        Add("왼쪽 탭 닫기", "", () => CloseTabs(Scope(-1)), Scope(-1).Length > 0);
        Add("오른쪽 탭 닫기", "", () => CloseTabs(Scope(1)), Scope(1).Length > 0);
        if (!session.IsPrivate)
        {
            Separate();
            Add("닫은 탭 다시 열기", "Ctrl+Shift+T", () => RestoreClick(this, new RoutedEventArgs()),
                closedTabs.Count > 0 && !session.RestoringClosedTab);
        }
        menu.IsOpen = true;
    }

    private async void CloseTabs(IEnumerable<TabItem> items)
    {
        try
        {
            foreach (var item in items.ToArray())
            {
                if (closing || session.IsEnding || session.EngineLost) break;
                if (CanUseTab(item)) await CloseTabAsync(item, protectPinned: true);
            }
        }
        catch (Exception error) when (RecoverableFailure.Is(error)) { if (!closing) Status.Text = "일부 탭을 닫지 못했습니다."; }
    }

    // ── Moving tabs ──────────────────────────────────────────────────────────

    private void MoveSelectedTab(int direction)
    {
        if (Tabs.SelectedItem is not TabItem item || !CanUseTab(item)) return;
        int from = Tabs.Items.IndexOf(item), to = from + direction;
        if (to < 0 || to >= Tabs.Items.Count || !CanSwapTabs(item, (TabItem)Tabs.Items[to])) return;
        SwapNeighbor(item, from, to);
        SaveTabs();
    }

    /// <summary>Moves the dragged (selected) tab by relocating its neighbour, so selection and the
    /// visible page never change mid-drag. The neighbour glides into place.</summary>
    private void SwapNeighbor(TabItem dragged, int from, int to)
    {
        var neighbor = (TabItem)Tabs.Items[to];
        if (!CanSwapTabs(dragged, neighbor)) return;
        double before = SlotLeft(neighbor);
        Tabs.Items.RemoveAt(to);
        Tabs.Items.Insert(from, neighbor);
        Tabs.UpdateLayout();
        if (Motion.Enabled)
        {
            var (_, move) = Motion.Transforms(neighbor);
            move.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                Motion.Keys(before - SlotLeft(neighbor), 0, 0, 260, Motion.Expo));
        }
        Tabs.SelectedItem = dragged;
    }

    /// <summary>Left edge of a tab's layout slot in TabRow coordinates, ignoring render transforms.</summary>
    private double SlotLeft(TabItem item)
    {
        if (VisualTreeHelper.GetParent(item) is not UIElement host) return 0;
        return host.TranslatePoint(new Point(0, 0), TabRow).X + LayoutInformation.GetLayoutSlot(item).X;
    }

    private void TabPress(object sender, MouseButtonEventArgs args)
    {
        pressedTab = TabAt(args.OriginalSource);
        if (pressedTab is null || closingTabs.Contains(pressedTab)) { pressedTab = null; return; }
        pressPoint = args.GetPosition(TabRow);
    }

    private void TabDrag(object sender, MouseEventArgs args)
    {
        if (pressedTab is not { } item || closingTabs.Contains(item) || !Tabs.Items.Contains(item) || args.LeftButton != MouseButtonState.Pressed) return;
        var point = args.GetPosition(TabRow);
        if (!draggingTab)
        {
            if (Math.Abs(point.X - pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            draggingTab = true;
            Tabs.SelectedItem = item;
            grabOffset = pressPoint.X - SlotLeft(item);
            Panel.SetZIndex(item, 10);
            Tabs.CaptureMouse();
        }

        // Pulling the tab well above or below the strip tears it off into its own window.
        if (!session.IsPrivate && (point.Y < -36 || point.Y > TabRow.ActualHeight + 36))
        {
            var torn = item;
            Tabs.ReleaseMouseCapture();
            Dispatcher.BeginInvoke(() => TearOff(torn), DispatcherPriority.Input);
            return;
        }

        var (_, move) = Motion.Transforms(item);
        move.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        double width = item.ActualWidth;
        double left = Math.Clamp(point.X - grabOffset, 0, Math.Max(0, Tabs.ActualWidth - width));
        int index = Tabs.Items.IndexOf(item);
        // Swap with a neighbour once the dragged tab's centre crosses the neighbour's centre.
        if (index > 0 && left + width / 2 < SlotLeft((TabItem)Tabs.Items[index - 1]) + ((TabItem)Tabs.Items[index - 1]).ActualWidth / 2)
            SwapNeighbor(item, index, index - 1);
        else if (index < Tabs.Items.Count - 1 &&
                 left + width / 2 > SlotLeft((TabItem)Tabs.Items[index + 1]) + ((TabItem)Tabs.Items[index + 1]).ActualWidth / 2)
            SwapNeighbor(item, index, index + 1);
        move.X = left - SlotLeft(item);
    }

    private void TabRelease(object sender, MouseButtonEventArgs args)
    {
        if (draggingTab) Tabs.ReleaseMouseCapture();
        pressedTab = null;
    }

    private void FinishTabDrag()
    {
        draggingTab = false;
        if (pressedTab is { } item)
        {
            Panel.SetZIndex(item, 0);
            var (_, move) = Motion.Transforms(item);
            double offset = move.X;
            move.X = 0;
            if (Motion.Enabled && offset != 0)
                move.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, Motion.Keys(offset, 0, 0, 280, Motion.Expo));
        }
        pressedTab = null;
        SaveTabs();
    }

    private void TearOff(TabItem item)
    {
        if (session.IsPrivate || !CanUseTab(item)) return;
        if (Tabs.Items.Count == 1)
        {
            // A lone tab carries its whole window, and can be dropped onto another window's tabs.
            DragWindowThenMerge(this);
            return;
        }
        var cursor = CursorDip();
        var window = MoveTabToNewWindow(item, new Point(cursor.X - 90, cursor.Y - 18));
        window.Dispatcher.BeginInvoke(() => DragWindowThenMerge(window), DispatcherPriority.Loaded);
    }

    /// <summary>Moves a tab into a new window next to this one; the page keeps running.</summary>
    private BrowserWindow MoveTabToNewWindow(TabItem item, Point? at = null)
    {
        if (session.IsPrivate || !CanUseTab(item) || Tabs.Items.Count <= 1) return this;
        // The new window adopts the tab only after its engine is ready.
        return OpenWindow([], 0, cascade: true, adopt: item, at: at);
    }

    private static void DragWindowThenMerge(BrowserWindow window)
    {
        // Only a real drag can drop the window's tab onto another window.
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        try { window.DragMove(); }
        catch (InvalidOperationException) { return; }
        if (window.closing || window.Tabs.Items.Count != 1) return;
        var item = (TabItem)window.Tabs.Items[0];
        var target = window.session.Windows.FirstOrDefault(other => window.CanMoveTo(item, other) &&
            other.OverTabStrip(CursorPixels(), out _));
        if (target is null) return;
        target.OverTabStrip(CursorPixels(), out int index);
        window.MoveToExistingWindow(item, target, index);
    }

#if DEBUG
    private bool smokeFailTabTransfer;
#endif
    /// <summary>Move the live page before closing its old window or persisting the new layout.</summary>
    private bool TransferTabTo(TabItem item, BrowserWindow target, int index, bool initializingTarget = false)
    {
        if (!CanUseTab(item) || session.IsPrivate || target == this || !ReferenceEquals(session, target.session) ||
            target.closing || target.restoring && !initializingTarget) return false;
        var tab = (BrowserTab)item.Tag;
        int originalIndex = Tabs.Items.IndexOf(item);
        var originalSelection = Tabs.SelectedItem;
        session.TabTransferDepth++;
        try
        {
            DetachTab(item);
#if DEBUG
            if (smokeFailTabTransfer) throw new InvalidOperationException("Synthetic adoption failure.");
#endif
            target.AttachTab(item, index);
            if (Tabs.Items.Count == 0) Close();
            else LayoutTabs();
            return true;
        }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            // Roll back a partial adoption while the original window is still alive.
            if (!closing && !session.IsEnding && !session.EngineLost)
            {
                if (target.Tabs.Items.Contains(item)) target.Tabs.Items.Remove(item);
                if (target.Pages.Children.Contains(tab.EngineView.Element)) target.Pages.Children.Remove(tab.EngineView.Element);
                tab.Owner = this;
                if (!Pages.Children.Contains(tab.EngineView.Element)) Pages.Children.Add(tab.EngineView.Element);
                if (!Tabs.Items.Contains(item)) Tabs.Items.Insert(TabInsertionIndex(tab.Pinned, originalIndex), item);
                if (originalSelection is not null && Tabs.Items.Contains(originalSelection)) Tabs.SelectedItem = originalSelection;
                LayoutTabs(animate: false);
                RefreshChrome();
                target.LayoutTabs(animate: false);
                target.RefreshChrome();
            }
            if (!closing) Status.Text = "탭을 이동하지 못했습니다.";
            return false;
        }
        finally
        {
            session.TabTransferDepth--;
            SaveSession();
        }
    }

    /// <summary>Removes a tab without closing its page or saving an intermediate layout.</summary>
    private void DetachTab(TabItem item)
    {
        var tab = (BrowserTab)item.Tag;
        var (_, move) = Motion.Transforms(item);
        move.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        move.X = 0;
        Panel.SetZIndex(item, 0);
        tab.NavigationEpoch++;
        tab.LastDownloadRequest = null;
        if (retryDownload?.Tab == tab) retryDownload = null;
        ClearManagement(tab);
        Tabs.Items.Remove(item);
        Pages.Children.Remove(tab.EngineView.Element);
    }

    /// <summary>Adopts a live tab from another window at <paramref name="index"/>.</summary>
    private void AttachTab(TabItem item, int index)
    {
        var tab = (BrowserTab)item.Tag;
        if (!ReferenceEquals(tab.Owner.session, session))
            throw new InvalidOperationException("Tabs cannot cross browser sessions.");
        tab.Owner = this;
        Pages.Children.Add(tab.EngineView.Element);
        Tabs.Items.Insert(TabInsertionIndex(tab.Pinned, index), item);
        LayoutTabs(item);
        Tabs.SelectedItem = item;
        foreach (TabItem other in Tabs.Items)
            ((BrowserTab)other.Tag).EngineView.Element.Visibility =
                other == item && (IsWebUrl(tab.Url) || IsInternalUrl(tab.Url)) ? Visibility.Visible : Visibility.Hidden;
        if (tab.View.CoreWebView2 is not null) UpdateAudio(tab);
        RefreshChrome();
        SyncProgress();
        SaveTabs();
    }

    /// <summary>True when a screen point (device pixels) is over this window's tab strip.</summary>
    private bool OverTabStrip(Point screen, out int index)
    {
        index = Tabs.Items.Count;
        if (!IsVisible || WindowState == WindowState.Minimized || PresentationSource.FromVisual(TabHost) is null) return false;
        var origin = TabHost.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        var strip = new Rect(origin.X, origin.Y - 10 * dpi.DpiScaleY,
            TabHost.ActualWidth * dpi.DpiScaleX, (TabHost.ActualHeight + 20) * dpi.DpiScaleY);
        if (!strip.Contains(screen)) return false;
        double x = TabRow.PointFromScreen(screen).X;
        for (int i = 0; i < Tabs.Items.Count; i++)
        {
            var other = (TabItem)Tabs.Items[i];
            if (x < SlotLeft(other) + other.ActualWidth / 2) { index = i; break; }
        }
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    private struct NativePoint { public int X, Y; }

    private static Point CursorPixels() => GetCursorPos(out var p) ? new Point(p.X, p.Y) : new Point();

    private Point CursorDip()
    {
        var pixels = CursorPixels();
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(pixels.X / dpi.DpiScaleX, pixels.Y / dpi.DpiScaleY);
    }

    // ── Full screen ──────────────────────────────────────────────────────────

    /// <summary>F11 or a page (e.g. video) toggles full screen. The developer notice stays visible.</summary>
    private void SetFullScreen(bool on, bool page = false)
    {
        if (on == fullScreen) return;
        fullScreen = on;
        fullScreenFromPage = on && page;
        var chrome = on ? Visibility.Collapsed : Visibility.Visible;
        TitleBar.Visibility = chrome;
        Toolbar.Visibility = chrome;
        DownloadPopup.IsOpen = false;
        RefreshBookmarkBar();
        StatusBar.Visibility = chrome;
        if (on)
        {
            restoreState = WindowState;
            restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal;
            var monitor = WindowCorners.MonitorBounds(this);
            Left = monitor.Left; Top = monitor.Top; Width = monitor.Width; Height = monitor.Height;
            WindowCorners.SetRounded(this, false);
            Frame.BorderThickness = new Thickness(0);
            Status.Text = "전체 화면 · F11로 종료";
        }
        else
        {
            Left = restoreBounds.Left; Top = restoreBounds.Top; Width = restoreBounds.Width; Height = restoreBounds.Height;
            WindowState = restoreState;
            if (roundedCorners) WindowCorners.SetRounded(this, true);
            SyncWindowState();
            if (page && Active?.View.CoreWebView2 is { ContainsFullScreenElement: true } web)
                _ = web.ExecuteScriptAsync("document.fullscreenElement && document.exitFullscreen()");
        }
        if (!on && !page && Active?.View.CoreWebView2 is { ContainsFullScreenElement: true } current)
            _ = current.ExecuteScriptAsync("document.exitFullscreen()");
    }
}

/// <summary>Windows 11 rounded corners and border colour via DWM; a no-op on older Windows.</summary>
internal static class WindowCorners
{
    private const int CornerPreference = 33, BorderColor = 34;
    private const int DoNotRound = 1, Round = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Requests rounded corners and a theme-coloured DWM border. Returns false when unsupported.</summary>
    internal static bool Apply(Window window)
    {
        if (!SetRounded(window, true)) return false;
        int color = 0x003B2A26; // COLORREF (BGR) of #262A3B, the theme's line colour
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, BorderColor, ref color, sizeof(int));
        return true;
    }

    internal static bool SetRounded(Window window, bool rounded)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        int value = rounded ? Round : DoNotRound;
        return DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, CornerPreference, ref value, sizeof(int)) == 0;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }

    /// <summary>Full bounds of the window's monitor in device-independent units.</summary>
    internal static Rect MonitorBounds(Window window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2 /* nearest */);
        if (!GetMonitorInfo(monitor, ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var dpi = VisualTreeHelper.GetDpi(window);
        var r = info.Monitor;
        return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY,
            (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
    }
}
