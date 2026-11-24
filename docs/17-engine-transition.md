# 17. 자체 엔진 전환 기반

## 현재 단계

| 구성 | 상태 |
|---|---|
| IBrowserEngine | 세션 초기화 및 뷰 생성 계약 구현 |
| IBrowserEngineView | 기본 탐색, 내부 페이지 표시, 확대 및 종료 계약 구현 |
| WebView2BrowserEngine | 실제 데스크톱 세션에 연결 |
| WebView2EngineView | WPF 뷰 및 기본 탐색 어댑터 연결 |
| 자체 렌더러/JavaScript 실행기 | 미구현, 선택 불가 |
| 대체 엔진 보안 통합 | 미구현, 제품 활성화 금지 |
| Windows 빌드·런타임·보안 회귀 | NOT RUN |

## 호출 구조

```text
BrowserSession
  IBrowserEngine
    WebView2BrowserEngine
      Program.CreateEngine (기존 보호 프로필 / DNS / proxy / scratch)
  BrowserTab
    IBrowserEngineView
      WebView2EngineView
        WebView2 WPF view / CoreWebView2
  Existing host policy
    HTTPS-first, threat checks, permission controls, SmartScreen,
    vault download interception, native UI, process failure handling
```

엔진은 한 세션에서 하나의 초기화 작업만 가진다. 새 창은 같은 초기화와 환경을 공유한다. 초기화 실패 후 다른 프로필·정책·엔진으로 대체하지 않는다. 브라우저 탭을 만들기 전 지원하는 어댑터인지 검사하며, 기존 네이티브 이벤트 연결 순서와 SmartScreen 적용을 유지한다. 뷰 생성은 새 웹 탐색을 시작하지 않는다. 기본 탐색 경계는 HTTP(S) 및 about:blank만 허용하고 기존 NavigationStarting 보안 검사를 대체하지 않는다.

## 아직 WebView2에 연결된 부분

| 영역 | 후속 분리 계약 |
|---|---|
| WPF 호스트, 팝업 및 탭 재부착 | 화면 호스트와 수명·스레드 계약 |
| NavigationStarting/Completed, 오류 | 취소 가능한 탐색 이벤트 및 오류 분류 |
| CookieManager, CDP Storage | 출처/파티션 기반 데이터 접근 및 삭제 |
| PermissionRequested, SmartScreen | 기본 거부 권한 및 위협 정책 결과 |
| DownloadStarting / 응답 재요청 | 실제 응답 스트림, 취소·인증·발행 계약 |
| Proxy, DNS, WebRTC, QUIC | 엔진별 강제 정책 및 실패 검증 |
| 프로세스 종료, Favicon, 오디오, Find | 수명·페이지 상태·편의 기능 이벤트 |

네이티브 접근을 억지로 일반화하지 않는다. 특히 다운로드 응답 스트림과 네트워크 정책은 어댑터의 이름만 바꿔 해결할 수 없다.

## 이후 구현 순서

1. 인터페이스 경계의 회귀 검증, 이벤트·프로세스 수명 계약 분리.
2. 비실사용 테스트 전용 엔진 하네스와 합성 웹 문서/응답 테스트 작성. 제품 경로에는 등록하지 않는다.
3. 검증된 엔진 기반 선택 평가: 렌더링, JavaScript, DOM, TLS/인증서, 접근성, 미디어 및 보안 업데이트 유지 가능성.
4. 보호 프로필 저장·복구와 렌더러 격리, 네트워크 강제 및 실제 다운로드 스트림 통합.
5. 엔진 간 데이터 이전은 별도 명시적 복구 가능한 마이그레이션 설계. 기존 프로필 직접 공유 금지.
6. 신규 엔진의 Gate A~E, 호환성, 업데이트 및 공급망 검증 후 제품 채택 결정. 첫 버전에는 신규 엔진을 활성화하지 않는다.

## 일괄 검증 추가 항목 (NOT RUN)

- [ ] 보호 프로필 없는 시작, WebView2 환경 재정의, DNS 적용/프록시 오류에서 기존 거부 경로 유지
- [ ] 단일 초기화, 다중 창 공유, 탭 이동 및 복원, 초기화 실패·종료 경쟁 및 컨트롤러 정리
- [ ] 새 탭/주소 검색/뒤로·앞으로/새로고침/정지/확대, 내부 페이지 표시 및 기존 탐색 차단
- [ ] SmartScreen, 외부 URI, 사이트 권한, 비밀번호, 사이트 데이터, 다운로드 금고의 실제 회귀
- [ ] 엔진 강제 종료 후 세션 보존 및 재실행, 보호 볼륨 밖 쓰기·프록시 누출 추적
- [ ] 설정에 현재 런타임 정보만 표시, 미검증 엔진 선택·자동 전환 경로 없음

기존 출시 상태와 보안 요구의 우선 문서는 SECURITY_STATUS.md 및 11-security-release-gates.md다.
