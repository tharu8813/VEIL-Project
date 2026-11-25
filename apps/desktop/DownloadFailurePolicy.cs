using System.IO;
using System.Net;
using System.Net.Http;

namespace Veil.Desktop;

internal enum DownloadPhase { Waiting, Request, Streaming, Publishing }

/// <summary>Only a failed initial request, before receiving a body, can offer a fresh GET.</summary>
internal sealed class DownloadRequestFailure(string reason, bool retryable) : HttpRequestException(reason)
{
    internal bool Retryable { get; } = retryable;
}

// Fixed classification from the Rust protocol; never expose arbitrary error text.
internal sealed class VaultStorageFullException() : IOException("Vault storage is full.")
{
}

internal static class DownloadFailurePolicy
{
    internal static DownloadRequestFailure HttpStatus(HttpStatusCode status, bool initial)
    {
        int code = (int)status;
        return new DownloadRequestFailure(code switch
        {
            401 or 403 => $"서버 인증 또는 접근 권한 오류 (HTTP {code})",
            404 or 410 => $"파일을 찾을 수 없음 (HTTP {code})",
            408 => "서버 요청 시간 초과 (HTTP 408)",
            429 => "서버 요청 제한 (HTTP 429)",
            >= 500 and <= 599 => $"서버 오류 (HTTP {code})",
            _ => $"서버 요청 실패 (HTTP {code})",
        }, initial && code is 408 or 429 or 500 or 502 or 503 or 504);
    }

    internal static DownloadRequestFailure Connection(HttpRequestException error, bool initial) =>
        new(error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "서버 주소 확인 실패 (DNS)",
            HttpRequestError.ConnectionError => "서버 또는 프록시 연결 실패",
            HttpRequestError.SecureConnectionError => "보안 연결 또는 인증서 검증 실패",
            HttpRequestError.ProxyTunnelError => "프록시 터널 연결 실패",
            _ => "네트워크 요청 실패",
        }, initial && error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError);

    internal static bool SafeRetryAddress(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 && uri.AbsoluteUri.Length <= 8192;

    internal static string Describe(Exception error, DownloadPhase phase, bool open)
    {
        if (!open) return "금고 열기 실패";
        bool storageFull = error is VaultStorageFullException ||
            error is IOException io && (io.HResult & 0xffff) is 0x27 or 0x70;
        // After the commit boundary, even access or session errors can leave a complete object.
        if (phase == DownloadPhase.Publishing)
            return storageFull ? "저장 공간 부족 · 보관 목록 확인 필요" :
                error is UnauthorizedAccessException ? "금고 저장소 접근 권한 오류 · 보관 목록 확인 필요" :
                "금고 저장 마무리 실패 · 보관 목록 확인 필요";
        if (error is DownloadRequestFailure failure) return failure.Message;
        if (error is UnauthorizedAccessException) return "금고 저장소 접근 권한 오류";
        if (storageFull) return "저장 공간 부족";
        if (error is OperationCanceledException) return "요청 시간 초과 또는 연결 중단";
        if (phase == DownloadPhase.Streaming) return "전송 중단 또는 암호화 저장 실패";
        if (error is System.Runtime.InteropServices.COMException) return "원래 브라우저 연결 사용 불가";
        if (error is HttpRequestException) return "네트워크 요청 실패";
        return "금고 또는 다운로드 처리 실패";
    }
}
