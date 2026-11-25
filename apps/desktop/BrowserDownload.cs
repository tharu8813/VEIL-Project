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
    public bool IsIndeterminate => !CanCancel || Phase != DownloadPhase.Streaming || totalBytes is not > 0 || Bytes >= totalBytes.Value;
    internal bool IsCancelling { get; private set; }
    public double Percent => totalBytes is > 0 ? Math.Clamp(100d * Bytes / totalBytes.Value, 0, 100) : 0;
    internal DownloadPhase Phase { get; private set; } = DownloadPhase.Waiting;
    internal long Bytes { get; private set; }
    internal long? TotalBytes => totalBytes;
    internal CancellationToken Token => cancellation.Token;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Report(long bytes, long? total)
    {
        if (!CanCancel) return;
        bool phaseChanged = Phase != DownloadPhase.Streaming || totalBytes != total ||
            (total is > 0 && Bytes < total.Value && bytes >= total.Value);
        Bytes = Math.Max(0, bytes);
        totalBytes = total;
        Phase = DownloadPhase.Streaming;
        if (!phaseChanged && lastUpdate != 0 && Stopwatch.GetElapsedTime(lastUpdate) < TimeSpan.FromMilliseconds(100)) return;
        lastUpdate = Stopwatch.GetTimestamp();
        StatusText = total is > 0 && Bytes >= total.Value ? "수신 완료 · 인증 및 저장 대기 중" : total is > 0
            ? $"보관 중 · {ToMiB(bytes)} / {ToMiB(total.Value)} MiB"
            : $"보관 중 · {ToMiB(bytes)} MiB";
        Refresh();
    }

    internal void WaitForVault()
    {
        if (!CanCancel) return;
        StatusText = "금고 설정 또는 잠금 해제 대기 중";
        Refresh();
    }

    internal void WaitForSlot()
    {
        if (!CanCancel) return;
        Phase = DownloadPhase.Waiting;
        StatusText = "다른 다운로드 완료 대기 중";
        Refresh();
    }

    internal void WaitForTransfer()
    {
        if (!CanCancel) return;
        Phase = DownloadPhase.Request;
        StatusText = "보호 다운로드 준비 중";
        Refresh();
    }

    internal void Finish()
    {
        Token.ThrowIfCancellationRequested();
        CanCancel = false;
        Phase = DownloadPhase.Publishing;
        StatusText = "금고 저장 마무리 중";
        Refresh();
    }

    internal void Cancel()
    {
        if (!CanCancel) return;
        CanCancel = false;
        IsCancelling = true;
        StatusText = "취소하는 중";
        cancellation.Cancel();
        Refresh();
    }

    private static string ToMiB(long bytes) => (bytes / 1048576d).ToString("0.0");
    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() => cancellation.Dispose();
}
