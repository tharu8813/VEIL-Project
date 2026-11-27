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
