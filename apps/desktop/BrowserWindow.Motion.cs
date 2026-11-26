using System.ComponentModel;
using System.IO;
using Path = System.Windows.Shapes.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private const double MaxTabWidth = 224;
    private const double MinTabWidth = 96;
    private readonly HashSet<TabItem> closingTabs = [];
    private readonly List<TextBlock> accentLetters = [];
    private LinearGradientBrush? sweepMask;
    private int progressGeneration;

    private Brush Res(string key) => (Brush)FindResource(key);

    private void InitializeMotion(bool playIntro)
    {
        if (playIntro) Loaded += (_, _) => PlayIntro();
        StateChanged += (_, _) => SyncWindowState();
        Home.SizeChanged += (_, _) => LayoutBackdrop();
        BuildWordmark();
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(Status, (_, _) => AnimateStatus());
        if (Motion.Enabled)
        {
            var pulse = (ScaleTransform)NoticePulse.RenderTransform;
            Motion.Loop(pulse, ScaleTransform.ScaleXProperty, 1, 2.8, 1.8);
            Motion.Loop(pulse, ScaleTransform.ScaleYProperty, 1, 2.8, 1.8);
            Motion.Loop(NoticePulse, OpacityProperty, 0.8, 0, 1.8);
        }
        else NoticePulse.Opacity = 0;
    }

    // ── Window chrome ────────────────────────────────────────────────────────

    private void SyncWindowState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        // A maximized WindowChrome window overhangs the work area by its resize border.
        var edge = SystemParameters.WindowResizeBorderThickness;
        Frame.Margin = maximized ? new Thickness(edge.Left + 2, edge.Top + 2, edge.Right + 2, edge.Bottom + 2) : new Thickness(0);
        // DWM draws the border and rounded corners on Windows 11.
        Frame.BorderThickness = new Thickness(maximized || roundedCorners || fullScreen ? 0 : 1);
        MaximizeButton.Content = maximized ? "" : "";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "이전 크기로 복원" : "최대화");
    }

    private void MinimizeClick(object sender, RoutedEventArgs args) => SystemCommands.MinimizeWindow(this);
    private void CloseWindowClick(object sender, RoutedEventArgs args) => Close();
    private void MaximizeClick(object sender, RoutedEventArgs args)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    // ── Intro: the chrome assembles itself in a short, staggered sequence ───

    private void PlayIntro()
    {
        var spin = (RotateTransform)Logo.RenderTransform;
        if (Motion.Enabled)
        {
            spin.BeginAnimation(RotateTransform.AngleProperty, Motion.Keys(-200, 0, 0, 1100, Motion.Spring));
            Logo.BeginAnimation(OpacityProperty, Motion.Keys(0, 1, 0, 300));
        }
        Motion.Enter(TabRow, 140, dx: -24, ms: 800);
        Motion.Enter(Caption, 200, ms: 600);
        Motion.Enter(NavGroup, 220, dy: -12, ms: 700);
        Motion.Enter(AddressHost, 300, scale: 0.92, ms: 900);
        Motion.Enter(ActionGroup, 380, dy: -12, ms: 700);
        Motion.Wipe(Notice, 460, 800);
        Motion.Enter(StatusBar, 560, dy: 14, ms: 700);
    }

    // ── Home: wordmark, orbit rings and flowing strands ─────────────────────

    private void BuildWordmark()
    {
        // Base layer letters plus an accent copy revealed by a travelling light band.
        var basePanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var accentPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        foreach (char letter in "VEIL")
        {
            basePanel.Children.Add(Letter(letter, Res("TextBrush")));
            var accent = Letter(letter, Res("AccentBrush"));
            accentLetters.Add(accent);
            accentPanel.Children.Add(accent);
        }
        sweepMask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.35) };
        sweepMask.GradientStops.Add(new GradientStop(Colors.Transparent, -0.4));
        sweepMask.GradientStops.Add(new GradientStop(Colors.White, -0.2));
        sweepMask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        accentPanel.OpacityMask = sweepMask;
        Wordmark.Children.Add(basePanel);
        Wordmark.Children.Add(accentPanel);
        if (!Motion.Enabled) accentPanel.Visibility = Visibility.Collapsed;

        static TextBlock Letter(char value, Brush brush) => new()
        {
            Text = value.ToString(),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 72,
            Margin = new Thickness(9, 0, 9, 0),
            Foreground = brush,
        };
    }

    private void HomeVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (Home.IsVisible) PlayHome();
        else StopHomeLoops();
    }

    private void PlayHome()
    {
        if (!Motion.Enabled) return;
        var letters = ((StackPanel)Wordmark.Children[0]).Children.OfType<TextBlock>().ToArray();
        for (int i = 0; i < letters.Length; i++)
        {
            Motion.Enter(letters[i], 120 + i * 95, dy: 46, ms: 1100, blur: 18);
            Motion.Enter(accentLetters[i], 120 + i * 95, dy: 46, ms: 1100);
        }
        Motion.Enter(Tagline, 620, dy: 16, ms: 900, blur: 6);
        Motion.Enter(HintChip, 780, dy: 16, scale: 0.9, ms: 900);
        Motion.Enter(Rings, 0, scale: 0.55, ms: 1600);
        Motion.Enter(Backdrop, 0, ms: 1400);
        Motion.Enter(Strands, 300, dy: 40, ms: 1600);
        StartHomeLoops();
    }

    private void StartHomeLoops()
    {
        if (!Motion.Enabled) return;
        Motion.Loop((RotateTransform)RingOuter.RenderTransform, RotateTransform.AngleProperty, 0, 360, 120);
        Motion.Loop((RotateTransform)RingInner.RenderTransform, RotateTransform.AngleProperty, 360, 0, 70);
        Motion.Loop((RotateTransform)RingComet.RenderTransform, RotateTransform.AngleProperty, 0, 360, 6.5, ease: Motion.Sine);
        var violet = (TranslateTransform)OrbViolet.RenderTransform;
        var cyan = (TranslateTransform)OrbCyan.RenderTransform;
        Motion.Loop(violet, TranslateTransform.XProperty, -70, 70, 11, true, Motion.Sine);
        Motion.Loop(violet, TranslateTransform.YProperty, -40, 50, 13, true, Motion.Sine);
        Motion.Loop(cyan, TranslateTransform.XProperty, 60, -80, 12.5, true, Motion.Sine);
        Motion.Loop(cyan, TranslateTransform.YProperty, 40, -50, 9.5, true, Motion.Sine);
        StartStrandLoops();
        if (sweepMask is not null)
        {
            // Light band travels across the wordmark, then rests before the next pass.
            double[] offsets = [-0.4, -0.2, 0];
            for (int i = 0; i < 3; i++)
            {
                var sweep = new DoubleAnimationUsingKeyFrames
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(1300),
                    Duration = TimeSpan.FromSeconds(5.5),
                };
                sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.Zero)));
                sweep.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i] + 1.6, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6)), Motion.Sine));
                sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(offsets[i] + 1.6, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.5))));
                sweepMask.GradientStops[i].BeginAnimation(GradientStop.OffsetProperty, sweep);
            }
        }
    }

    private void StopHomeLoops()
    {
        Motion.Stop((RotateTransform)RingOuter.RenderTransform, RotateTransform.AngleProperty);
        Motion.Stop((RotateTransform)RingInner.RenderTransform, RotateTransform.AngleProperty);
        Motion.Stop((RotateTransform)RingComet.RenderTransform, RotateTransform.AngleProperty);
        foreach (var orb in new[] { OrbViolet, OrbCyan })
        {
            var move = (TranslateTransform)orb.RenderTransform;
            Motion.Stop(move, TranslateTransform.XProperty);
            Motion.Stop(move, TranslateTransform.YProperty);
        }
        foreach (var strand in Strands.Children.OfType<Path>())
            Motion.Stop(strand.RenderTransform, TranslateTransform.XProperty);
        if (sweepMask is not null)
            foreach (var stop in sweepMask.GradientStops) Motion.Stop(stop, GradientStop.OffsetProperty);
    }

    private void LayoutBackdrop()
    {
        double width = Home.ActualWidth, height = Home.ActualHeight;
        if (width <= 0 || height <= 0) return;
        Canvas.SetLeft(OrbViolet, width * 0.18 - OrbViolet.Width / 2);
        Canvas.SetTop(OrbViolet, height * 0.3 - OrbViolet.Height / 2);
        Canvas.SetLeft(OrbCyan, width * 0.8 - OrbCyan.Width / 2);
        Canvas.SetTop(OrbCyan, height * 0.72 - OrbCyan.Height / 2);

        // Each strand is two wavelengths per viewport width and drawn twice as wide, so a
        // translation by exactly one viewport width loops without a seam.
        Strands.Children.Clear();
        (double amplitude, double phase, double opacity, double thickness)[] strands =
            [(34, 0, 0.42, 1.4), (22, 1.9, 0.26, 1), (46, 3.6, 0.16, 2.2)];
        double wavelength = width / 2;
        foreach (var (amplitude, phase, opacity, thickness) in strands)
        {
            var figure = new PathFigure { StartPoint = new Point(0, 120 + amplitude * Math.Sin(phase)) };
            var points = new PolyLineSegment();
            for (double x = 6; x <= width * 2 + 6; x += 6)
                points.Points.Add(new Point(x, 120 + amplitude * Math.Sin(x / wavelength * 2 * Math.PI + phase)));
            figure.Segments.Add(points);
            var geometry = new PathGeometry([figure]);
            geometry.Freeze();
            Strands.Children.Add(new Path
            {
                Data = geometry,
                Stroke = Res("AccentBrush"),
                StrokeThickness = thickness,
                Opacity = opacity,
                RenderTransform = new TranslateTransform(),
            });
        }
        if (Home.IsVisible) StartStrandLoops();
    }

    private void StartStrandLoops()
    {
        double width = Home.ActualWidth;
        if (!Motion.Enabled || width <= 0) return;
        double[] periods = [16, 23, 31];
        int i = 0;
        foreach (var strand in Strands.Children.OfType<Path>())
            Motion.Loop((TranslateTransform)strand.RenderTransform, TranslateTransform.XProperty, 0, -width, periods[i++ % 3]);
    }

    // ── Navigation progress line ─────────────────────────────────────────────

    private void SyncProgress()
    {
        if (Active?.Loading == true) StartProgress();
        else HideProgress();
    }

    private void StartProgress()
    {
        ++progressGeneration;
        var fill = (ScaleTransform)ProgressFill.RenderTransform;
        Progress.BeginAnimation(OpacityProperty, Motion.To(1, 120));
        var creep = new DoubleAnimationUsingKeyFrames();
        creep.KeyFrames.Add(new EasingDoubleKeyFrame(0.28, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)), Motion.Out));
        creep.KeyFrames.Add(new EasingDoubleKeyFrame(0.62, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800)), Motion.Out));
        creep.KeyFrames.Add(new EasingDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8)), Motion.Out));
        if (!Motion.Enabled) { fill.ScaleX = 0.6; return; }
        fill.BeginAnimation(ScaleTransform.ScaleXProperty, creep);
        Motion.Loop((TranslateTransform)ProgressGleam.RenderTransform, TranslateTransform.XProperty,
            -ProgressGleam.Width, Math.Max(Progress.ActualWidth, 1), 1.2, ease: Motion.Sine);
    }

    private void FinishProgress()
    {
        int generation = ++progressGeneration;
        var fill = (ScaleTransform)ProgressFill.RenderTransform;
        if (!Motion.Enabled) { HideProgress(); return; }
        fill.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 220));
        var fade = Motion.To(0, 380, delayMs: 220);
        fade.Completed += (_, _) => { if (generation == progressGeneration) ResetProgress(); };
        Progress.BeginAnimation(OpacityProperty, fade);
    }

    private void HideProgress()
    {
        ++progressGeneration;
        Progress.BeginAnimation(OpacityProperty, null);
        Progress.Opacity = 0;
        ResetProgress();
    }

    private void ResetProgress()
    {
        var fill = (ScaleTransform)ProgressFill.RenderTransform;
        fill.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        fill.ScaleX = 0;
        Motion.Stop(ProgressGleam.RenderTransform, TranslateTransform.XProperty);
    }

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private sealed class TabHeader
    {
        internal required Grid Root { get; init; }
        internal required TextBlock Label { get; init; }
        internal required Image Favicon { get; init; }
        internal required TextBlock Glyph { get; init; }
        internal required Path Spinner { get; init; }
        internal required Button Audio { get; init; }
        internal required Button Close { get; init; }
        internal required TextBlock Pin { get; init; }
    }

    private TabHeader CreateTabHeader(TabItem item, BrowserTab tab)
    {
        var spinner = new Path
        {
            Data = Geometry.Parse("M7,1.5 A5.5,5.5 0 1 1 1.5,7"),
            Stroke = Res("AccentBrush"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 14,
            Height = 14,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
            Visibility = Visibility.Collapsed,
        };
        var favicon = new Image { Width = 16, Height = 16, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(favicon, BitmapScalingMode.HighQuality);
        var glyph = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var icon = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 9, 0) };
        icon.Children.Add(glyph);
        icon.Children.Add(favicon);
        icon.Children.Add(spinner);

        var label = new TextBlock
        {
            Text = tab.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
        };
        var close = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "",
            Width = 22,
            Height = 22,
            FontSize = 8,
            Margin = new Thickness(4, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "탭 닫기");
        close.Click += (_, _) => tab.Owner.CloseTab(item);
        // Speaker shown only while the page plays audio; click toggles mute.
        var audio = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "",
            Width = 22,
            Height = 22,
            FontSize = 10,
            Margin = new Thickness(4, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "탭 음소거",
        };
        System.Windows.Automation.AutomationProperties.SetName(audio, "탭 음소거 전환");
        audio.Click += (_, _) => tab.Owner.ToggleMute(tab);

        var root = new Grid { Width = 0, ClipToBounds = true };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 1);
        Grid.SetColumn(audio, 2);
        Grid.SetColumn(close, 3);
        root.Children.Add(icon);
        root.Children.Add(label);
        var pin = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(pin, 1);
        root.Children.Add(pin);
        root.Children.Add(audio);
        root.Children.Add(close);
        item.Header = root;
        return new TabHeader { Root = root, Label = label, Favicon = favicon, Glyph = glyph, Spinner = spinner, Audio = audio, Close = close, Pin = pin };
    }

    private void SetTabLoading(BrowserTab tab, bool loading)
    {
        tab.Loading = loading;
        var spinner = tab.Header.Spinner;
        spinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        tab.Header.Glyph.Visibility = loading || tab.Header.Favicon.Source is not null ? Visibility.Collapsed : Visibility.Visible;
        tab.Header.Favicon.Visibility = !loading && tab.Header.Favicon.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        var rotate = (RotateTransform)spinner.RenderTransform;
        if (loading) Motion.Loop(rotate, RotateTransform.AngleProperty, 0, 360, 0.8);
        else
        {
            Motion.Stop(rotate, RotateTransform.AngleProperty);
            if (tab.Header.Favicon.Visibility == Visibility.Visible)
                Motion.Enter(tab.Header.Favicon, 0, scale: 0.4, ms: 500, ease: Motion.Spring);
        }
        if (Active == tab)
        {
            if (loading) StartProgress();
            else FinishProgress();
        }
    }

    private async Task UpdateFavicon(BrowserTab tab)
    {
        if (!AcceptTabEvents(tab)) return;
        long epoch = tab.NavigationEpoch;
        long request = ++tab.FaviconRequest;
        ImageSource? source = null;
        try
        {
            using var stream = await tab.EngineView.GetFaviconPngAsync();
            if (stream is { Length: > 0 })
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                if (image.CanFreeze) image.Freeze();
                source = image;
            }
        }
        catch (Exception error) when (RecoverableFailure.Is(error)) { }
        if (!AcceptTabEvents(tab) || tab.NavigationEpoch != epoch || tab.FaviconRequest != request) return;
        tab.Header.Favicon.Source = source;
        SetTabLoading(tab, tab.Loading);
    }

    private void TabHostSizeChanged(object sender, SizeChangedEventArgs args) => LayoutTabs(animate: false);

    /// <summary>Tabs share the strip evenly; width changes glide instead of jumping.</summary>
    private void LayoutTabs(TabItem? entering = null, bool animate = true)
    {
        var items = Tabs.Items.Cast<TabItem>().Where(item => !closingTabs.Contains(item)).ToList();
        if (items.Count == 0) return;
        double available = TabHost.ActualWidth - NewTab.ActualWidth - NewTab.Margin.Left - 12;
        const double pinnedWidth = 84;
        int pinned = items.Count(item => item.Tag is BrowserTab { Pinned: true });
        int normal = Math.Max(1, items.Count - pinned);
        double width = available > 0 ? Math.Clamp((available - pinned * (pinnedWidth + 4)) / normal - 4, MinTabWidth, MaxTabWidth) : MaxTabWidth;
        foreach (var item in items)
        {
            double target = (item.Tag is BrowserTab { Pinned: true } ? pinnedWidth : width) - 18;
            var root = (FrameworkElement)item.Header;
            if (item == entering && Motion.Enabled)
            {
                var grow = Motion.Keys(0, target, 0, 520, Motion.Expo);
                grow.FillBehavior = FillBehavior.HoldEnd;
                root.BeginAnimation(WidthProperty, grow);
                Motion.Enter(item, 60, dy: 10, ms: 600);
            }
            else if (animate && Motion.Enabled)
                root.BeginAnimation(WidthProperty, Motion.To(target, 300, Motion.Expo));
            else
            {
                root.BeginAnimation(WidthProperty, null);
                root.Width = target;
            }
        }
    }

    /// <summary>Collapses a tab header before it is removed. Returns false if already closing.</summary>
    private async Task<bool> AnimateTabClose(TabItem item)
    {
        if (!closingTabs.Add(item)) return false;
        try
        {
            if (Motion.Enabled)
            {
                var root = (FrameworkElement)item.Header;
                root.BeginAnimation(WidthProperty, Motion.To(0, 200, Motion.Out));
                item.BeginAnimation(OpacityProperty, Motion.To(0, 140));
                LayoutTabs();
                await Task.Delay(210);
            }
            return true;
        }
        finally { closingTabs.Remove(item); }
    }

    // ── Small acknowledgements ──────────────────────────────────────────────

    private void AnimateStatus()
    {
        Motion.Enter(Status, 0, dy: 7, ms: 420);
        if (Motion.Enabled)
            Motion.Pop(Motion.Transforms(StatusDot).Scale, 1.8);
    }

    private void UpdateBookmarkGlyph()
    {
        bool saved = Active is { } tab && IsWebUrl(tab.Url) && data.Bookmarks.Any(entry => entry.Url == tab.Url);
        BookmarkGlyph.Text = saved ? "" : "";
        BookmarkGlyph.Foreground = saved ? new SolidColorBrush((Color)FindResource("CyanColor")) : Res("TextBrush");
    }

    private void MenuOpened(object sender, RoutedEventArgs args)
    {
        var menu = (ContextMenu)sender;
        Motion.Enter(menu, 0, dy: -8, scale: 0.94, ms: 380, origin: new Point(1, 0));
        int index = 0;
        foreach (var entry in menu.Items.OfType<MenuItem>())
            Motion.Enter(entry, 40 + index++ * 28, dx: 14, ms: 420);
    }

    private void AddressGotFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args) =>
        Motion.Enter(AddressHost, 0, scale: 1.015, ms: 380, ease: Motion.Spring);

    private void ShowFindBar(bool show)
    {
        if (show)
        {
            FindBar.Visibility = Visibility.Visible;
            FindBar.BeginAnimation(HeightProperty, Motion.To(44, 320, Motion.Expo));
            Motion.Enter(FindContent, 60, dy: -10, ms: 420);
            return;
        }
        var collapse = Motion.To(0, 220, Motion.Out);
        collapse.Completed += (_, _) => { if (FindBar.Height == 0 || !Motion.Enabled) FindBar.Visibility = Visibility.Collapsed; };
        FindBar.BeginAnimation(HeightProperty, collapse);
        if (!Motion.Enabled) FindBar.Visibility = Visibility.Collapsed;
    }
}
