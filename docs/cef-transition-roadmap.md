# VEIL CEF 전환 로드맵

2026-11-27 기준으로 기존 Windows x64 WPF 앱의 엔진을 WebView2에서 CEF로 전환한다. 개발에는 버전을 고정한 사전 빌드 CEF 배포본을 사용한다. 현재 제품은 WebView2로 동작한다. CEF 1단계에서 [독립 Win32 호스트](../apps/cef-smoke/README.md)의 로컬 JavaScript 실행, 렌더러 제한 토큰, 프로세스 종료를 확인했다. WPF 호스팅과 사이트 격리 실측은 남아 있다.

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
