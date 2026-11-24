# Phase 0 기술 검증

2026-11-23 · 기준 커밋 `5868126` 및 해당 작업 트리 · Windows 11 빌드 26300 x64 · Node 26.13.1 · .NET SDK 10.0.403 · Rust 1.99.1 및 MSVC 14.51 확인

## 실측

실제 브라우저 프로세스로 루프백 서버에 접속했다. 두 엔진 모두 새 전용 프로필에서 쓰기 → 프로세스 정상 종료 → 같은 프로필과 원점에서 재실행 → 읽기 → 별도 실행에서 GET 취소 순서로 진행했다. WebView2는 다운로드의 `Interrupted`와 브라우저 프로세스 종료 이벤트를 관찰했다. Qt는 `DownloadCancelled`를 관찰했다.

| 항목 | WebView2 157.0.4341.60 / SDK 1.0.2903.40 | PySide6 6.11.1 / Chromium 140.0.7339.225 / Python 3.14.10 |
|---|---|---|
| P-01 영구 HttpOnly 쿠키, LocalStorage, IndexedDB | PASS, 재실행 후 3종 복원 | PASS, 재실행 후 3종 복원 |
| P-02 Service Worker, CacheStorage | PASS, 재실행 후 2종 복원 | PASS, 재실행 후 2종 복원 |
| E-02 저장소 파일의 마커 조사 | 평문 발견. `EBWebView/Default/` 아래 Local Storage, IndexedDB, HTTP Cache, Service Worker CacheStorage 및 ScriptCache | 평문 발견. `storage/` 아래 Cookies, Local Storage, IndexedDB, Service Worker CacheStorage 및 ScriptCache, 별도 `cache/` |
| D-01 일반 GET 취소 | PASS, 종료 후 지정 프로필에서 다운로드 이름 및 `.crdownload` 0개 | PASS, 종료 후 지정 프로필에서 다운로드 이름 및 `.crdownload` 0개 |
| P-03 잠긴 저장소·E-02 쓰기 추적 | NOT RUN. 보호 백엔드, OS 임시 경로 및 하위 프로세스 추적 필요 | NOT RUN |
| P-04 강제 종료 복구 | 제한된 PASS. 사이트 데이터 쓰기 성공 직후 호스트 강제 종료 후 프로필 5종 복원 | NOT RUN |
| E-03 다운로드 응답 스트림 | 기본 GET 응답에서 `null`. 실험 옵션 사용 시 취소된 일반 GET에서 본문 해시 일치. 보호 금고 스트리밍 및 일시 쓰기 추적은 NOT RUN | NOT RUN. 공개 다운로드 API는 저장 경로/취소 제어를 제공 |
| N-01 로컬 HTTP 프록시 장애 | IPv4·IPv6 루프백 원점 각각 제한된 PASS. 직접 접속 대조군 성공, 정상 경유 1건, 리스너 종료 후 원본 직접 요청 0건 | NOT RUN |
| N-02 DNS/WebRTC/UDP/QUIC 및 외부 IPv6 경로 | NOT RUN | NOT RUN |
| E-04 샌드박스 토큰 감사 | NOT RUN. 별도 렌더러 프로세스만 관찰 | NOT RUN |

두 기본 프로필은 Gate A의 구현 후보에서 제외한다. 마커가 발견된 경로에는 별도 암호화 계층이 필요하다. 검색 범위에 없던 Cookies 파일의 값, TEMP·로그·덤프·페이지 파일, 순간적으로 생성된 다운로드 임시 파일의 보호 여부는 이 실험으로 알 수 없다.

처음 제한된 도구 실행에서는 WebView2 탐색이 `Unknown`으로 실패했고 Qt는 프로필 생성에서 `0x80000003`으로 종료했다. 일반 사용자 권한에서 같은 시험을 재실행해 위 결과를 얻었다. 엔진 샌드박스 설정과 Windows 보안 설정은 변경하지 않았다. 실행기와 증거 위치는 [개발 및 테스트 가이드](DEVELOPMENT.md)에 기록한다.

`node tests/phase0/run.mjs`의 대표 결과는 `.veil-dev/phase0-pbURdK/evidence.json`에, `node tests/phase0/proxy.mjs`의 엔진 결과는 `.veil-dev/proxy-nfwyzw/evidence.json`에 저장했다. 파일은 작업 트리의 무시 경로이며 실행하면 새로 생성된다.

다운로드 응답 API에는 런타임별 기능 플래그 및 콘텐츠 크기 제한이 있다. 기본 설정에서 취소한 GET은 `null`이었다. 별도 실험 프로세스에서 기능 플래그를 요청한 경우 동일한 합성 GET의 본문 SHA-256이 원본과 일치했고 다운로드 이벤트는 `Interrupted`였다. 대표 증거는 `.veil-dev/phase0-BygqRD/evidence.json`에 있다. 보호 경로 후보를 재평가하려면 인증·POST·Blob·Service Worker 의미 보존, 크기 제한 밖 처리, 실시간 디스크 쓰기를 함께 검증해야 한다. [GetContentAsync API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2webresourceresponseview.getcontentasync?view=webview2-dotnet-1.0.4022.49)

프록시 시험에서는 IPv4로 수신하는 고정 로컬 프록시가 합성 응답을 보낸 직후 리스너를 닫았다. 프록시는 요청을 외부로 전달하지 않는다. Chromium의 암묵적 루프백 예외를 빼는 옵션은 이 실험에만 사용했다. `node tests/phase0/proxy.mjs`와 `--ipv6`는 프록시 없이 직접 접속하는 대조군을 포함하며 각각 `127.0.0.1`, `::1` HTTP 원점을 대상으로 한다. HTTPS CONNECT, DNS, 외부 IPv6 경로, WebRTC, UDP/QUIC, 절전 복귀 및 OS 수준 경로 제한은 미검증이다. [Chromium 프록시 규칙](https://chromium.googlesource.com/chromium/src/+/main/net/docs/proxy.md#overriding-the-implicit-bypass-rules)

2026-11-23 추가 시험에서 `node tests/phase0/browser-crash.mjs`로 쓰기 성공 직후 합성 호스트를 강제 종료했다. 브라우저 프로세스는 종료될 때까지 관찰하고 같은 원점과 프로필에서 재실행했다. 영구 쿠키, LocalStorage, IndexedDB, Service Worker, CacheStorage 모두 복원됐다. 대표 결과는 `.veil-dev/browser-crash-hPIdR2/evidence.json`에 있다. 렌더러 직접 강제 종료, 트랜잭션 도중 종료, 전원 차단, Qt 대응 시험은 미실행이다.

## 나머지 엔진과 보호 방식

| 후보 | 자료상 가능 범위와 제약 | 실측 |
|---|---|---|
| Qt C++ | PySide6와 같은 Qt API로 저장 경로·쿠키 정책·다운로드 경로를 제어한다. 브라우저 본문을 보호 저장소로 보내는 경로는 추가 실증이 필요하다. [프로필](https://doc.qt.io/qt-6.11/qwebengineprofile.html) · [다운로드](https://doc.qt.io/qt-6.10/qwebenginedownloadrequest.html) | NOT RUN |
| CEF | Chromium 브랜치 추적 및 자체 빌드가 가능하다. 보호 저장소 연결과 샌드박스 통합에 드는 비용을 측정해야 한다. [CEF 브랜치와 빌드](https://github.com/chromiumembedded/cef/blob/master/docs/branches_and_building.md) | NOT RUN |
| Chromium 포크 | 프로필 경로를 설정할 수 있고 소스 수준 변경이 가능하다. 저장/다운로드 계층 수정, 패치 추적 및 재빌드 비용이 크다. [데이터 경로](https://chromium.googlesource.com/chromium/src/+/master/docs/user_data_dir.md) · [샌드박스](https://chromium.googlesource.com/chromium/src/+/main/docs/design/sandbox.md) | NOT RUN |
| WebView2 | UDF 경로 및 다운로드 취소·대상 경로 변경을 지원한다. 전체 저장소 암호화와 본문 스트림 제어는 공개 API에서 확인하지 못했다. [UDF](https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createasync) · [다운로드 이벤트](https://learn.microsoft.com/microsoft-edge/webview2/reference/win32/icorewebview2downloadstartingeventargs) | 위 표 참조 |
| Python + Qt | 프로필 및 캐시 경로를 설정하고 다운로드 수용·취소와 디스크 대상을 제어한다. 다운로드 응답 바이트를 직접 받는 공개 API는 확인하지 못했다. [프로필](https://doc.qt.io/qt-6.11/qwebengineprofile.html) · [다운로드](https://doc.qt.io/qt-6.10/qwebenginedownloadrequest.html) | 위 표 참조 |

암호화 볼륨, 엔진 내 저장 계층, 독립 DB 암호화의 역할과 제약은 [ADR-0001](decisions/ADR-0001-developer-engine.md)에 기록했다. WFP는 별도 시험 환경에서 권한과 복구 절차를 갖춘 뒤 검증 대상으로 삼는다. [WFP 범위](https://learn.microsoft.com/windows/win32/fwp/about-windows-filtering-platform)
