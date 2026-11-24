using System.ComponentModel;
using System.Diagnostics;

namespace Veil.Desktop;

/// <summary>Transient state for a download, shared by the profile's windows.</summary>
internal sealed class BrowserDownload(string name) : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private long lastUpdate;
    private long? totalBytes;

    public string Name { get; } = name;
    public string StatusText { get; private set; } = "대기 중";
    public bool CanCancel { get; private set; } = true;
    public bool IsIndeterminate => totalBytes is not > 0;
    public double Percent => totalBytes is > 0 ? Math.Clamp(100d * Bytes / totalBytes.Value, 0, 100) : 0;
    internal long Bytes { get; private set; }
    internal long? TotalBytes => totalBytes;
    internal CancellationToken Token => cancellation.Token;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Report(long bytes, long? total)
    {
        Bytes = bytes;
        totalBytes = total;
        if (lastUpdate != 0 && Stopwatch.GetElapsedTime(lastUpdate) < TimeSpan.FromMilliseconds(100)) return;
        lastUpdate = Stopwatch.GetTimestamp();
        StatusText = total is > 0
            ? $"보관 중 · {ToMiB(bytes)} / {ToMiB(total.Value)} MiB"
            : $"보관 중 · {ToMiB(bytes)} MiB";
        Refresh();
    }

    internal void Finish()
    {
        CanCancel = false;
        StatusText = "금고 저장 마무리 중";
        Refresh();
    }

    internal void Cancel()
    {
        if (!CanCancel) return;
        CanCancel = false;
        StatusText = "취소하는 중";
        cancellation.Cancel();
        Refresh();
    }

    private static string ToMiB(long bytes) => (bytes / 1048576d).ToString("0.0");
    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() => cancellation.Dispose();
}
