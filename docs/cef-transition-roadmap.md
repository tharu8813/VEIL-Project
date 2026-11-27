# VEIL CEF 전환 로드맵

2026-11-27 기준으로 기존 Windows x64 WPF 앱의 엔진을 WebView2에서 CEF로 전환한다. 개발에는 버전을 고정한 사전 빌드 CEF 배포본을 사용한다. 현재 제품은 WebView2로 동작한다. CEF 1단계에서 [독립 Win32 및 WPF 호스트](../apps/cef-smoke/README.md)의 로컬 JavaScript 실행, 렌더러 제한 토큰, 프로세스 종료를 확인했다. 제품 어댑터 연결과 사이트 격리 실측은 남아 있다.

2단계 준비로 개발용 CEF 어댑터를 실제 VEIL 엔진 인터페이스에 맞춰 구현했다. 별도 WPF 테스트에서 두 주소 탐색, 뒤로·앞으로, 종료, 합성 프로필 외 입력 거부를 확인했다. 기존 `BrowserWindow`의 WebView2 전용 탐색·다운로드·권한 이벤트가 분리되기 전까지 제품 시작 경로에는 등록하지 않는다.

기존 `BrowserWindow`에서 WebView2 초기화와 이벤트 등록을 별도 구현으로 이동했다. WebView2와 개발용 CEF가 공통 탐색 시작·완료 이벤트를 구현한다. CEF 루프백 테스트에서 HTTP 전 취소, 리다이렉트 탐색 ID 유지, 성공과 오류 완료, 방문 기록 복원과 종료를 확인했다. 개발용 CEF의 기본 거부 핸들러는 루프백 HTTP 첨부, Blob 다운로드, 위치 권한, 가상 카메라 요청으로 검증했다. 제품 금고와 권한 정책 연결, 임시 파일 실측, 겹치는 탐색 검증은 남아 있다.

응답 필터는 일반 루프백 응답의 96 KiB를 원본 요청에서 모두 관찰했다. 지연 전송한 96 KiB 첨부를 차단하면 두 차례 모두 16 KiB만 관찰됐다. 금고 연결에는 차단된 다운로드의 관찰 데이터와 별도로, 원본 응답을 끝까지 소유하는 경로와 완전성 검증이 필요하다. 테스트 전용 임시 폴더에서 첨부 이름과 `.crdownload` 이벤트 및 잔여 파일은 관찰되지 않았다. OS 전체 쓰기 추적은 남아 있다.

개발용 `CefURLRequest`가 직접 시작한 루프백 GET 첨부를 최대 16 KiB 조각으로 나눠 8개 슬롯의 큐를 통해 기존 Rust 금고 프로토콜로 스트리밍했다. 금고 소비를 일시 정지해 큐의 슬롯 8개를 채웠을 때, CEF가 요청 취소를 보고했고 Rust 금고에 완성 객체가 남지 않았다. 정상 응답 768 KiB는 암호화 객체 하나를 발행했고 복호화 결과가 원본과 일치했다. 중간에 끊긴 응답은 일부 바이트가 금고에 도달한 뒤 중단됐고 완성 객체를 발행하지 않았다. 세 요청은 각각 한 번만 도달했다. 정상 응답은 루프백 페이지의 지정 링크를 스크립트로 클릭해 시작한다. CEF의 OnBeforeBrowse가 브라우저 탐색을 HTTP 전 취소한 뒤 별도 CefURLRequest로 같은 주소에 GET 한 건을 보낸다. 테스트 서버는 인계 요청만 도착했는지 확인한다. 일반 페이지 다운로드, 인증 정보가 필요한 요청 및 제품 금고 연결은 남아 있다.

## 구현 순서와 완료 기준

| 단계 | 작업 | 완료 기준 |
|---|---|---|
| 1. Windows 호스트 검증 | 공식 CEF x64 배포본의 sandbox bootstrap으로 브라우저와 자식 프로세스를 시작하는 최소 네이티브 호스트를 만든다. WPF에서 창을 호스팅하고 UI 스레드·메시지 루프·종료 방식을 검증한다. | 일반 사용자로 로컬 HTML·JavaScript 실행, 렌더러 토큰·사이트 분리 관찰, 종료 시 자식 프로세스 정리. 패키지 버전·해시 고정. |
| 2. 기존 앱과 연결 | 명시적인 개발 빌드에서 `IBrowserEngine`과 `IBrowserEngineView`에 CEF 어댑터를 연결한다. WebView2에 직접 연결된 탭·팝업 이벤트를 필요한 만큼 분리한다. | 합성 프로필로 실제 VEIL 창에서 탭 하나, 주소창, 뒤로·앞으로, 오류 처리와 종료 검증. 초기화 실패 시 앱이 이유를 표시하고 종료. |
| 3. 브라우저 기능과 격리 | 다중 창, 비공개 탭, 내부 페이지, 권한, 즐겨찾기와 기록을 연결한다. Rust 코어와 볼륨 브로커를 재사용해 CEF 초기화 전에 보호 세션을 확립한다. | IME·DPI·접근성 회귀 통과. 보호 볼륨이 없으면 시작 거부. 캐시·임시 파일·로그·덤프의 쓰기 위치와 잠금·복구 검증. |
| 4. 다운로드와 네트워크 | CEF 콜백과 응답 경로를 조사해 금고로 실제 응답 바이트를 전달하는 방식을 검증한다. 프록시·DNS·HTTPS·WebRTC·QUIC, 쿠키·권한, 위협 보호를 연결한다. | 지원 요청 유형별 암호화·취소·복구 검증. 미지원 경로는 차단. 프록시 또는 보호 세션이 끊겼을 때 직접 연결과 보호 볼륨 밖 쓰기가 없는지 실측. |
| 5. 이전과 배포 | 기존 프로필과 분리된 CEF 프로필에 설정·즐겨찾기·기록 등 지원 데이터를 명시적으로 이전한다. 런타임 패키징, 서명, 라이선스, SBOM, 업데이트 및 롤백을 검증한다. | 원본 보존·복구 확인, 깨끗한 Windows 설치와 업데이트 검증, Gate A~E 통과. 내부 Alpha와 회귀 검증을 거친 뒤 기본 엔진 전환 및 WebView2 의존성 제거. |

## 첫 기술 결정

공식 CEF Windows Sandbox 문서는 M138부터 사전 빌드된 bootstrap 실행 파일로 클라이언트 DLL을 로드하고, 같은 실행 파일이 브라우저·자식 프로세스를 담당하는 구성을 설명한다. 이 구성을 1단계에서 먼저 실측한다. CefSharp는 WPF 연동 사례가 있지만 프로젝트 문서가 CEF 샌드박스 미지원으로 명시하므로, VEIL 제품의 기본 어댑터로 선정하지 않는다. [CEF Windows 샌드박스 문서](https://github.com/chromiumembedded/cef/blob/master/docs/sandbox_setup.md) · [CefSharp 배포 안내](https://github.com/cefsharp/CefSharp/blob/master/NuGet/Readme.txt)

브라우저 컨트롤의 렌더링만으로 보안 통과를 판정하지 않는다. 사용자 데이터는 2단계 합성 검증에 사용하지 않는다. 현재 Windows x64 배포 버전은 1단계 시작 시 확인한다. [CEF 공식 문서](https://github.com/chromiumembedded/cef/blob/master/docs/README.md) · [VEIL 출시 게이트](11-security-release-gates.md)

### 추가 구현: 탭 파비콘 및 오디오 경계 (검증 대기)

공통 뷰 계약에 엔진이 제공하는 파비콘 PNG, 음소거 상태/설정 및 재생 상태를 추가했다. WebView2는 기존 엔진 API를 사용하고, 비동기 파비콘 결과는 탭 소유자·탐색 epoch·요청 번호가 일치하는 경우만 표시한다. 개발용 CEF는 네이티브 SetAudioMuted/IsAudioMuted를 연결하지만 실제 재생 감지 및 캐시 파비콘 추출은 미지원(null)으로 유지한다. 별도 파비콘 URL 재요청 또는 오디오 캡처는 추가하지 않았다. CEF 네이티브 빌드/실행은 NOT RUN이며 제품 엔진 전환 조건은 그대로 유지한다.

### 추가 구현: 프로세스 오류 경계 (검증 대기)

공통 뷰의 ProcessFailed 이벤트는 페이지/자식 프로세스 오류와 브라우저 세션 손실을 구분한다. WebView2의 BrowserProcessExited만 세션 저장 및 모든 창 종료 경로로 전달하며, 나머지는 기존 페이지 오류 안내를 유지한다. 개발용 CEF는 OnRenderProcessTerminated에서 미완료 탐색을 해제하고 페이지 오류만 전달한다. 정상 OnBeforeClose는 오류로 보고하지 않는다. CEF와 WPF가 같은 프로세스에서 실행되므로 호스트 자체의 강제 종료는 내부 콜백으로 감지할 수 없으며 외부 감시·보호 볼륨 복구 실측이 필요하다. 정상 종료 시 오류 이벤트가 없음을 확인하는 테스트 및 정적 회귀 검사를 추가했다. 실제 렌더러 충돌 주입, CEF 네이티브 컴파일/실행은 NOT RUN이다. 기존 제품 경로와 보안 게이트는 변경하지 않는다.

### 추가 구현: 팝업·웹 페이지 닫기 요청 경계 (검증 대기)

WebView2 팝업은 공통 비동기 요청을 통해 기존의 새 탭 인계 방식으로 처리한다. 원본 탭 생존·탐색 epoch, 대상 탭 생존, 동일 엔진 환경/프로필 조건을 유지하고 인계가 실패하면 대상 탭을 닫는다. 요청은 한 번만 사용 가능하며 네이티브 deferral 종료 때 무효화한다. 웹 페이지 닫기 요청도 지연 실행 전후 탭과 epoch를 확인한다. CEF 개발 호스트는 단일 뷰이므로 OnBeforePopup에서 모든 팝업을 거부하며 대상 HTTP 요청이 발생하지 않는 회귀 테스트를 추가했다. 합성 테스트에서만 Chromium의 선행 팝업 차단을 해제하여 호스트 차단을 검증한다. 일반 CEF 팝업 인계·웹 페이지 닫기 요청 및 실제 Windows 네이티브 실행은 미지원/NOT RUN. 제품 보안 경로는 변경하지 않는다.

### 추가 구현: 페이지 전체화면 상태 경계 (검증 대기)

공통 뷰에 읽기 전용 페이지 전체화면 상태 및 변경 이벤트를 추가한다. 활성 상태의 살아 있는 외부 웹 탭만 창 전체화면 변경을 요청할 수 있고 탭 전환 시 기존 페이지 전체화면 복원 조건을 유지한다. WebView2의 기존 이벤트를 어댑터로 이동하고 CEF 개발 호스트에는 OnFullscreenModeChange 상태 전달을 추가한다. CEF의 실제 사용자 제스처, 전체화면 전환 및 종료, 다중 탭 동작은 NOT RUN이며 제품 엔진 선택·권한 정책은 바꾸지 않는다.

### 추가 구현: 탐색 오류 코드 영역 분리 (검증 대기)

WebView2와 CEF의 네이티브 오류 코드를 엔진 종류와 함께 저장하며 서로의 열거형으로 변환하지 않는다. 성공 및 취소의 오류 상태는 null로 유지하고 기존 WebView2 진단 문자열 및 HTTPS 실패 경고 조건을 보존한다. CEF 합성 HTTP 오류 검증과 정적 회귀 검사에 코드 영역을 추가했다. 실제 Windows 런타임 결과는 NOT RUN이며 제품 엔진 선택과 보안 정책을 변경하지 않는다.

### 추가 구현: 강력 새로고침 및 페이지 검색 경계 (검증 대기)

강력 새로고침은 WebView2의 기존 고정 CDP 호출과 CEF의 ReloadIgnoreCache를 좁은 공통 동작으로 연결한다. 일반 새로고침 fallback은 동일 탭·탐색·선택 상태가 유지될 때만 허용한다. 페이지 검색은 WebView2의 기존 JSON 인코딩 window.find 동작을 어댑터 내부에 두고 공통 nullable 결과를 반환한다. CEF 네이티브 검색 콜백 및 결과 수명 관리는 아직 미지원(null)으로 표시한다. 기존 결과 무효화 검사와 정적 회귀 검사를 유지하며 실제 Windows 실행은 NOT RUN이다. 제품 엔진과 보안 경로는 변경하지 않는다.

### 추가 구현: CEF 뷰 소유 상태 (검증 대기)

다중 인스턴스 준비 1단계로 주소·제목·전체화면 및 관리 콜백을 CEF Client가 소유하는 ViewState로 이동한다. 생성 시 콜백을 한 번에 등록하며 실패 시 해당 상태만 해제하고, HWND 소유자가 일치할 때만 외부 해제를 허용한다. 정상 닫기에서 같은 상태의 콜백만 제거한다. 아직 단일 브라우저 제한과 합성 다운로드 전역 상태를 유지하며 다중 브라우저 핸들 및 동시 실행은 다음 단계다. 실제 Windows 실행 NOT RUN, 제품 경로 및 보호 정책 유지.

### 추가 구현: CEF 뷰 고유 핸들 (검증 대기)

단일 뷰의 네이티브 브라우저 제어 및 조회 API에 0이 아닌 단조 증가 uint64_t 식별자를 도입했다. 잘못되거나 만료된 식별자는 다른 뷰로 대체하지 않는다. HWND는 크기 조정 및 콜백 해제에서 함께 검증한다. C# 어댑터는 생성 시 받은 식별자를 명령에 전달하고 창 해제 시 무효화한다. 합성 금고 요청도 유효한 뷰를 요구하지만 계측 및 다운로드 상태는 여전히 단일 뷰 전용이다. 잘못된 ID의 조회 및 음소거 명령을 확인하는 합성 검사와 정적 회귀 검사를 추가했다. 실제 다중 뷰 레지스트리·동시 탭·Windows 실행은 NOT RUN/미구현이며 제품 경로와 보안 정책을 유지한다.

### 추가 구현: 네이티브 뷰 레지스트리 (검증 대기)

고유 ID별 ViewEntry(상태와 브라우저 참조)를 UI 스레드 전용 레지스트리로 관리한다. 생성 전에 ID를 예약하고, 동기 생성 콜백 재진입 이후 같은 항목이 살아 있는 경우만 브라우저 참조를 등록한다. 닫기/실패 시 정확히 같은 상태의 항목만 제거한다. 다중 항목 구조는 마련했으나 합성 다운로드 상태 및 관리 종료 흐름이 분리되기 전에는 kDevelopmentViewLimit=1을 유지한다. 실제 동시 탭 및 Windows 실행은 NOT RUN이다. 제품 경로 및 보안 정책을 변경하지 않는다.

### 추가 구현: 뷰별 합성 다운로드 계측 및 인계 상태 (검증 대기)

팝업·다운로드·권한 차단 계수와 응답 필터 결과, 페이지 첨부 인계 상태를 ViewState에 이동한다. IO 스레드에서 완료되는 필터는 원본 뷰 상태를 공유 소유하며 원자 변수로 결과를 기록한다. 조회 및 첨부 인계는 해당 뷰 ID를 사용한다. 실제 CefURLRequest와 Rust 스트림 콜백 수명은 별도 단계이며 기존 단일 뷰 제한을 유지한다. Windows 실행 NOT RUN, 제품 보안 경로 유지.

### 추가 구현: 뷰별 합성 암호화 스트림 수명 (검증 대기)

각 합성 다운로드마다 별도 OwnedTransfer를 만들고 원본 ViewState에서 소유한다. 요청 클라이언트는 약한 참조로 이전 전송을 식별하며 완료 또는 취소 뒤 도착하는 콜백을 무시한다. 종료 또는 명시 취소에서 콜백을 먼저 무효화한 후 네이티브 요청을 취소하며 관리 호스트도 해당 뷰의 Rust 스트림을 실패로 종료한다. 기존 크기·응답 헤더·리다이렉트 차단 및 단일 뷰 제한을 유지한다. 실제 Windows 실행 및 동시 다운로드 NOT RUN; 제품 보안 경로 유지.

### 추가 구현: 뷰 종료 및 호스트 종료 게이트 (검증 대기)

뷰의 실제 종료는 OnBeforeClose의 레지스트리 제거로 판정한다. WPF는 최초 종료 ID를 보존하고 해당 ID가 제거되며 전체 레지스트리가 비고 Rust fixture 작업이 완료된 뒤에만 dispatcher를 종료한다. HwndHost 해제는 해당 뷰의 콜백과 전송을 취소하고 필요한 경우 해당 브라우저만 강제 닫는다. 예외적인 관리 호스트 반환 후에는 남은 브라우저를 닫고 최대 5초간 메시지 루프를 처리하며, 레지스트리가 비지 않으면 CefShutdown을 호출하지 않고 오류 27로 비정상 종료한다. 단일 뷰 제한과 보안 정책 유지. Windows 실제 실행, 다중 뷰 및 beforeunload 사용자 취소 동작은 NOT RUN.

### 추가 구현: 합성 두 뷰 동시 실행 fixture (검증 대기)

일반 개발 호스트는 1개, 기존 로컬 HTTP 자동 fixture만 최대 2개 뷰를 허용하며 관리 어댑터도 네이티브 한도를 따른다. 동일 합성 프로필의 두 WPF 창을 동시에 생성하여 서로 다른 URL/제목, 탐색 이력, 음소거, ID 및 상태를 비교하고 두 번째 뷰를 닫은 후 만료된 ID가 첫 뷰에 영향을 주지 않는지 검사한다. 첫 뷰는 기존 탐색 및 금고 검사를 이어간다. 관리 어댑터의 생성 수는 엔진 수명 동안 최대 2개로 제한(재생성 슬롯은 후속). 실제 Windows 실행 NOT RUN이며 쿠키/프로필 격리 또는 동시 다운로드 검증을 의미하지 않는다. 제품 WebView2, 사설 프로필·외부 URL·네트워크 제한 유지.

### 추가 구현: 관리 뷰 생성 슬롯 재사용 (검증 대기)

관리 엔진의 누적 생성 제한을 예약 목록으로 전환한다. 미연결 뷰도 슬롯을 점유하며 Dispose 후 네이티브 종료 확인이 완료된 경우에만 반환한다. HWND 해제 후 이전 ID를 보존하고 이전 브라우저가 남아 있으면 재연결을 거부한다. UI dispatcher 접근을 확인하고 네이티브 한도도 유지한다. 합성 fixture는 미연결 예약 반환, 두 번째 뷰 종료 및 새 ID 재생성, 만료 ID의 탐색/음소거 무효, 반복 종료 후 기본 뷰 유지 여부를 검사한다. 실제 Windows 실행 NOT RUN; 일반 개발 호스트 1개·합성 fixture 2개 한도, 제품 보안 경로 유지.

### 추가 구현: 합성 동시 다운로드 취소 격리 fixture (검증 대기)

두 활성 뷰에서 각각 합성 전용 동일 출처 HTTP 요청과 별도 Rust 금고 프로세스를 시작한다. 서버는 각 요청의 첫 조각을 보낸 뒤 취소 대상 연결이 닫힐 때까지 정상 요청의 나머지 본문을 보류한다. 양쪽 수신과 진행 중 상태 확인 후 보조 뷰 전송만 취소하고 닫으며, 정상 뷰의 전송 완료·복원, 취소 이후 콜백 바이트 불변·객체 미게시를 검사한다. 합성 금고별 디렉터리를 분리하고 기존 단일 다운로드 회귀 테스트도 유지한다. 실제 Windows 실행 NOT RUN이며 운영 제품의 동시 다운로드/공용 금고 동시 쓰기 또는 프로필 격리 검증을 의미하지 않는다.

### 추가 구현: 단일 WPF 창 CEF 개발 탭 셸 (검증 대기)

명시적 VEIL_CEF_WPF_TABS=1 모드에서만 수동 개발 탭 UI를 열고 최대 2개 뷰를 허용한다. 기존 자동 fixture는 유지한다. 동일 Grid에 모든 HwndHost를 계속 연결하며 선택 탭만 Visible로 표시한다. 추가·선택·닫기와 주소/뒤로/앞으로/새로고침을 활성 뷰로 연결하며 실제 네이티브 종료까지 슬롯을 추적한다. 별도 tabs-smoke.mjs로 두 페이지 동시 로드, 선택 시 ID/주소 유지, 활성 탭 종료 후 나머지 탐색과 레지스트리 종료를 검사한다. Windows 실행 NOT RUN. 실제 제품 창, 외부 URL, 보호 프로필, beforeunload 취소, 키보드/airspace 검증은 후속이다.

### 추가 구현: 제품 UI 전체 화면 종료 및 창 해제 엔진 경계 (검증 대기)

전체 화면 종료를 좁은 TryExitPageFullScreenAsync 계약으로 이동한다. WebView2는 기존 전체 화면 해제 스크립트만 사용하며 CEF는 미검증 종료 동작을 false로 반환한다. 실제 상태는 기존 엔진 이벤트가 결정한다. 제품 창 종료 시 탭 및 초기화 중인 미게시 뷰를 공통 인터페이스로 해제하고 기존 사설 프로필 삭제 순서는 유지한다. 초기화 예약은 finally에서 제거하며 종료 경합의 중복 해제는 어댑터의 멱등 Dispose로 처리한다. 실제 Windows 실행 NOT RUN. WebView2 전용 보안 초기화 가드, 네트워크·다운로드·쿠키·권한 경로 및 제품 엔진은 그대로 유지한다.

### 추가 구현: 사이트 데이터 읽기 전용 엔진 경계 (검증 대기)

선택적 IEngineSiteDataReader 계약으로 쿠키 도메인 목록과 출처 저장소 사용량 조회를 이동한다. WebView2 어댑터는 사설 프로필을 거부하고 조회 전후 네이티브 뷰를 재확인하며 유효한 도메인 문자열과 유한한 음이 아닌 사용량만 반환한다. 쿠키 값/객체, 일반 CDP·스크립트 권한은 노출하지 않는다. CEF는 아직 이 계약을 구현하지 않아 미지원이며 삭제는 기존 WebView2 전용 경로와 선택·출처·epoch 확인을 그대로 유지한다. 실제 Windows 실행 NOT RUN; 제품 엔진 및 보안 정책 유지.

### 추가 구현: 사이트 데이터 삭제 엔진 경계 (검증 대기)

선택적 IEngineSiteDataWriter 계약으로 정확한 쿠키 도메인 삭제, 고정 허용 목록의 출처 저장소 삭제 및 명시적 프로필 종류(쿠키·DOM 저장소·디스크 캐시) 삭제를 분리한다. 쿠키 원본은 어댑터 내부에만 남기고 최신 스냅샷을 가져온 후 사용자 선택·탭·다운로드 상태와 네이티브 뷰를 재확인한다. 사설 프로필, 잘못된 출처, 비어 있거나 알 수 없는 삭제 플래그를 거부한다. 삭제 요청 후 쿠키 재조회는 읽기 계약을 사용하며 선택 경합 시 결과 표시를 중단한다. UI에는 기존 네이티브 프로필 동일성 가드를 보존한다. CEF는 읽기·쓰기 모두 미지원. 실제 Windows 실행 NOT RUN; 제품 엔진과 삭제 범위 유지.

### 추가 구현: CEF 합성 쿠키 도메인 읽기 (검증 대기)

합성 HTTP fixture에서만 브라우저별 요청 컨텍스트의 CefCookieManager.VisitAllCookies를 사용한다. 독립 결과 객체에 도메인 문자열만 저장하며 최대 4096개·길이 255 제한, visitor 해제 시 완료 게시(빈 목록 포함), 삭제 플래그 false를 적용한다. 관리 코드는 ID별 단일 조회를 polling하며 timeout/종료 시 결과를 폐기하고 원래 ID만 정리한다. 두 탭 fixture는 빈 프로필, HTTP HttpOnly 합성 쿠키 2개, 같은 프로필의 도메인 결과, 닫힌 ID 접근 거부를 검사한다. CEF 저장소 사용량 및 삭제는 미지원. 실제 Windows 실행 NOT RUN, 사설 프로필·제품 보안 경로·인증 다운로드 검증과 별개.

### 추가 구현: CEF 합성 정확 도메인 쿠키 삭제 (검증 대기)

합성 loopback 도메인 127.0.0.1 및 localhost만 허용하는 IEngineSiteDataWriter의 정확 도메인 삭제를 구현한다. UI 스레드 visitor에서 일치 쿠키마다 현재 뷰·작업 객체 및 관리 권한을 재확인한 뒤 삭제 플래그를 설정한다. 늦은 visitor는 만료된 scan을 사용하므로 새 작업의 권한을 재사용하지 못한다. 권한 만료 전 삭제 요청이 없으면 null, 일부 요청이 있었거나 오류면 실패로 보고한다. 두 도메인의 합성 HttpOnly 쿠키로 권한 철회·정확 범위·재조회 및 후속 HTTP 쿠키 보존을 검사한다. 저장소/프로필 전체 삭제 미지원이며 실제 Windows 실행 NOT RUN. 제품 엔진 및 보호 프로필 정책 유지.

### 추가 구현: CEF 합성 프로필 쿠키 전체 삭제 (검증 대기)

기존 exact-domain 삭제와 별도 명시적 FixtureProfile 범위/네이티브 진입점을 추가한다. 합성 fixture와 opt-in 탭 모드 모두 활성화된 임시 프로필에서만 사용하며, 관리 계약은 Cookies 단독 플래그만 허용하고 혼합/미지원 플래그는 변경 전에 거부한다. 기존 per-cookie 권한·뷰·작업 수명 검증을 공유하며 빈 도메인이나 wildcard를 전체 삭제로 해석하지 않는다. 회귀 fixture는 두 출처의 합성 쿠키 재생성, 혼합 범위 거부, 권한 철회, 전체 삭제 후 빈 조회/HTTP 쿠키 부재, 양쪽 LocalStorage 표식 보존을 검사한다. 실제 Windows 실행 NOT RUN. DOM 저장소·디스크 캐시 삭제 및 제품 보안 프로필 연결은 미지원 상태 유지.

### 추가 구현: CEF 합성 출처 저장소 사용량 조회 (검증 대기)

합성 HTTP fixture + opt-in 탭 모드의 실행 포트에 해당하는 127.0.0.1/localhost 정규 출처만 허용한다. 브라우저 UI 스레드의 CefDevToolsMessageObserver/ExecuteDevToolsMethod로 고정 Storage.getUsageAndQuota 한 가지 명령을 실행하고 요청 ID·응답 크기·JSON 숫자 타입·유한한 음이 아닌 사용량을 검사한다. 임의 명령·원본 JSON·저장값은 관리 코드에 전달하지 않으며 observer 등록은 뷰/조회 종료 시 해제한다. 관리 계약의 timeout과 ID 검증 유지. 합성 CacheStorage 64 KiB/1 MiB를 생성하여 출처별 사용량 차이 및 외부 출처 거부를 검사한다. 실제 Windows 실행 NOT RUN. 엔진 보고 사용량은 쿠키나 모든 저장 유형의 총량이 아니며 삭제 및 제품 프로필 연결은 미지원.

### 추가 구현: CEF 합성 출처 저장소 삭제 (검증 대기)

합성 HTTP fixture + opt-in 탭 모드의 두 정규 출처에 한해 고정 Storage.clearDataForOrigin 명령을 실행한다. 기존 SiteDataPolicy.OriginStorageTypes와 같은 local_storage,indexeddb,cache_storage,service_workers,websql,file_systems 허용 목록만 사용하며 cookies/all/프로필 전체 삭제는 제외한다. 명령 직전 관리 권한 및 뷰·작업 객체를 다시 검사하고 CDP 응답 ID와 성공 상태를 확인한다. 이미 전달된 명령은 observer 해제로 되돌릴 수 없으므로 timeout/오류는 부분 적용 가능성을 유지한다. 합성 회귀는 외부 출처 및 권한 만료 거부, LocalStorage/CacheStorage 삭제와 다른 출처 보존, HTTP 쿠키 보존을 검사한다. 나머지 허용 저장 유형의 개별 실제 삭제 검증은 후속. Windows 실행 NOT RUN, 제품 엔진 및 보호 프로필 유지.

### 추가 구현: CEF 합성 HTTP 캐시 삭제 (검증 대기)

새 임시 프로필의 opt-in 탭 fixture에서만 별도 tab-fixture-cache 경로를 설정하고 요청 컨텍스트의 캐시 경로가 일치할 때 고정 Network.clearBrowserCache 명령을 허용한다. 일반 개발 및 다운로드 fixture의 설정은 유지한다. 공통 프로필 계약의 Cookies 단독 또는 DiskCache 단독 범위만 허용하며 혼합/DOM 저장소/미지원 플래그를 변경 전에 거부한다. 기존 CDP 삭제 슬롯·권한 재확인·응답 ID·타임아웃을 사용한다. 합성 HTTP 캐시 응답의 재사용 및 삭제 후 서버 재요청 횟수, 권한 철회와 혼합 범위 거부, 쿠키·LocalStorage·CacheStorage 보존을 검사한다. HTTP 캐시의 실제 디스크 파일 소거/보안 삭제를 주장하지 않는다. Windows 실행 NOT RUN, 제품 보안 프로필 및 전체 DOM 저장소 삭제 미지원.

### 추가 구현: CEF 알려진 합성 출처 일괄 삭제 (검증 대기)

CEF 공개 CefRequestContext API에는 HTTP 캐시 삭제가 있으나 WebView2 AllDomStorage와 동일한 전체 저장소 삭제 계약은 확인되지 않았다. CDP Storage.clearDataForOrigin은 지정 출처만 대상으로 하므로 방문 기록/열린 탭으로 전체 프로필을 추정하지 않는다. 제품 DomStorage 플래그와 혼합 범위는 미지원 유지. 별도 내부 fixture helper가 실행 포트의 두 명시적 출처를 순차 삭제하고 매 명령마다 원래 뷰와 권한을 확인한다. 권한 철회 시 이미 응답 성공한 요청 수(0/1/2)를 반환하며 오류 시 일부 적용 가능성을 포함해 실패한다. LocalStorage/CacheStorage의 두 출처 삭제 및 중도 철회, 쿠키 보존을 확인하는 테스트 추가. 실제 Windows 실행 NOT RUN. 전체 저장소 키/파티션 열거 및 삭제 보장은 별도 출시 차단 조건. 참고: https://cef-builds.spotifycdn.com/docs/154.0/classCefRequestContext.html 및 https://chromedevtools.github.io/devtools-protocol/tot/Storage/#method-clearDataForOrigin.

### 추가 구현: 엔진별 사이트 데이터 지원 범위 및 UI 가드 (검증 대기)

SiteDataCapabilities로 제품 WebView2, 합성 CEF fixture, 미지원 상태를 분리한다. WebView2는 기존 3가지 프로필 삭제 유형 및 조합을 지원하고 CEF 탭 fixture는 Cookies/DiskCache 단독만 광고한다. CEF의 전체 DomStorage 및 혼합 범위는 미지원, 제품 UI 진입 자체도 미허용 유지. 제품 UI의 기존 네이티브 프로필 동일성·사설 모드·다운로드·탭/선택 수명 가드를 보존하며, 지원 정보가 바뀌거나 미지원 선택이면 실행하지 않는다. 대화상자 버튼/체크박스와 확인 동작에도 지원 정보를 적용한다. 순수 .NET 테스트에서 플래그 조합, 미지원, fixture 구분 및 유효 범위를 검사하고 정적 회귀 추가. 실제 Windows UI/CEF 실행 NOT RUN; 지원 정보는 권한 또는 실제 보안 검증을 대체하지 않는다.

### 추가 구현: CEF 사이트 데이터 새 프로세스 격리 및 테스트 임시 폴더 정리 (검증 대기)

첫 탭 fixture 종료 직전에 지속 합성 쿠키 및 LocalStorage/CacheStorage 표식을 생성한다. 동일 HTTP 출처의 두 번째 프로세스를 별도 mkdtemp 루트에서 시작해 이전 프로필이 디스크에 존재하는 동안 쿠키/표식 부재를 확인한다. CEF 테스트 실행 파일의 부모 PID·명령줄 루트에 따른 프로세스 잔존 검사 후 성공한 runner 소유 루트만 정리하고 실패 시 진단 경로 보존. 단일 다운로드 fixture의 탭 모드 환경 상속 차단. Windows 실행 NOT RUN; 프로세스 검사 범위는 지정 테스트와 연관된 프로세스이며 일반 파일 제거이지 보안 소거 또는 제품 암호화 프로필 격리 입증이 아니다.

#212 자체 리뷰 보완: spawn 환경 변수는 env 객체 안에 배치하고 Windows의 대소문자 비구분 환경 변수 중복을 제거한 뒤 명시적으로 설정한다. 실행 경로를 읽을 수 없는 연관 프로세스도 이름·PID/명령줄 범위에서 보수적으로 검사한다. 새 head CI 및 Windows 런타임 확인 필요.

### 추가 구현: 로그인 양식 공통 capability 및 WebView2 어댑터 분리 (검증 대기)

IEngineLoginForms는 고정 probe/read/fill만 노출하며 공통 SavedLogin 레코드의 로그 문자열 redaction을 유지한다. 제품 BrowserWindow는 기존 사용자 명령, 마스터 인증, 출처·탭·선택 epoch, 보호 세션 검사에 원래 어댑터 동일성을 추가한다. WebView2 구현은 dispatcher, 정확한 HTTPS 페이지·정규 출처·일반 프로필 및 권한을 실행 전후 검사하며 기존 LoginFormPolicy의 최상위 가시적 동일 출처 폼 규칙과 미제출 정책을 유지한다. CEF에는 계약 파일만 링크하고 구현하지 않으며 실제 계정 전달 경로는 열지 않는다. PasswordStore/Rust 암호화 형식 및 인증/백업/복원 유지. 정적 계약 테스트 추가, 실제 Windows 로그인 폼 검증 NOT RUN. 기존 페이지 스크립트 환경의 한계 및 이미 실행된 채우기의 롤백 불가를 보안 검증 완료로 간주하지 않는다.

### 추가 구현: CEF 합성 로그인 양식 boolean 감지 (검증 대기)

CEF 제품 IEngineLoginForms 미구현 유지. 명시적 탭 fixture의 127.0.0.1 포트 및 사전 등록된 6개 경로만 허용하고, 정확한 현재 최상위 URL 확인 후 고정 Runtime.evaluate를 실행한다. 반환값은 제한된 JSON의 boolean만 인정하며 입력값 읽기/채우기/제출 경로 없음. 탐색·주소 변경·renderer 실패·종료 시 observation을 무효화하고 관리 권한 및 뷰 동일성 재확인. 정상 폼과 숨김·다중 비밀번호·다른 출처 제출·iframe·new-password 거부, 권한 만료 결과 폐기를 합성 테스트로 검사한다. HTTP loopback fixture 예외는 제품 HTTPS 규칙 변경이 아니다. 실제 Windows 실행 NOT RUN, 페이지 JS 환경이 신뢰 경계라는 주장 없음. 제품 암호화 저장소 연결 및 실제 계정 읽기/채우기 미지원.

#214 자체 리뷰 보완: CefWriteJSON의 문서화된 배열 root를 사용한다(https://cef-builds.spotifycdn.com/docs/150.0/cef__parser_8h.html). boolean false와 실패/권한 만료/스크립트 예외(null)를 분리하여 실패가 거부 테스트 통과로 오인되지 않도록 한다. iframe fixture는 실제 동일 출처 하위 문서에 폼이 로드되었음을 확인한 뒤 준비 상태를 표시한다. UI thread 확인을 registry 조회보다 먼저 수행한다. 새 head CI 및 실제 Windows 실행 검증 필요.

### 추가 구현: CEF 합성 로그인 입력값 일치 검증 (검증 대기)

실제 계정 읽기 기능은 미구현 유지. 허용된 127.0.0.1의 별도 세 페이지에서만 고정 합성 계정/비밀번호 표식과 입력값이 일치하는지 페이지 내부에서 비교한다. 호스트로 값 원문을 전달하지 않고 기존 boolean/null observer만 재사용한다. 값 비교 직전 관리 권한 콜백 및 원래 뷰·주소·로드 상태를 재검증하고 권한 철회 시 명령 미발행. 정상/빈 값/불일치, 기존 감지 페이지 거부, dispatch 권한 철회, 미제출/로그 미포함 테스트 추가. CEF 제품 IEngineLoginForms와 암호화 저장소는 여전히 연결하지 않는다. 실제 Windows 실행 NOT RUN. 합성 equality 검증은 일반 로그인 정보 읽기나 보안 경계의 검증 완료가 아니다.

### 추가 구현: CEF 합성 로그인 자동완성 및 독립 상태 검증 (검증 대기)

제품 credential capability는 미구현 유지. 명시된 5개 loopback 페이지에서만 고정 합성 문자열을 채우며 기존 비밀번호 또는 다른 계정명이 있으면 거부한다. 값 비교와 같은 UI thread/뷰/페이지/dispatch 권한 가드를 적용하고 결과는 boolean/null만 반환한다. 별도의 고정 읽기 전용 상태 검사로 실제 입력값과 input/change 이벤트 수, submit 이벤트 부재를 확인한다. 빈 폼, 같은 계정, 기존 비밀번호, 다른 계정, dispatch 권한 철회 및 허용 외 경로 테스트 추가. 발행된 명령은 취소/롤백 보장 없음. Windows 실행 NOT RUN. 일반 계정 저장·읽기·채우기 및 암호화 저장소 연결은 여전히 미지원.

### 추가 구현: CEF 합성 로그인 작업의 재진입 및 수명 가드 (검증 대기)

관리 어댑터의 작업 슬롯과 컨텍스트 generation을 추가하고 권한 콜백 전후 동일 generation을 확인한다. 탐색 명령/탐색 콜백/주소 변경/renderer 오류/뷰 해제 및 탭 선택·닫기에서 이전 권한을 무효화한다. 네이티브 탐색 및 닫기 명령에서도 기존 observation을 즉시 무효화한다. 중복 호출은 최초 작업의 슬롯을 정리하지 않으며, A-B-A 탭 변경 및 native dispatch 권한 콜백 내부 탐색의 결과를 합성 폼 상태 검사로 검증한다. 테스트 시간 상한 조정. 실제 Windows 실행 NOT RUN. 이미 발행된 CDP mutation 취소/롤백 보장 없음, renderer 종료 및 닫기 중 실제 실행 검증 별도 필요. CEF 제품 credential capability 미구현 유지.

#217 CI 보완: Windows 소스 검사의 기존 hard reload 및 monotonic identity 단언을 새 선행 invalidation 순서를 반드시 요구하도록 갱신했다. 닫기 중 권한 콜백이 true를 반환해도 이전 generation 무효화, 닫힌 native ID 직접 재사용 거부, 새 ID의 빈 양식 확인 회귀를 추가했다. renderer 오류 invalidation은 기존 소스 검사 수준이며 실제 crash 재현 미실행. 실제 계정 연동 전 필수: 표준 사용자 Windows에서 전체 fixture 및 protected profile 격리/쓰기 추적, HTTPS 최상위 문서와 실제 document lifetime 검증, 명시적 인증/사용자 동의, bounded secret 전달 및 로그·크래시·디스크 노출 검증, renderer 종료/닫기/탐색 경쟁, 실패 시 fail-closed, 제품 release gate 재검토. 실제 Windows 실행 NOT RUN; CEF credential capability 미구현 유지.

### 추가 구현: CEF 합성 renderer 종료 및 복구 회귀 (검증 대기)

명시적 tab fixture, 정확한 127.0.0.1 포트·전용 경로, registry 단일 뷰, pending 전용 observation 조건에서만 고정 CDP Runtime.evaluate(awaitPromise) 및 Page.crash 진단 명령을 허용한다(https://chromedevtools.github.io/devtools-protocol/tot/Page/#method-crash). 임의 URL/스크립트/명령/계정 입력은 없음. renderer 종료 콜백에서 observation 실패 처리 및 renderer failed gate를 설정하고 성공한 최상위 navigation 전까지 새 로그인 observation을 거부한다. 실제 callback 1회, 이전 결과 null, 실패 중 재요청 거부, 명시적 새 페이지 탐색 후 동일 view ID와 빈 폼을 확인하는 fixture 추가. 다른 renderer 오류는 기존 실패 경로. 실제 Windows 실행 NOT RUN, Page.crash는 실험적 CDP 명령이므로 pinned CEF 지원 및 런타임 순서 확인 필요. 제품 credential capability/엔진 변경 없음.

### 추가 구현: 제품 credential capability admission (검증 대기)

IEngineLoginForms에 Unavailable/SyntheticFixture/Product 범위 선언을 추가한다. WebView2의 초기화된 일반 profile만 Product로 선언하며 CEF는 해당 인터페이스 미구현을 유지한다. BrowserWindow는 제품 범위에 한해 감지·명시적 페이지 저장·채우기를 시작하고 모든 기존 origin/탐색/선택/인증/동의 가드와 함께 매번 capability 및 adapter 동일성을 재검사한다. private/fixture/unknown/absent/revoked scope 및 admission 중 명령 미실행을 순수 .NET 회귀 테스트로 추가한다. 수동 암호화 저장소 관리·형식·백업/복원 변경 없음. 범위 metadata는 단독 authorization이나 제품 release 승인 아님. CEF 실제 비밀번호 전달, production profile 보호, 런타임 검증 완료 전 Product 선언 금지. 실제 Windows 실행 NOT RUN.

### 추가 구현: 표준 사용자 Windows 합성 통합 실행 스크립트 (검증 대기)

main의 컴파일 전용 helper와 분리한 `scripts/test-cef-wpf.ps1`을 추가한다. clean commit, x64/non-elevated interactive session, 새 작업 폴더, 고정 CEF archive 해시 및 locked Rust build, fixture TEMP/TMP, 제한 시간, test executable 잔여 프로세스 확인을 요구한다. Win32 → 단일 WPF/금고 → 다중 탭/사이트 데이터/로그인 회귀를 순서대로 실행하고 commit·바이너리 SHA-256·단계별 결과·로그를 보존한다. 일반 사용자 실행의 필요조건만 확인하며 실제 desktop 활성 여부나 token sandbox 보안을 독립 측정하지 않는다. 통합 실행 NOT RUN. 보호 볼륨에서 CEF 데이터 보존, 볼륨 외 쓰기 추적, sandbox/site isolation, 네트워크 경로, 업데이트/패키징, Gate A~E는 별도 증거 필수. 제품 엔진 WebView2 유지.

### 추가 구현: CEF 보호 프로필 읽기 전용 사전 검사 (검증 대기)

제품 브로커 기반 ProfileGate를 재사용하는 CefProtectedProfilePreflight 및 별도 CefProfileLayout을 추가한다. 고정 제품 root/cef 아래 cache, temp, logs 및 cef.log 경로를 분리하며 WebView2 프로필이나 기존 temp를 사용하지 않는다. 실제 등록된 암호화/보호/잠금 해제/보호기 상태 및 동일 볼륨 GUID를 기존 root와 존재하는 디렉터리에 요구하고 재분석 지점·파일 충돌·권한 오류를 거부한다. 초기/종료 root 검증, 기존 CEF 하위 경로 검사, 순수 레이아웃 회귀 포함. 읽기 전용: 생성, 환경 변경, 엔진 시작, 암호화 보장 또는 launch permission 없음. 기존 CEF smoke는 CefInitialize 이후 WPF 진입이므로 이 검사에 통과해도 보호 엔진 연결은 불가하다. 후속은 broker 인증 완료 후, 보호 경로 생성/재검증, 자식 bootstrap 시작 전 TEMP/TMP/로그 경로 전달, bootstrap 측 native 초기화 이전 독립 검증 및 종료/재잠금 조율. 검사와 사용 사이 TOCTOU, 볼륨 외 쓰기, renderer sandbox/사이트 격리 및 실제 BitLocker VM 실행 미검증. 제품 엔진 WebView2 유지.

### 추가 구현: CEF 보호 디렉터리 준비 모듈 (검증 대기)

기존 보호 세션과 exclusive profile lock을 호출자가 소유하는 전제에서만 CefProtectedProfilePreparation.Prepare를 사용하도록 계약을 정의했다. 최초 root 보호 상태 및 GUID 고정, 기존 대상 전체 read-only preflight 이후 고정 parent/child 순서로 CEF root/cache/temp/logs를 준비한다. 각 단계에서 세션 권한, root와 직접 parent의 등록 보호 상태/GUID를 재확인하고 기존 ProfileGate.Prepare의 생성 전 ancestor 검증을 재사용한다. 생성 후 동일 GUID 및 redirected children 검사, 전체 재검사 및 최종 권한/볼륨 확인을 수행한다. 중간 실패는 즉시 전파하며 이미 만들어진 빈 폴더를 삭제하거나 기존 데이터에 롤백/정리를 시도하지 않는다. 레이아웃 순서 순수 .NET 검사와 소스 계약 검사 추가. 제품 startup에서 미호출, 실제 session/lock 소유권과 호출자 권한의 결합은 후속. path TOCTOU/파일 hardlink/OS 쓰기 검증을 대체하지 않으며 실제 BitLocker VM 실행 NOT RUN. CEF 초기화 전 별도 실행 경계 및 child/renderer 종료와 relock 조율은 미구현. 제품 WebView2 유지.

### 추가 구현: 인증 세션 기반 CEF 준비 환경 통합 (검증 대기)

#222~#224의 정확한 head를 합친 통합 브랜치에서 CefPreparationLease.PrepareChildEnvironment를 추가한다. 등록 제품 root와 같은 인증 broker를 사용하여 살아 있는 lease를 재확인하고, 보호 디렉터리 준비에 lease의 StillAuthorized를 직접 전달한다. 현재 프로세스 환경을 복사하여 CEF 전용 TEMP/TMP/cache/log 계획을 구성하고 반환 직전 권한을 다시 검증한다. 호출자에게 임의 root/broker/권한 콜백을 받지 않는다. 반환값은 환경 스냅샷이며 launch 권한이 아니다. 기존 제품 시작 경로 미호출, native broker 인증 및 보호 경로 검증 연결 미완료. #225 네이티브 경로 검증 PR은 별도. 실제 Windows/BitLocker/lock contention 및 프로세스 수명 검증 NOT RUN, 기존 WebView2와 release gate 유지.

### 추가 구현: 브로커 소유권 확인과 CEF 준비 통합 (검증 대기)

#226에 #227의 정확한 head를 통합한다. CEF 준비 lease의 기존 thread/session/lock/volume 검증에 브로커 AuthenticatedOwner 조회를 연결하고, 파일 경로 검증 전후 모두 현재 소유권 및 보호 상태와 인증 GUID를 확인한다. 소유자 거부, 응답 오류, 잠금 요청 발생 시 준비 실패를 전파하며 일반 Status만으로 대체하지 않는다. #225 네이티브 검증은 별도 PR이다. 아직 자식 bootstrap이 실제 브로커 인증을 수행하지 않고 준비 API도 제품 startup에 연결되지 않는다. 별도 native client admission 및 인증 위임, 종료/재잠금, 실제 BitLocker 및 OS 쓰기 실측은 NOT RUN. WebView2 유지.

### 추가 구현: 프로세스 세대 확인 및 CEF 자식 위임 정책 모델 (검증 대기)

Named pipe에서 확인한 ClientIdentity에 OS GetProcessTimes의 생성 시각을 포함하고, unlock crash watcher handle을 열 때 PID와 생성 시각 및 생존 상태를 일치시킨다. 별도 순수 CefChildAdmission 모델은 인증된 owner/child의 전체 identity, 동일 SID/session, unlock 세대 객체, 15초 단조 시간, 단일 사용 및 명시 철회를 요구한다. 미래 broker가 직접 관측한 child image 검증·생존·보호 상태만 전달할 계약이며 현재 protocol/제품에는 연결되지 않는다. 실제 parent-child launch binding, 서명/해시 pin, 원본 handle 보유, pipe 인증, 자식 종료/relock, Windows/BitLocker 실측은 미완료. 기존 broker client allowlist와 WebView2 제품 시작을 유지한다. 순수 회귀 및 소스 계약 검사 추가.

### 추가 구현: CEF 실행 파일 pin 검증 (검증 대기)

CefImageDigest는 정확한 64자 SHA-256과 전체 스트림 검사를 요구한다. 미연결 CefPinnedImage.OpenVerified는 별도 등록 경로와 실제 child process handle의 PID/생성 시각/image path, 관리자 소유 및 쓰기 제한 파일·직접 directory, reparse ancestor, read-only share로 유지한 파일 handle의 canonical path 및 single link, SHA-256/Authenticode, 검사 후 재검증을 요구한다. 성공 시 파일 handle을 반환하여 호출자가 수명을 유지할 수 있다. 프로세스 메모리/로드된 DLL, 상위 directory ACL 전부, 경로 race, 실제 parent-child 관계, 별도 제품 pin 등록 및 배포·서명, native bootstrap/IPC 권한 위임은 아직 미완료. 현재 브로커 allowlist와 명령, 기존 WebView2 startup은 변경하지 않는다. 순수 hash 회귀 및 source contract 추가; 실제 Windows signature/ACL/process/BitLocker 실측 NOT RUN.

### 추가 구현: CEF launch nomination 및 Windows process handle 회귀 (검증 대기)

Native.ObserveProcess는 실제 live process handle에서 PID/생성 시각/image/SID/session을 관측하며 기존 pipe의 PID/session과 일치시킨다. 미연결 DuplicateNominatedChild는 기존 owner handle과 재개방한 generation 일치 owner process를 요구하고 owner가 명시한 원본 child handle을 query/synchronize 권한만으로 복제한다. child의 SID/session/생성 시각, Toolhelp reported parent, 양쪽 생존 및 신원을 다시 확인하고 실패 시 handle을 닫는다. reported parent는 생성자가 지정할 수 있는 메타데이터라 단독 인증 증거가 아니며 이 helper도 생성 이벤트/실제 creator 증명이나 launch authority가 아니다. 실제 authenticated broker 요청, 등록 실행 파일 pin 및 one-shot admission 연결과 suspended launch/containment는 후속. 새 CI에서 합성 child 실제 프로세스를 실행하여 handle/세대/종료 및 잘못된 handle 거부를 검증하도록 추가. Windows test 결과 대기; 실제 CEF/BitLocker/서비스/표준 사용자 실측 NOT RUN. 기존 broker 명령 및 WebView2 유지.

### 추가 구현: owner-only CEF broker 사전 검증 명령 (검증 대기)

CheckCefChild 명령은 기존 인증 및 allowlist를 통과한 실제 unlock owner만 요청 가능하다. 엄격한 decimal string childHandle만 허용하고 기존 명령과 secret/path/PID 입력을 분리한다. admin-only 생산 설정의 별도 CefChildImage/CefChildSha256 pin이 모두 등록되어야 하며 미등록/개발 모드에서는 CefUnavailable. 실제 handle 복제, child 파일 경로/서명/hash 검증, owner/child 및 볼륨 재확인 후 일반 status만 반환한다. handle은 요청 종료 시 폐기하며 위임 grant, 자식 pipe 권한, 실행 승인은 절대 생성하지 않는다. 현재 installer/desktop/native 제품 startup 미연결. 순수 실제 parser 회귀와 fake broker 거부 시나리오 추가; fake broker 및 실제 production pin/BitLocker 실행 NOT RUN. 다음 단계는 broker 소유의 retained handle/grant lifecycle 및 별도 child redemption 프로토콜과 native 검증 연결.

### 추가 구현: 단일 대기 CEF 위임 자원 (검증 대기)

StageCefChild는 별도 제품 pin이 등록된 실제 unlock owner만 호출할 수 있다. 기존 CheckCefChild와 별도 명령으로 child handle/image handle 및 CefChildAdmission을 하나의 broker pending slot에 보존한다. 중복 staging 거부, 15초 단조 정책 및 serialized timeout, lock/detach 요청, owner 종료, service cleanup, 보호 상태 상실 시 철회/자원 정리. 반환은 기존 status이며 grant token 또는 child pipe 권한은 없다. 실제 redemption/launch authorization, child exit containment/relock, installer 등록 및 native 연동은 미완료. 순수 resource lifecycle, 실제 parser, 소스 계약 및 fake broker 거부 테스트 추가. 최신 CI와 production Windows/BitLocker 실행 결과 대기. WebView2 유지.

### 추가 구현: CEF child 일회성 검증 응답 (검증 대기)

RedeemCefChild는 기존 client allowlist의 image 거부 중 별도 admin pin과 경로가 일치하는 production 후보만 전용 분기로 들어가며 다른 모든 명령은 ClientRejected. 기존 desktop owner도 이 명령을 사용할 수 없다. pipe에서 관측한 실제 child identity와 pending generation이 일치해야 slot을 소비하고, owner/child retained handles, 실행 파일 canonical path/서명/hash, 등록 보호 상태를 재확인한 뒤 one-shot 정책을 적용한다. 성공은 최소 boolean cefChildVerified 응답뿐이며 실패·예외·lost reply 모두 재사용 불가. 현재 installer 및 CEF bootstrap에 연결하지 않았으며 실행 허가나 보호 볼륨 접근권 자체를 발급하지 않는다. child lifespan/containment/relock 및 원본 pipe identity handle 수명 강화, 전체 install trust, 실제 signed client/service/BitLocker 검증 필요. 기존 WebView2 유지. 실제 parser와 순수 slot 회귀 및 source contract 추가.

### 추가 구현: CEF native broker 검증 통신 모듈 (검증 대기)

별도 broker_verification 모듈은 고정 설치 service/pipe, SCM running PID와 pipe server PID 및 live process handle 일치 전후 확인, 제한된 ASCII volume ID, 고정 RedeemCefChild 요청, LF 프레임 및 정확한 compact boolean receipt만 허용한다. Overlapped I/O의 8초 예산과 취소/완료 정리, 실패 후 retry 없음. SCM 호출 및 cancellation drain은 엄격한 전체 wall-clock deadline 보장이 아니다. Native wire synthetic CI에서 invalid ID/부분 응답/추가 필드/거부/중복 필드 등을 검증하며 실제 서비스에는 접속하지 않는다. 기존 native smoke startup 및 CefInitialize 미연결, #225 경로 검증 별도, 실제 service/pipe transport·signed package·launch containment·BitLocker NOT RUN. receipt는 초기화 허가가 아니다. 고정 pipe 및 제한된 ID는 향후 installer registration 계약과 맞춰야 한다. WebView2 유지.

### 추가 구현: native 경로 및 broker wire 검증 통합 (검증 대기)

#225의 정확한 head 2a9370fe512b6ac4dadb1dba2f456a7ab7a201c9를 #226에 통합한다. 기존 native wire CI와 synthetic path CI를 모두 유지하며 기존 두 smoke host는 product 경로 환경이 존재하면 CefExecuteProcess 이전에 즉시 거부한다. 경로/볼륨 문자열 비교에 길이 제한과 embedded NUL 거부를 추가하고 아직 존재하지 않는 log를 포함해 모든 supplied path의 canonical drive 형식을 먼저 확인한다. synthetic NUL regression 추가. 두 모듈을 동일 브랜치에서 빌드/검증하는 단계이며 실제 native bootstrap의 broker auth + registered paths 결합, 권한 수명/containment 및 BitLocker는 NOT RUN. WebView2 제품 및 release gate 유지.

### 추가 구현: native startup preflight sequencing (검증 대기)

RegisteredBootstrap은 별도의 신뢰된 admin 등록 root/volume GUID/volume ID를 입력으로 요구한다. 현재 v1 broker receipt에는 이 정보가 없어 환경변수나 receipt만으로 trust를 구성할 수 없다. 실제 등록 loader 및 설치 계약 미구현. CheckBootstrap은 TEMP/TMP와 고정 protected path, volume 및 canonical handles 검증 후 단 한 번 receipt 요청, 재조회한 환경/경로 상태를 다시 검증하고 변화 시 거부. known fixture/log override 존재 거부. 독립 native module과 합성 filesystem/receipt sequencing regression (invalid volume, TEMP/TMP mismatch, refused receipt, env mutation, removed cache) CI 추가. 기존 RunWinMain과 CefInitialize는 이 함수를 호출하지 않으며 product environment 거부 유지. Bootstrap executable + loaded DLL pin, trusted registration, launch containment/lifetime, actual broker + BitLocker/runtime NOT RUN. 성공 preflight는 초기화 권한이 아니다. WebView2 유지.

### 추가 구현: native admin registration reader (검증 대기)

고정 64-bit HKLM\SOFTWARE\VEIL\CefBootstrap에서만 ProductRoot/VolumeGuid/VolumeId REG_SZ 3개를 읽는 reader를 추가. SOFTWARE는 OS-controlled anchor로 가정하고 VEIL 및 CefBootstrap key 자체의 SYSTEM/Administrators owner, protected non-null DACL, nontrusted read-only ACE, registry link refusal, bounded strings/embedded NUL/strict GUID+ASCII volume ID, leaf snapshot 변경 및 ACL 재검증을 요구한다. 설치자는 두 키를 protected DACL로 생성해야 하며 기존 broker.json/client.json의 등록과 일치하는지 검증하는 provisioning 작업은 미구현. 별도 registration 합성 ACL/format test는 실제 HKLM 접근/수정 없이 수행한다. 기존 native bootstrap 실행에는 미연결. 관리자 변경 경쟁/OS anchor 신뢰/registry ACL 실측 및 서비스 연동, signed loaded DLL 및 launch containment/BitLocker는 NOT RUN. WebView2 및 제품 실행 차단 유지.

### 추가 구현: installer CEF registration consistency plan (검증 대기)

기존 패키지는 Desktop/Broker만 허용하고 CEF 설치 파일/핀/업데이트 저널 지원이 없다. 따라서 먼저 순수 cef-registration-plan.ps1과 read-only 관리자 검사 check-cef-registration.ps1을 추가. broker.json/client.json의 exact volume ID, volume GUID format, fixed service pipe, production flags, profile path, 별도 executable pin 및 관측 SHA-256 일치 확인. 관리자 검사에서는 고정 Program Files/ProgramData 경로, 기존 ACL helper, 서명 지문, 정지된 LocalSystem service 및 등록/실행 파일 snapshot 전후 확인. 예정 제품 host 경로 Cef/veil-cef-product.exe는 현재 존재하지 않음. 실제 registry write/installer activation/update/remove/recovery는 미연결; 현재 package contract에서는 검사 성공 불가. 전체 signed package inventory, JSON strict parser, ancestor ACL/races, protected volume identity, SCM/registry 실제 통합 검증 필요. 합성 순수 PowerShell 5.1 테스트 CI 추가. 제품 실행이나 출시 허가가 아니며 WebView2 유지.

### 추가 구현: isolated product-named CEF build candidate (검증 대기)

별도 EXCLUDE_FROM_ALL veil-cef-product DLL 및 공식 pinned bootstrap.exe 복사본을 product-candidate/Release 출력으로 빌드한다. 기존 smoke/WPF/managed fixture와 분리하고 runtime/resources는 CEF distribution의 CMake inventory를 사용한다. product_candidate.cc RunWinMain은 무조건 29 반환하며 CEF 실행·broker 인증·profile I/O를 시작하지 않는다. build-cef-smoke.ps1 -BuildProductCandidate 선택 및 CI compile + unsigned candidate inventory/hash/fixture refusal tests 추가. inventory는 installable=false/startupEnabled=false이며 signed manifest가 아니다. 기존 format 1 package/installer는 CEF를 여전히 거부하고 변경하지 않는다. 실제 signed runtime inventory/loaded module verification, license notices, format migration/installer registration, launch containment, BitLocker 및 제품 실행은 NOT RUN. WebView2 유지.

### 추가 구현: detached signed CEF candidate evidence (검증 대기)

별도 cef-candidate.json / cef-candidate.p7s evidence format에 exact file inventory + source commit + pinned archive identifier를 기록하고 SHA-256 CMS 서명, independent thumbprint/commit, existing ACL 및 product EXE/DLL Authenticode 검사, 모든 파일 이름/길이/hash/누락/추가 검증을 제공한다. 후보와 evidence 디렉터리는 분리되고 installable=false/startupEnabled=false 고정. 현재 format 1 설치 패키지 및 설치/업데이트에는 연결하지 않는다. 관리자는 이미 서명된 EXE/DLL과 보호 디렉터리를 제공해야 한다. synthetic CI는 in-memory certificate로 CMS signature math 및 변조/누락/추가/다른 signer/commit 등을 검사하며 certificate chain/ACL/Authenticode는 모의 처리한다. manifest의 archive identifier는 실제 archive provenance 증명이 아니며 strict JSON parser, hardlink/ADS/TOCTOU 및 loader dependency pin, 실제 서명/설치/BitLocker NOT RUN. WebView2 유지.

### 추가 구현: isolated signed candidate staging (검증 대기)

기존 유지보수의 restricted directory / signed inventory 방식을 이용하되 설치 트리와 기존 update journal은 변경하지 않는다. stage-cef-candidate.ps1은 고정 Program Files/VEIL.cef-candidate-staging sibling에만 후보/evidence를 별도 복사한다. 원본 서명·인벤토리 검증이 먼저이며 실제 열거된 이름을 사용하고 덮어쓰기 없이 복사, 대상 전체 서명/파일 검증 및 최초 manifest 비교, 원본 재검증. 기존 staging은 변경하지 않고 실패/중단 잔재는 관리자 검토용 보존. 합성 테스트는 실제 CMS math 및 파일 복사를 실행하고 ACL/chain/Authenticode만 모의 처리한다. 현재 후보는 installable=false/startupEnabled=false; 실제 운영 설치/업데이트/복구 활성화, 관리자 ACL/ancestor/TOCTOU 및 signed package/BitLocker NOT RUN. WebView2 유지.

### 추가 구현: Windows suspended child containment primitive (검증 대기)

CefContainedProcess는 local absolute executable 및 명시적 정렬 Unicode environment block, no shell/no inherited handles/no arbitrary arguments, CREATE_SUSPENDED, unnamed KILL_ON_JOB_CLOSE job에 assign 및 membership 확인, broker nomination용 live suspended handle, verification callback 후 단일 resume, 실패 시 owned child 정리 구조를 제공한다. 실제 Windows synthetic apphost CI로 denied resume, suspended state, successful resume, independent process handle에서 job close exit 확인 예정. callback은 production broker authority가 아니며 현재 desktop startup에는 연결하지 않는다. CreateProcess와 job assignment 사이 owner crash window (atomic job assignment 필요), graceful shutdown, complete process tree drain, broker lifetime/relock, CEF sandbox nested jobs compatibility, trusted image/loaded DLL pin, Windows/BitLocker NOT RUN. 기존 product startup 및 WebView2 유지.

### 추가 구현: atomic Windows job assignment (검증 대기)

STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_JOB_LIST로 unnamed KILL_ON_JOB_CLOSE job에 child를 생성 시점부터 소속시킨다. CREATE_SUSPENDED/UNICODE/EXTENDED_STARTUPINFO flags, no handle inheritance, post-create membership recheck, attribute memory 및 SafeHandle ref lifetime 관리, API 실패 시 legacy fallback 없음. 기존 CreateProcess/AssignProcess 사이 orphan window를 제거하는 설계. 실제 Windows synthetic apphost에서 suspended child를 보유한 별도 owner를 강제 종료하고 관찰 핸들로 child exit 및 marker 미생성을 확인하는 회귀 추가. 실제 CEF sandbox nested-job compatibility, child subtree drain, owner/child broker attestation 및 lifetime/relock, protected volume, runtime은 NOT RUN. Product startup에 미연결. WebView2 유지.

### 추가 구현: desktop owner-only suspended CEF staging connection (검증 대기)

CefStageWire serializes strict StageCefChild volumeId + invariant positive decimal handle, exact JSON status fields/duplicate refusal, registered GUID/protection flags. NamedPipeVolumeBroker production-only fixed pipe, 8-second I/O timeout, service PID validation before/after reply, no retry. CefPreparationLease.StageSuspendedChild accepts existing contained suspended child, requires same owner/volume/profile lease before/after staging, checks same live nominated handle and disposes child job on refusal/ambiguous reply. No resume or launch, no invocation from Program. Actual parser roundtrip (64-bit handle precision) and strict response tests added to Windows synthetic check. Pending stage 15-second lifetime, retained pipe process object comparison, revocation on backend errors, executable/DLL trust and full process lifetime still required; actual broker/BitLocker/sandbox NOT RUN. WebView2 remains product engine.

### 추가 구현: retained pipe process identity and pending revocation (검증 대기)

Native.Identify retains actual observed process handle through request; pipe PID/session requery and SameLiveProcess (CompareObjectHandles + live waits) under serial, owner watch duplicates observed handle rather than PID reopen, nominated source/owner and redemption pipe/retained child object comparison. Lock/Detach retire pending before backend status; Execute exceptions retire pending. Synthetic Windows same/different/exited process handle and retained owner watch tests. Initial pipe PID lookup is still a snapshot, not kernel-issued authenticated process handle; handle transfer, malicious same-user manipulation, full service integration and backend failure tests remain unverified. No CEF startup authorization or WebView2 engine switch. Actual Windows service, BitLocker and sandbox NOT RUN.

### 추가 구현: product candidate native preflight wiring (검증 대기)

제품 후보 RunWinMain에서 sandbox_info non-null, no arguments, job membership (any job, not desktop job proof) 후 fixed HKLM registration loader 및 VerifyRegisteredBootstrap을 호출한다. 등록 정보 before/after 비교, one-shot receipt, 모든 실패 및 성공 모두 nonzero exit 29–34; CefExecuteProcess/CefInitialize 미호출, subprocess 미지원, fixture 호스트 unchanged. Native synthetic sequencing test uses mock load/verify, no live registry or broker. Actual CEF bootstrap may load DLLs before RunWinMain: package/loaded image verification must precede resume, current preflight is NOT launch authorization. Existing installer never provisions registry or activates candidate, actual product/BitLocker/service/sandbox NOT RUN.

### 추가 구현: retained CEF runtime file inventory lease (검증 대기)

CefRuntimeFileLease.OpenInstalled uses fixed Program Files/VEIL/Cef path and independently authenticated pins (no registration loader or trust issuance yet). Exact file names, count, length, SHA-256, required host/libcef/resources, no fixture/unknown executable, no redirected ancestors/entries, existing admin-only file ACL helper, Win32 final path and single link, FileShare.Read handles held until disposal. RequireCurrent rechecks inventory and retained handles, not mapped DLLs or all ancestor ACLs. Synthetic Windows CI exercises actual byte/hash/share and missing/extra/duplicate/traversal failures, ACL mocked; no real signed runtime, external DLL load search, ADS, writable ancestor replacement, ongoing directory mutation, handle-to-loaded-image attestation, actual installation or BitLocker verified. Not called by product launch or Program. Next: authenticated administrator runtime pin loader and guarded coordinator, full loader/lifetime audit. WebView2 unchanged.

Native CI 45936147075 for 7dbeb86 failed in product DLL compilation (MSVC C4018 promoted to C2220); fix SID size arithmetic to unsigned size_t and bound fixed/variable SID before IsValidSid. No compiler warning policy relaxation. Exact new head CI pending.

### 추가 구현: fixed administrator runtime registration loader (검증 대기)

Fixed ProgramData/VEIL/cef-runtime-pins.json, separate disabled candidate registration kind (cef-admin-runtime-pins), exact fields, duplicate/unknown property refusal, bounded JSON, canonical pin path/hash/integer length/required files, fixed archive identity, startupEnabled=false only. Retains registration handle FileShare.Read + canonical single-link/ACL recheck and runtime lease. CefPreparationLease.PrepareRegisteredRuntime binds to the authenticated owner before/after preparation, no creation or resume; Program unchanged. Source commit is metadata, not independent build provenance; installer must publish from verified signed evidence and atomically reconcile package/broker/HKLM; no publication or actual launch yet. Real ACL/ancestor/loader/BitLocker unverified. Synthetic parser roundtrip and negative cases added. Windows CI 45936690659 failed because test Reject did not catch InvalidDataException (expected invalid pin refusal); explicit catch added without weakening production validation. Latest CI pending.

### 추가 구현: disabled candidate launch coordinator (검증 대기)

CefDisabledLaunchCoordinator combines owner profile lease, admin runtime pin lease, protected environment with minimal SystemRoot/WINDIR/PATH, atomic suspended child, StageSuspendedChild and one-shot CefDisabledPreflight. Only accepts exit 29 (verified but disabled), local 12s budget checks including stage/rechecks (synchronous broker calls may exceed deadline), job active process accounting/forced terminate and drain before normal lease release. No Program/UI invocation, no CefInitialize, no registry publication. Synthetic test apphost exit 29 and stage rejection exercise sequencing and real process wait; not an actual signed CEF/service launch. Exit code alone is not authenticated proof of a real broker receipt; loader/dependency trust, actual registration, continuously live owner, drain failure and non-forced relock remain gates. On already-disposed job or drain failure cleanup is best effort, not confirmed drain. WebView2 unchanged.

### 추가 구현: one-shot owner confirmation of completed disabled preflight (검증 대기)
Broker retains redeemed child object for 15s, same authenticated owner and generation only, ConfirmCefPreflight consumes receipt and compares duplicated original owner handle to actual exited object plus exit 29 and protected status. Stage blocks while receipt pending; lock/detach/backend failure/owner cleanup revoke receipt. Desktop requires confirmation after drain, no retries. Fix failed CI 45937610146: job active count zero alone does not guarantee process wait signaled; explicitly wait for original process exit. Synthetic object tests and source checks added. Real service/BitLocker/loaded DLL trust and end-to-end receipt not run; no production startup or install activation.

### 추가 구현: creation-time image-load policy and post-bootstrap module directory observation (검증 대기)

Atomic STARTUPINFOEX adds PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_NO_REMOTE / NO_LOW_LABEL / PREFER_SYSTEM32 always-on (bits 52,56,60), confirms Windows process flags before resume and on creation. No fallback. Native disabled candidate sets SetDefaultDllDirectories APPLICATION_DIR|SYSTEM32, verifies current mitigation, enumerates current Toolhelp modules and rejects unknown/noncanonical/directories outside executable directory or System32; snapshot only and may refuse legitimate WinSxS/system modules. Synthetic path classifier and actual managed test apphost mitigation queries in CI. Microsoft Win32 documentation: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute and https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-setdefaultdlldirectories. This is NOT mapped code digest, pre-entry bootstrap loader proof, DLL allowlist enforcement, or continuous module monitoring. No real signed product, broker or CEF/BitLocker runtime launched. Actual CefInitialize still disabled and WebView2 unchanged.

### 추가 구현: native image mapping and backing-file name consistency (검증 대기)

ObserveImageBacking checks VirtualQuery MEM_IMAGE + allocation base, loader path, kernel GetMappedFileNameW device path, canonical DOS and NT path from opened file handle; rejects redirected path components, local hardlinks, non-image mappings, requires product host/DLL observed. Reobserves mapping name and checks full snapshot before and after broker receipt; no retry or CefInitialize. Synthetic native test uses actual Windows test executable mapping, wrong path, private allocation, and data-file mapping. Parent's authenticated runtime inventory lease still supplies on-disk SHA-256 checks; native snapshot is NOT independent hash of loaded code or exact file object binding, cannot detect same-name stale sections, patched private image pages, transient loads, or pre-entry code execution. Actual signed product and OS compatibility (including WinSxS) NOT RUN, native CI pending. WebView2 unchanged.

### 추가 구현: independent broker-side registered module observation (검증 대기)

At redemption, broker independently opens fixed admin runtime registration and verifies the complete installed SHA-256 inventory using shared desktop source, retains files, independently snapshots the actual live child loader modules (Toolhelp with retained process identity checks). Only direct installed application files (registered EXE/DLL, same retained disk file ID/volume serial) or System32 path accepted; host and product DLL required, before/after module inventory equal. Synthetic actual Windows child module snapshot and disk object tests; no real product run. Snapshot and reopened disk object comparison are not kernel section identity or mapped byte attestation; System32 trust remains path-based, legitimate WinSxS modules may be refused. Re-reading the full runtime during redemption may exceed existing receipt/I/O deadline; measure on real signed candidate before enabling and consider pre-stage retained lease in a later change. No startup or installer authorization; CefInitialize remains blocked. WebView2 unchanged.

### 추가 구현: retained broker runtime inventory across staging and redemption (검증 대기)
Full runtime SHA-256 verification moved to broker Stage before issuing 15-second admission; pending owns registration/runtime/file handles along with signed executable and process, disposes on failure, expiry, lock or redemption. Redemption reuses retained runtime and checks module batch instead of repeating whole inventory walks per module. Small host signature/digest still rechecked. Explicit serialized broker access permits different worker threads under existing semaphore; desktop keeps thread affinity. Stage client I/O budget 45s, local 12s redemption budget begins after stage reply; broker 15s lifetime unchanged. Synthetic resource disposal and cross-thread/batch tests. No actual throughput benchmark; hashing remains synchronous under serial and may exceed client budget. Signed installed product/BitLocker/mapped byte trust NOT RUN, no actual CefInitialize, WebView2 unchanged.

### 추가 구현: disabled candidate lifetime rechecks and natural-vs-forced exit separation (검증 대기)

Desktop preflight now polls current authenticated profile/runtime authority and live image policy between process wait slices (at most 250ms per wait, 12s local budget unchanged); loss of authority rejects and drains. Natural job exit must be observed before broker confirmation, with a short non-terminating wait; forced cleanup uses distinct exit code 35, never verified-but-disabled 29. Synthetic actual Windows tests revoke after marker, assert no confirmation, verify forced exit code and non-terminating natural wait. Not continuous or real-time enforcement: synchronous broker/volume checks can block, owner dispatcher messages may be delayed, no actual CEF/browser/subprocess lifespan, service/BitLocker or graceful shutdown verified. Product initialization remains disabled and WebView2 unchanged. Future active runtime authorization needs independent service-side liveness/revocation, safe broker-held containment and actual shutdown/relock verification.

### 추가 구현: independent broker monitoring and scoped candidate termination (검증 대기)
After signed executable and registered runtime verification, retain a termination-capable handle compared to the original nominated process object; keep equivalent handle through redemption. Serialized broker monitors pending and completed disabled candidate slots at 1s delay; rechecks generation, live owner, registered protected volume, expiry, pending child liveness; revoke on error/loss, lock/detach, owner cleanup, failed redemption. Requests termination only on retained verified process, exit code 35, observes exit up to 2s, logs ExitNotConfirmed on failure. Does NOT terminate the whole job or prove complete drain, does not retain failed termination handle for retry, does not guarantee wall-clock interval when backend or semaphore blocked. Synthetic actual Windows process object mismatch/termination checks added; service/BitLocker/real CEF NOT RUN. Actual product startup remains disabled, no new pipe commands, WebView2 unchanged.

### 추가 구현: retained failed candidate termination and bounded retries (검증 대기)
Fix source-contract CI 45941081712 (stale return-new assertion). Revoked pending process handle transfers out of disposable admission; failed termination retains the exact handle, no PID reopen, 30 automatic attempts with 2s delay under broker serialization, logs exhaustion and keeps handle for later cleanup attempts. Failed confirmation also uses stop-or-retain. Block new Stage/Unlock/Detach while unresolved; non-forced Lock remains available, service cleanup refuses detach after verified lock if exit unresolved. Actual retry timing not hard bounded, service restart loses in-memory recovery, failed stop no longer holds the full runtime file lease, not whole-job containment or graceful shutdown. Synthetic ownership/replay tests and source contracts added; real termination failure and service/BitLocker not run. No product startup; WebView2 unchanged.

### 추가 구현: broker-created disabled candidate job primitive (검증 대기)
New disconnected CefBrokerOwnedJob creates its own unnamed kill-on-close job, active process limit 1, no breakaway, issues one reduced assignment/query handle to the authenticated process via DuplicateHandle and same-object checks. Refuses owner membership, verifies single nominated child and job accounting, terminates only internally owned job with exit 35, observes active count. Synthetic Windows test assigns an isolated test child, rejects missing membership and reduced-access termination, verifies whole job termination and process exit. Production atomic assignment, wire/owner generation binding, remote handle lifecycle, kill-on-close when a duplicate remains open, broker restart, full multi-process CEF nested-job compatibility and failed job termination retention are NOT implemented/verified. No broker dispatcher or product startup changes; no arbitrary user-nominated job termination. WebView2 unchanged.

### 추가 구현: direct broker assignment to an already-contained suspended candidate (검증 대기)
Prefer broker-only assignment over issuing remote job handles: open PROCESS_SET_QUOTA|PROCESS_TERMINATE for exact verified process, recheck object and identity, assign internally created single-candidate job, check membership/accounting before resume. Synthetic actual Windows integration exercises desktop atomic job + nested broker job, closes each owner independently and expects child exit. No production dispatcher wiring yet: require nested compatibility evidence first, then retain broker job across pending/redeemed/recovery states. No new pipe command, no remote handle on direct route, existing experimental remote handle helper remains disconnected. Same-user process injection/suspension, service boundary, actual CEF nested sandbox jobs, BitLocker and restart recovery not verified. Product startup remains disabled, WebView2 unchanged.

### 추가 구현: broker-owned job retained through disabled candidate lifecycle (검증 대기)
Previous exact head fe72d71 general CI passed, including real Windows synthetic nested-job containment test (45942445119). Stage now creates a private single-process broker job after executable/runtime pins, directly binds the nominated process, and retains it through redemption and completed receipt. Confirmation checks exact process exit 29 and empty broker job; revocation or failed confirmation retains both job and process handles on failed drain, retries through existing recovery; no remote assignment handle is issued by broker dispatcher. Admission failure during initial job assignment still relies on local job and kill-on-close cleanup, not independently retained drain proof. Actual CEF sandbox subprocess compatibility, signed installed service cross-session assignment, BitLocker, mapped bytes, and restart recovery remain unverified. Single process only, startup disabled, no new wire commands, WebView2 unchanged.

### 추가 구현: separate multi-process nested CEF smoke runner (검증 대기)
New tests/cef/CefNestedSmoke runs only the built Win32 smoke executable with a new temp profile, atomically contained desktop job plus independent test-only nested job without an active-process cap, non-elevated interactive x64 session required. Samples own job members and restricted/low integrity tokens; requires JS smoke exit 0, observed restricted low/untrusted descendant and empty jobs. Standard-user evidence runner adds nestedBuild/nestedWin32 stages, hashes/logs; hosted CI only compiles this runner, never claims browser execution. The production broker job still has limit 1 and startup remains disabled. Sampled token evidence does not prove renderer role, every process sandboxed, all memory mappings or no off-volume writes. Real nested CEF, broker/service/BitLocker and renderer sandbox compatibility NOT RUN. WebView2 unchanged.


### 추가 구현: downloadable unsigned nested smoke bundle (검증 대기)
Native compile workflow packages only the built Win32 fixture plus matching pinned CEF runtime/resources, required licenses and framework-dependent nested runner; upload retained seven days, exact checkout SHA and inventory manifest. No product binary/installer, no hosted interactive launch. Local PowerShell 7 non-elevated x64 runner verifies manifest/commit/files, launches bounded test, rechecks inventory, records JSON/log evidence outside bundle. Expected checkout SHA may be synthetic PR merge SHA. Manifest is unsigned consistency evidence, not independent provenance; verify artifact run before execution. Requires .NET 10 x64 and native runtime prerequisites. Packaging and actual runtime are NOT RUN at change time; no product gates advanced. WebView2 unchanged.


### 추가 구현: review returned nested fixture evidence (검증 대기)
Previous exact PR head bdeaba1 general/native CI passed, artifact 15174370614 from run 45943924095, checkout 5a4d0ab67225e01218e0028dc85a9a1c28d63ddd, 199023604 bytes, archive artifact digest sha256:f66679545e7a5cb6eef8b547ea784740ed99cc5f5540f944e4ae68a679e430c7. New bounded Node evidence reviewer supports this already published bundle; original trusted manifest and exact checkout expected, report hash must match raw manifest, typed sample values and non-root unique descendant PIDs required, independently derives reported restricted-low count and checks natural empty jobs and exit 0. Synthetic negative tests in normal CI. Output only CONSISTENT_REPORTED_SMOKE_PASS, not independent execution or provenance; actual interactive Windows evidence not received, product gates NOT RUN. No startup or WebView2 changes.


### 추가 구현: initial job assignment failure recovery (검증 대기)
Prior exact head 743a263 passed general and native CI. Stage now transfers already-pinned termination handle and any private broker job to serialized stop recovery on exception (including create/assign/bind failure). Before executable/runtime verification no termination authority is acquired. Both job drain and process exit attempted independently even if a job query throws, with final accounting required. Recheck unresolved-stop gate after revocation in same request to prevent fresh admission/Unlock/Detach. Added source contracts. No actual injected service-side assignment failure or OS termination refusal tested; runtime pins are still released after failed staging and not held across recovery, same-user injection/suspension and service restart remain open. Product startup disabled; interactive nested fixture still NOT RUN; WebView2 unchanged.


### 추가 구현: retain runtime pins across redemption and unresolved stop (검증 대기)
Prior exact head af172b3 passed general and native CI. Pending admission now transfers image and runtime inventory pins alongside job/child resources to completion receipt or stop recovery, including initial assignment exception after verified handle acquisition. Only confirmed natural exit and empty job, or successful forced drain with observed process exit, releases all four resources. Failed stop retains them across bounded retries. Synthetic ownership transfer/double-detach and source contracts added. No actual installed service/BitLocker, OS termination refusal, or interactive CEF nested runtime tested. Disk pins are not a measurement of mapped bytes, nor durable across broker crash; same-user races remain unproven. Startup remains disabled and WebView2 unchanged.

### 추가 구현: 잠금 요청 거부 시에도 CEF 인증 철회 (검증 대기)

Lock/Detach 및 서비스 정리 시작 시 실제 backend 상태 조회보다 먼저 CEF 인증 소유자와 자식 admission/receipt를 철회한다. 후보 정리 예외가 발생해도 인증은 먼저 제거되며 기존 unlockOwner crash-relock 감시 토큰은 보존한다. 비강제 잠금이 열린 핸들로 거부되어 볼륨이 아직 unlocked여도 새 CEF 준비는 재인증 전 거부한다. 가짜 backend 회귀에 Detach 거부/HandlesOpen, 새 unlock 후 권한 회복 및 재잠금 성공을 추가했다. 일반 Status 및 실제 잠금 오류 의미, 기존 WebView2 startup은 유지한다. 외부 OS 잠금/재잠금의 미관측 상태 전이, 실제 설치형 broker 및 BitLocker, CEF 실행은 별도 검증 필요. 작성 시 테스트 NOT RUN. 제품 startup disabled.

### 통합: nested smoke 증거 결합 (#228)

정확한 head 475d11a의 일반/네이티브 CI는 PASS (45946397415 / 45946397389), artifact `cef-nested-smoke-56b556458a45625c49b9c15d7a36f43cc62821db` 생성 확인. #226의 잠금 권한 철회 커밋 ce73c94와 변경 경로가 겹치지 않음을 확인하여 두 부모를 유지한 draft 통합 커밋으로 결합한다. 실제 파싱 stdout 원본 SHA-256, 최초 manifest hash 및 inventory 검증을 포함하며 이전 보고서 호환을 위해 검사를 약화하지 않는다. 통합 head CI와 독립 리뷰는 별도 필수. 실제 standard-user nested CEF 및 BitLocker runtime NOT RUN; 제품 startup disabled, WebView2 및 출시 게이트 유지.

### CI 보완: retained process 종료 신호의 독립 검증

통합 head 2d51601의 일반 CI (45947323875)에서 CefProcessBindingCheck가 `Exited child still considered live` 복합 단언으로 실패했다. Rust, .NET 빌드, source/evidence 및 NetworkPolicyCheck는 통과했으나 후속 containment/runtime file 테스트는 건너뛰었다. 실제 실패 조건(비정상 exit code 또는 retained handle liveness)은 기존 로그만으로 구분할 수 없다. 합성 자식 stdin을 명시적으로 flush하고 정확히 복제된 process handle의 WAIT_OBJECT_0를 제한 10초 내 요구하며 이후 managed exit code, production IsRunning, SameLiveProcess 및 기존 nomination 단언을 모두 보존한다. 실패 원인을 진단 가능하게 분리하며 timeout이나 오류를 성공으로 처리하지 않는다. production liveness 정책 변경 없음. 수정 head CI 및 실제 CEF/BitLocker/독립 리뷰 미확인; startup disabled 및 WebView2 유지.

### 추가 검증: 잠금 거부 후 owner 종료 및 자동 재잠금 회귀

정확한 head 82ce76c 일반 CI 45947832401 PASS (직전 실패했던 CefProcessBindingCheck, containment, runtime inventory 포함); native CI 확인 시 진행 중. 표준 사용자 fake-backend suite에 같은 별도 owner process가 Unlock/AuthenticatedOwner/Lock(HandlesOpen)/AuthenticatedOwner 및 Stage 거부를 차례로 받고 종료하는 시나리오를 추가한다. watcher의 실제 HandlesOpen 로그를 먼저 요구한 다음 synthetic marker 제거, 자동 relock, 권한 거부 및 Detach를 확인한다. SYSTEM/elevated/session 0/noninteractive 실행을 테스트 시작 전에 거부한다. README에 CEF 빌드 없이 실행할 방법과 증거 필드를 문서화했다. 이 회귀는 hosted CI에서 문법만 검사되고 실제 Windows broker 실행 NOT RUN. 실제 BitLocker, CEF, 독립 리뷰 및 출시 검증은 별도. Product startup disabled; WebView2 unchanged.

### 추가 검증: disabled preflight와 양쪽 job 및 종료 객체 확인 통합 회귀

정확한 head edff03c 일반 CI 45948372574 PASS, native CI 확인 시 진행 중. 기존 CefContainmentCheck에 실제 Windows synthetic apphost를 사용하여 desktop atomic job, broker-owned single candidate job, retained process handle, CefDisabledPreflight.Run 및 Native.MatchesExitedNomination을 하나의 순서로 묶는 두 시나리오를 추가했다. 정상 exit 29와 두 job 종료 확인 후에도 잘못된 owner handle을 nomination으로 제시하면 성공을 반환하지 않음을 요구한다. 확인 실패를 강제 종료로 성공 처리하지 않고 정확한 객체의 자연 종료를 유지한다. callback은 합성 broker dispatch이며 실제 pipe/service, signed runtime, BitLocker 또는 CEF 실행이 아니다. 최신 CI NOT RUN; product startup disabled, WebView2 unchanged.

### 추가 구현: 별도 CefPreflight 빌드의 보호 프로필 사전 검사 진입점

정확한 head ad8c946의 일반 CI 45948786981 및 native CI 45948786928 PASS. 제품 Debug/Release와 별개의 MSBuild Configuration=CefPreflight에서만 고정 --cef-protected-preflight 인자를 허용한다. 해당 구성은 일반 탐색 진입점을 컴파일에서 제외하고 설치된 고정 Desktop 경로, 관리자 아닌 실행, 사용자 단일 실행 mutex, 실제 등록 broker, 새 보호 볼륨 인증을 요구한다. 비밀번호 버퍼를 정리하고 기존 disabled coordinator를 호출한 뒤 session Dispose 및 등록 볼륨이 잠겼거나 분리되었는지 확인할 때만 exit 29 반환. CEF 브라우저 시작 없음, 평문 fallback 없음, 일반 제품 Debug/Release 동작 유지. CI에 별도 구성 빌드 및 설치되지 않은 경로/정상 Release 거부 검사 추가. 이 검증 빌드는 배포물/installer가 아니며 별도 격리 VM의 승인된 서명·등록 및 runtime pins 준비가 선행되어야 한다. 동기 owner thread 검사 중 WPF 잠금 알림 dispatch 지연 및 실제 Windows/BitLocker/CEF runtime은 미검증이며 제품 활성화 금지. 신규 CI NOT RUN, 독립 리뷰 대기.

### 추가 구현: 독립 STA 알림 pump (CefPreflight 전용)

정확한 head a703549 일반 CI 45949344666 PASS, native CI 확인 시 진행 중. 검증 전용 entry는 owner dispatcher와 별개인 STA background thread에서 기존 WTS/power hidden HWND를 등록하고 Dispatcher.Run을 유지한다. 잠금 요청은 Interlocked로 단방향 latch, owner는 Volatile.Read로 기존 authority 체크에서 관찰한다. owner 종료 요청은 비동기 dispatch만 하며 lease/IPC/cleanup은 기존 owner thread에 유지. 등록 실패/10초 시작 제한 또는 pump 종료는 성공으로 처리하지 않으며 종료 시 dispatcher shutdown 및 제한 join. 기본 Debug/Release는 기존 owner HWND 사용 유지. 소스 계약 추가; 신규 CI, 실제 WTS/절전/BitLocker/CefPreflight 실행 NOT RUN. OS 알림 전달 자체의 지연 및 동기 broker I/O 중 즉시 중단은 보장하지 않으며 실제 제품 CEF 활성화 금지.

### 추가 구현: 정확한 인증 볼륨의 종료 확인 (검증 대기)

정확한 head 00d8370 일반 CI 45949822918 PASS, native CI 확인 시 진행 중. CefPreflight 전용 성공 경로에 명시적 cleanup을 추가: 원래 인증 GUID와 현재 볼륨 일치 요구, 비강제 Lock 응답 성공, 같은 GUID 및 잠금/ReadyToUnlock 상태 재조회, 이번 실행에서 Attach한 경우 Detach 성공 및 분리 상태 재조회. 잠긴 메타데이터의 FullyEncrypted/ProtectionOn은 attestation이 아니므로 요구하지 않는다. 오류/부재/다른 GUID/잠금 거부/분리 실패는 검증 성공 불가, 기존 Dispose와 broker crash watcher는 best-effort 정리만 수행. 원래부터 attached였으면 분리하지 않는다. 순수 synthetic 12개 시나리오 및 source contract 추가, 기존 Debug/Release Dispose 정책 변경 없음. 새 head CI, 실제 broker/BitLocker/CEF, 독립 리뷰 NOT RUN. 알림 및 외부 OS 상태 변화의 관측 한계는 유지. Product startup disabled.

### 추가 구현: 서명 증거에서 비활성 런타임 핀 계획 생성

정확한 head 38acf5b 일반 CI 45950466353 PASS, native CI 확인 시 진행 중. 기존 별도 CMS 서명·Authenticode·제한 ACL·정확한 후보 인벤토리 검증을 재사용해 C# runtime registration parser 계약의 JSON을 생성하는 읽기 전용 계획 도구를 추가한다. 고정 staging만 읽고 stdout에 계획만 반환; 설치 경로/ProgramData/서비스/볼륨 변경 없음. startupEnabled=false 유지, source commit·archive hash·파일별 길이/SHA-256 유지. 기존 evidence 합성 CI에 정상 계획 및 잘못된 signer/commit/파일/manifest 거부 추가. 폐기용 VM 준비 런북에 별도 Desktop 구성, 서명 및 후보 staging, 등록 계획 검토와 실제 게시 미구현 차단점을 명시. 신규 CI 및 실제 서명/서비스 등록/BitLocker NOT RUN; 제품 활성화 금지.

### 추가 구현: 검증 전용 전체 패키지 서명 증거

정확한 head 5a45daf 일반 CI 45950908056 PASS, native CI 확인 시 진행 중. 별도 cef-preflight-vm-package CMS 형식으로 Desktop/CefPreflight, Broker 및 Cef 후보의 모든 파일을 크기/SHA-256 인벤토리에 포함한다. 원래 CEF 후보 CMS 검증과 runtime pins 일치를 요구하고 VEIL EXE/DLL Authenticode 및 별도 CefPreflight ProductName 마커를 검사한다. 일반 Release와 마커 분리를 CI에서 확인한다. 마커와 서명은 실행 동작 또는 빌드 출처의 독립 attestation이 아니다. 설치 가능/제품 시작 플래그는 false만 허용, 기존 패키지 및 설치 스크립트 변경 없음. 합성 CMS/변조/누락/추가/마커 거부 테스트 추가, 실제 Authenticode 및 ACL은 합성 시험에서 mock. 신규 CI, 실제 서명/설치/BitLocker/CEF runtime 및 독립 리뷰 NOT RUN. 게시/복구 트랜잭션은 다음 단계.

### 추가 구현: 검증용 전체 패키지 스테이징 및 등록 핀 계획

정확한 head 342b6cd 일반 CI 45951446755 PASS, native CI 확인 시 진행 중. 기존 설치/등록/서비스가 없는 깨끗한 VM에서만 고정 별도 staging에 전체 검증용 payload와 두 CMS 증거를 관리자 제한 ACL로 복사하는 준비 도구 추가. 복사 전 검증, 원본 증거 해시 및 복사본 비교, 원본/복사본 재검증 후 Desktop/Broker/CEF SHA-256 및 runtime pins 계획을 출력한다. 기존/부분 staging은 자동 삭제·재사용하지 않는다. 등록/설치/서비스/볼륨 변경 없음. 합성 테스트 추가, 신규 CI 및 실제 ACL/서명/VM NOT RUN. 설치 게시·복구·실제 BitLocker 검증은 남아 있음.

### 추가 구현: 보호 볼륨 등록 초안 및 준비 기록

정확한 head 12c4661 일반 CI 45951965294 PASS, native CI 확인 시 진행 중. 별도 staging을 재검증하고 Desktop/Broker/CEF 이미지 핀과 명시된 시험 볼륨/보호기/사용자 SID를 연결하는 broker.json/client.json/cef-runtime-pins.json 초안 생성. 실제 실행 시 기존 복구 확인 루틴으로 명시적 기존 보호기 확인, 비강제 재잠금과 분리 후 재검증하며 실제 VHDX를 일시 mount/unlock할 수 있다. 비밀번호를 기록하지 않는다. 고정 별도 Program Files 디렉터리에 CreateNew/Flush(true)/재조회 후 파일 해시와 prepared-only journal을 마지막에 작성. 기존/부분 초안 보존. 합성 핀·입력 거부·파일 충돌 테스트 추가. 설치/서비스/실제 등록 변경 없음. 신규 CI, 실제 복구/ACL/VM/전원 차단 및 독립 리뷰 NOT RUN; 초안 재검증 및 게시/복구 상태 기계는 미구현.

### 추가 구현: 준비 저널 및 독립 등록 입력 재검증

정확한 head 9b15df5 일반 CI 45952554083 PASS, native CI 확인 시 진행 중. 읽기 전용 check-cef-preflight-registration.ps1은 고정 staging/준비 디렉터리, 설치 부재, 분리된 시험 VHDX 및 독립 운영자 입력을 사용한다. 등록 JSON에서 생성된 opaque volume ID만 읽고 나머지는 서명 검증 stage와 독립 입력으로 재구성한다. 바이트 수준 정확한 writer 형식 및 파일별 해시, prepared-only journal 대조; 누락/추가/변조/재해시된 잘못된 정책/다른 SID/보호기/프로필 거부. 부분 준비와 실제 설치 흔적을 구분하여 보존, 자동 복구 없음. 저널은 서명 또는 실제 복구 검증 증명이 아니며 TOCTOU를 제거하는 보유 핸들도 아니다. 합성 회귀 추가, 신규 CI 및 실제 VM/독립 리뷰 NOT RUN. 실제 게시 및 서비스 등록/장애 복구 상태 기계는 미구현.

### 추가 구현: 검증 전용 설치 파일 게시 (서비스 미등록)

정확한 head 61de04f 일반 CI 45953063242 PASS, native CI 확인 시 진행 중. 명시적인 폐기용 VM 옵션으로 기존 설치·서비스·pending·transaction 부재 및 복구 자격 증명 재확인/분리 후, 서명 검증 stage/등록 초안을 별도 제한 pending에 복사하고 원본/복사본을 재검증한다. 별도 intent 및 단계별 create-new 저널 후 동일 부모 디렉터리 이동으로 실제 Program Files/ProgramData VEIL 파일을 게시한다. 두 경로 이동은 각각 별개이며 서비스 생성/시작, 자동 복구/삭제 없음. 합성 실제 파일 복사/이동 및 중간 거부 테스트 추가. 실제 서명/ACL/BitLocker/서비스/전원 차단, 신규 CI, 독립 리뷰 NOT RUN. 게시 상태 재검증·복구 및 서비스 등록은 후속. 제품 CEF 비활성 유지.

### 추가 구현: 게시 상태 재검증 및 명시적 제한 재개

정확한 head bfc09e5 일반 CI 45953601321 PASS, native CI 확인 시 진행 중. 서명된 staging/등록 초안과 독립 입력으로 intent 및 순서가 있는 저널 prefix를 바이트 단위 재구성하고 실제 pending/live 배치와 교차 검증한다. verified-copy 기록 이후 알려진 4개 경계에서만 명시적 -Resume을 허용하며 매 단계 전체 파일/등록을 재검증, 기존 복구 자격 증명 및 재잠금·분리를 다시 확인한다. 복사 검증 전 중단, 불일치, 변조, 중복 경로, 서비스 존재, 연결 볼륨은 거부하고 삭제·덮어쓰기 없음. 완료 재개는 읽기 전용. 합성 실제 디렉터리 이동/저널 테스트 추가. 단일 관리자 폐기용 VM 전제, 실제 전원 차단·동시성·ACL·서명/BitLocker/서비스 NOT RUN, 신규 CI 및 독립 리뷰 대기. 서비스 등록/시작과 제품 CEF 초기화는 미구현.

### 추가 구현: 검증 전용 비활성 브로커 서비스 등록

정확한 head 7eff11c 일반 CI 45954145871 PASS, native CI 확인 시 진행 중. 전체 files-published-only 상태 및 시험 볼륨 복구 자격 증명 재확인 후 별도 SCM 등록 intent를 작성하고 LocalSystem/Disabled/Stopped 서비스 생성, 시스템·관리자 관리/인증 사용자 조회 전용 DACL 설정 및 재확인한다. 고정 경로·서비스 상태·DACL·전체 게시 파일을 다시 대조한 뒤 registered-disabled 기록을 작성. 실패 시 기록·SCM 상태를 보존하고 기존 서비스 채택/시작/삭제 없음. 합성 SCM 콜백 및 실제 저널/SDDL 파싱 테스트 추가. 신규 CI 및 실제 SCM/DACL/BitLocker/VM/독립 리뷰 NOT RUN. 등록 중단 확인, 서비스 시작 및 CEF 초기화는 남아 있음.

### 추가 구현: 서비스 등록 재검증 및 일회성 시작/정지 시험

정확한 head 1f765d0 일반 CI 45954697017 PASS, native CI 확인 시 진행 중. 별도 등록 저널과 실제 Disabled/Stopped SCM identity/DACL 및 게시 증거 재검증. 명시적 RunProbe에만 복구 자격 증명 재확인 후 Manual 및 Running 관측, finally에서 Disabled/정상 Stop/분리 확인. 실패 기록 보존, 완료 기록은 정상 전체 시험에서만 생성. 실제 서비스와 볼륨은 실행하지 않았으며 강제 종료/분리 및 자동 재개 없음. 합성 시작/정지·시작 응답 실패 후 정리 테스트 추가. 신규 CI 및 실제 SCM/ACL/BitLocker/전원 차단/독립 리뷰 NOT RUN. 일반 사용자 사전 검사 세션 및 CEF 초기화는 남아 있음.

### 추가 구현: 일반 사용자 보호 프로필 검증 세션

정확한 head cea72bb 일반 CI 45955235904 PASS, native CI 확인 시 진행 중. 비활성 등록과 이전 서비스 전용 probe 저널을 재구성해 확인한 뒤 명시적 폐기용 VM/RunProtectedSession 옵션으로 별도 시험 계정의 Windows 자격 증명을 요청, 등록 SID와 일치 확인. 서비스 제한 시작 중 고정 서명 Desktop EXE를 해당 계정으로 --cef-protected-preflight만 실행, 관리자 측 Process에서 최대 5분 종료 관측, 정확한 int 29/동일 서비스 PID/시험 VHDX 분리 확인 후 Disabled/Stopped 및 등록 재검증. 별도 저널 기록. 실패/timeout은 강제 종료 없이 정상 정리 시도 및 상태 보존. 합성 callback 테스트 추가. 실제 Windows Secondary Logon/토큰/SCM/BitLocker/서명/VM/독립 리뷰 NOT RUN; 일반 사용자 계정 구성과 독립 OS trace는 별도 확인 필요. 29는 CEF 활성화/브라우징이 아님. 신규 CI 대기.

### 추가 구현: 일반 사용자 실행 프로세스 토큰 직접 관측

직전 head 78589ea의 일반/native CI는 작업 시작 시 실행 중. Process.Start가 반환한 프로세스 핸들에서 Win32 GetProcessId/QueryFullProcessImageName/OpenProcessToken 및 TokenSessionId/ElevationType/Elevation/IntegrityLevel을 읽고, 등록 SID·고정 실행 경로·동일 대화형 세션·medium integrity·비상승/기본 토큰·관리자 그룹 부재를 요구한다. 동일 Process 객체 종료 코드 int 29와 tokenVerified/PID/세션 관측이 있어야 protected-preflight 기록 생성. 실제 CI 자체 프로세스 토큰 읽기 및 합성 잘못된 관측 거부 시험 추가. 시작 후 스냅샷으로 실행 전 suspended 검증이나 런타임 토큰 불변성 증명이 아니며 실제 별도 사용자/VM/BitLocker/CEF NOT RUN. 신규 CI 및 독립 리뷰 대기.

후속 CI 조회: 78589ea 일반 CI 45955792334 FAIL (별도 preflight package 거부 테스트), Rust PASS. 로그는 공통 Reject에서 거부되지 않은 입력만 보여주므로 정확한 케이스는 미확정. 합성 서비스 객체가 같은 참조를 공유해 PID 교체가 이전 관측에도 반영되는 결함을 발견, scalar PID 스냅샷으로 수정하고 Reject 호출 위치 진단을 추가. 신규 CI로 재확인 필요.

후속 CI: 4e2b607 일반 CI 45956408765 FAIL. Rust PASS 및 .NET 빌드 성공 후 Windows PowerShell 5.1 파서에서 cef-preflight-package.test.ps1의 정규식 삽입 중복/문자열 파손 확인. 이전 정상 파싱 파일(78589ea) 기준으로 토큰 관측 및 거부 케이스 구간을 재구성하고 중복 제거. PID scalar snapshot 수정은 유지. 테스트 내용의 검증 조건은 완화하지 않음. 새 CI 실행 결과 및 실제 VM 확인 전 완료 처리 금지.

후속 CI: 6e69f5c 일반 CI 45956868760 FAIL. Rust PASS, .NET 빌드 및 Windows PowerShell 5.1 파싱 통과. 실제 CI 자체 프로세스의 Inspect 호출에서 SecurityException Access is denied. TOKEN_QUERY 핸들을 WindowsIdentity/WindowsPrincipal로 감싸는 추가 권한 요구가 원인 후보이므로 해당 사용을 제거하고 TokenUser/TokenGroups/TokenIntegrityLevel을 GetTokenInformation으로 직접 읽도록 수정. 가변 구조체의 크기, SID 포인터와 길이, 그룹 수 및 플랫폼 정렬을 확인하며 deny-only 관리자 그룹도 거부. 기존 PID/세션/상승/무결성 검증 유지, 자체 프로세스의 활성 관리자 그룹 교차 검사 추가. 새 CI 결과 및 실제 VM은 미검증. 이전 실패의 직접 원인 확정 및 보호 세션 회귀 테스트 완료는 새 CI 확인 후 판단.

### 추가 구현: 전체 서명 패키지와 등록/게시 기록 결합

정확한 head f73b4b3 일반 CI 45957413402 PASS, native CI 확인 시 진행 중. 등록된 세 EXE 해시만으로는 동일 서명자·커밋의 다른 DLL 인벤토리를 구분하지 못하므로 staging에서 네 서명 증거(JSON/CMS)의 SHA-256 및 정규화된 서명자 지문을 검증 전후 관측하고 등록 준비 저널 format 2와 게시 intent format 2에 결합한다. 게시 경계에서 fresh stage plan 재검증을 추가하며 기존 format 1 준비 기록은 거부한다. 동일 EXE/서명자/커밋이나 변경 DLL로 다시 서명된 별도 패키지 테스트, 이전 형식 준비 저널 거부 테스트 추가. 기존 VM 준비 자료는 덮어쓰지 않고 깨끗한 스냅샷에서 다시 준비해야 한다. 관리자 단일 실행 전제의 스냅샷 비교이며 실제 동시성/서명/SCM/BitLocker/VM/독립 리뷰는 미검증. 보호 세션 결과 재검증 및 실제 CEF 실행은 후속 작업.

### 추가 구현: 보호 세션 기록 읽기 전용 재검증

정확한 head 196ceec 일반 CI 45958003951 PASS, native CI 확인 시 진행 중. 보호 세션 관측에 broker PID를 포함하고 최종 정리 기록에 관측 SHA-256을 결합한다. 별도 검사기는 현재 전체 서명 패키지/등록/게시 기록, 서비스 전용 시험 및 Disabled/Stopped DACL/시험 볼륨 분리를 검증하고 네 보호 세션 기록을 정확한 바이트로 재구성하여 재조회한다. 누락/추가/공백 변조/다른 등록/관측 PID 교체 거부 회귀 테스트 추가. 기존 관측 해시 없는 기록 거부. 관리자 수집 기록의 일관성 검사이며 독립적인 과거 실행 증명이나 활성화 권한은 아님. 신규 CI, 실제 SCM/BitLocker/VM/CEF 및 독립 리뷰 NOT RUN.
