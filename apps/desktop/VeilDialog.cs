using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Veil.Desktop;

/// <summary>
/// Dark dialog shell shared by the library and download windows: custom title bar with a
/// glyph badge, body slot, footer slot and a short entrance sequence.
/// </summary>
internal class VeilDialog : Window
{
    private static readonly Uri ThemeUri = new("/Veil.Desktop;component/Theme.xaml", UriKind.Relative);
    private readonly Grid body = new();
    private readonly StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border shell;
    private readonly Border badge;
    protected readonly TextBlock Subtitle;
    private Action<bool>? pageCompletion;
    protected Window PromptOwner => pageCompletion is null ? this : Owner ?? this;

    // Native controls are reparented into the browser, never exposed to WebView script.
    internal FrameworkElement TakePage(Action<bool> complete)
    {
        if (pageCompletion is not null || IsVisible) throw new InvalidOperationException("Dialog already hosted.");
        pageCompletion = complete;
        Content = null;
        return shell;
    }

    protected void Complete(bool accepted)
    {
        if (pageCompletion is { } finish) finish(accepted);
        else DialogResult = accepted;
    }

    protected void Dismiss()
    {
        if (pageCompletion is { } finish) finish(false);
        else Close();
    }

    protected void OnContentLoaded(Action action) => shell.Loaded += (_, _) => action();

    protected VeilDialog(Window? owner, string title, string glyph, double width, double height)
    {
        Owner = owner;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = 380;
        MinHeight = 280;
        ShowInTaskbar = owner is null;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner is not null) FontFamily = owner.FontFamily;
        FontSize = 13;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeUri });
        Background = Res("ChromeBrush");
        Foreground = Res("TextBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 56,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(10),
            Background = Res("AccentBrush"),
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 14,
                Foreground = Res("InkBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold };
        Subtitle = new TextBlock { FontSize = 12, Foreground = Res("MutedBrush"), Margin = new Thickness(0, 1, 0, 0) };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { heading, Subtitle } };
        var close = new Button
        {
            Style = (Style)FindResource("CloseCaptionButton"),
            Content = "",
            Height = 40,
            VerticalAlignment = VerticalAlignment.Top,
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "닫기");
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Dismiss();

        var header = new DockPanel { Height = 56, Margin = new Thickness(20, 0, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { badge, titles } };
        header.Children.Add(titleRow);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Margin = new Thickness(20, 12, 20, 18);
        body.Margin = new Thickness(20, 6, 20, 0);
        layout.Children.Add(header);
        layout.Children.Add(footer);
        layout.Children.Add(body);
        shell = new Border { BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), Child = layout };
        Content = shell;

        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        SourceInitialized += (_, _) => { if (WindowCorners.Apply(this)) shell.BorderThickness = new Thickness(0); };
        Loaded += (_, _) =>
        {
            Motion.Enter(layout, 0, dy: 18, scale: 0.97, ms: 520);
            Motion.Enter(badge, 80, scale: 0.3, ms: 700, ease: Motion.Spring);
            Motion.Enter(titleRow, 120, dx: -14, ms: 600);
            Motion.Enter(body, 180, dy: 12, ms: 620);
            int index = 0;
            foreach (UIElement button in footer.Children)
                Motion.Enter(button, 260 + index++ * 50, dy: 10, ms: 520);
        };
    }

    protected Brush Res(string key) => (Brush)FindResource(key);

    protected void SetBody(UIElement element) => body.Children.Add(element);

    protected Button AddFooterButton(string label, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"),
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 0, 16, 0),
            Height = 32,
        };
        button.Click += (_, _) => action();
        footer.Children.Add(button);
        return button;
    }

    protected void AddFooterElement(FrameworkElement element)
    {
        element.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(element);
    }

    /// <summary>Rows cascade in after the list (re)populates.</summary>
    protected static void Cascade(ListBox list)
    {
        if (!Motion.Enabled) return;
        list.Dispatcher.BeginInvoke(() =>
        {
            for (int i = 0; i < Math.Min(list.Items.Count, 14); i++)
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem row)
                    Motion.Enter(row, i * 32, dx: 22, ms: 520);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Centered placeholder shown when a list has nothing to show.</summary>
    protected Border EmptyState(string glyph, string message)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 28,
            Foreground = Res("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.6,
        };
        var text = new TextBlock
        {
            Text = message,
            Foreground = Res("MutedBrush"),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        return new Border
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Children = { icon, text } },
        };
    }

    /// <summary>Two-line row: bold title over a muted detail, with an optional right-hand tag.</summary>
    protected FrameworkElement Row(string glyph, string title, string detail, string? tag = null, Brush? tagBrush = null)
    {
        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Foreground = Res("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold });
        lines.Children.Add(new TextBlock { Text = detail, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("MutedBrush"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(lines, 1);
        grid.Children.Add(icon);
        grid.Children.Add(lines);
        if (tag is not null)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = tag, FontSize = 11, Foreground = tagBrush ?? Res("MutedBrush") },
            };
            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);
        }
        return grid;
    }
}
