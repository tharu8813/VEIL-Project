using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Veil.Desktop;

internal sealed class VaultPdfPreviewDialog : VeilDialog
{
    private readonly CancellationTokenSource cancel = new();
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Button previous, next;
    private readonly DispatcherTimer guard;
    private readonly Func<bool> ready;
    private readonly byte[] bytes;
    private int index, count;
    private bool busy, closed;

    internal VaultPdfPreviewDialog(Window owner, string name, byte[] bytes, Func<bool> ready)
        : base(owner, "PDF 미리보기 · 개발용", "", 880, 720)
    {
        this.bytes = bytes;
        this.ready = ready;
        Subtitle.Text = "읽기 전용 · 페이지 이미지로 표시";
        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8) };
        var root = new DockPanel();
        DockPanel.SetDock(label, Dock.Top);
        root.Children.Add(label);
        root.Children.Add(new Border { Background = Res("SurfaceBrush"), Padding = new Thickness(8), Child = image });
        SetBody(root);
        previous = AddFooterButton("이전 페이지", () => _ = ShowPageAsync(index - 1));
        next = AddFooterButton("다음 페이지", () => _ = ShowPageAsync(index + 1));
        AddFooterButton("닫기", Dismiss, primary: true);
        previous.IsEnabled = next.IsEnabled = false;
        guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        guard.Tick += (_, _) => { if (!ready()) Close(); };
        Loaded += (_, _) => { guard.Start(); _ = ShowPageAsync(0); };
        Closed += (_, _) =>
        {
            closed = true;
            guard.Stop();
            cancel.Cancel();
            image.Source = null;
            if (!busy) cancel.Dispose();
        };
    }

    private async Task ShowPageAsync(int requested)
    {
        if (busy || closed || !ready() || requested < 0 || requested >= VaultPdfPreview.MaxPages ||
            (count != 0 && requested >= count)) return;
        busy = true;
        previous.IsEnabled = next.IsEnabled = false;
        image.Source = null;
        Subtitle.Text = "페이지를 렌더링하고 있습니다.";
        try
        {
            var page = await VaultPdfPreview.RenderAsync(bytes, requested, cancel.Token);
            if (closed || !ready()) return;
            index = requested;
            count = page.Count;
            image.Source = page.Bitmap;
            Subtitle.Text = $"{index + 1} / {count} 페이지 · 읽기 전용";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException or
            System.Runtime.InteropServices.COMException or NotSupportedException or FormatException)
        {
            if (!closed) Subtitle.Text = "페이지를 표시하지 못했습니다. 암호화된 PDF, 손상된 문서 또는 제한 초과일 수 있습니다.";
        }
        finally
        {
            busy = false;
            if (closed) cancel.Dispose();
            else
            {
                previous.IsEnabled = index > 0 && ready();
                next.IsEnabled = index + 1 < count && ready();
            }
        }
    }
}
