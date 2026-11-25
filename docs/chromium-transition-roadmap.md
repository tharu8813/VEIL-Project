# Chromium 직접 포크 전환 로드맵 (C0~C11)

- 결정일: 2026-11-25. 사용자 요청: WebView2에서 Chromium 직접 포크로 전환.
- 상태: C0 IN PROGRESS. C1~C11 NOT STARTED. 현재 제품은 WebView2이며 보안 게이트 상태를 변경하지 않는다.
- 조사 기준: 223e6f3a8c80da8441464317e0573c7a7191523b (main, PR #110 병합). 조사 시 열린 PR #111은 브로커 진단 로그 보안 수정이며 독립적으로 유지한다.
- 기존 [ADR-0009](decisions/ADR-0009-engine-boundary.md), [보안 상태](SECURITY_STATUS.md), [출시 게이트](11-security-release-gates.md), [기존 Alpha](decisions/ADR-0010-internal-alpha.md)를 함께 적용한다. 과거 PASS는 신규 엔진 증거가 아니다.

## 아키텍처와 운영 원칙

Chromium의 chrome/ 및 Views를 기반으로 독립 Windows x64 브라우저를 개발한다. Blink, V8, sandbox, Site Isolation, Network Service, 미디어와 접근성의 업스트림 구조를 유지한다. 기존 WPF 화면은 디자인 및 UX 참조로 보존하며 C++/Views/WebUI로 필요한 기능을 이식한다. Rust 보안 코어와 기존 C# BitLocker 브로커를 가능한 한 유지하되, 인증된 IPC와 프로세스 수명 계약을 새로 검증한다. 브로커 인증·ACL·세션·서버 신원 확인을 약화하지 않는다. 평문 저장, 보호 모드의 직접 연결, 자동 엔진 폴백, 샌드박스 해제 금지.

Chromium 소스 전체를 VEIL 저장소에 넣지 않는다. 외부 checkout과 고정 commit/tag, DEPS 및 depot_tools/toolchain 기록, 작고 검토 가능한 패치 및 VEIL 모듈을 관리한다. 원본 브랜치와 패치 적용 상태를 분리하며 현재 apps/, crates/, tests/를 이동·삭제하지 않는다. 새 파일은 chromium/ 및 scripts/chromium/ 등 독립 경로부터 추가한다. 초기 후보는 Windows Stable에 대응하는 upstream revision이며 정확한 pin은 공식 릴리스와 소스 tag/commit을 교차 확인한 뒤 C1에서 기록한다. 미확정 핀으로 제품 빌드하지 않는다.

버전 제안: 기존 1.0.x 내부 시험 경로를 보존하고 Chromium은 1.1.0-alpha.N 후보로 관리한다. 기존 호환 계약이 깨지는 경우 SemVer 재검토. 정식 버전 번호, 첫 안정 출시 엔진, 설치 프로그램 정책은 호환성 및 출시 승인 후 확정한다. Chromium 보안 패치의 업데이트 주기는 기능 버전과 분리한다. 현재 VERSIONING 및 패키지 코드 변경 없음.

## 순서 및 합격 기준

| 단계 | 작업 | 완료 조건 | 상태 |
|---|---|---|---|
| C0 | 설계 결정, 의존성 목록, 기존 보존, 빌드 환경 조사, CI 운영 설계 | 결정/조사 문서 검토, 실제 Windows 준비 상태와 누락 조사 기록 | IN PROGRESS |
| C1 | 공식 Stable revision 선택, depot_tools/DEPS/toolchain 고정, 원본 checkout, GN 빌드 | Windows x64 원본 Chromium 빌드·실행, sandbox 검증, 로그 및 재현 절차 | NOT STARTED |
| C2 | VEIL 제품명/아이콘/실행 파일, 독립 프로필, 내부 페이지의 권한 경계 | WebView2 없는 개발자용 독립 실행, 합성 데이터만, 기존 제품과 프로필 분리 | NOT STARTED |
| C3 | Views/WebUI 탭, 주소창, 북마크, 설정, 다운로드 팝업, 다중 창, IME/DPI/접근성 | 기존 핵심 UX의 회귀 검증 | NOT STARTED |
| C4 | Rust 코어 및 인증된 브로커, 보호 볼륨, 잠금/복구, 임시/캐시 경로 | Gate A/B: 보호 없는 시작 차단, 쓰기 추적, 종료/전원 차단 복구 | NOT STARTED |
| C5 | 실제 다운로드 응답 스트림을 금고에 연동, GET/POST/Blob/Data/SW 유형별 검증, 취소/재개 | Gate C: 지원 유형별 암호화·변조·중단·ENOSPC·누출 실측, 미지원은 차단 | NOT STARTED |
| C6 | DoH/프록시/HTTPS/IPv6/WebRTC/QUIC/추적·쿠키 정책, 외부 통신 목록 | Gate D: 정책별 패킷 실측, 중단/전환 시 누출 0, 미검증 보호 홍보 금지 | NOT STARTED |
| C7 | SmartScreen 대체, 위협 목록 서명·신선도·폐기 및 오류 표시, sandbox/권한/내부 페이지 보호 | 위협 탐지/오류/실패 동작 및 격리 검증; 미확인 상태를 안전으로 표시하지 않음 | NOT STARTED |
| C8 | 마이그레이션 도구, 원본 암호화 백업, 설정/북마크/기록/금고 호환, 로그인 이전 가능성 | 원본 불변, 복구 검증; 쿠키/키/로그인 이전은 지원 가능한 것만, 불가 시 재로그인 | NOT STARTED |
| C9 | 업스트림 보안 릴리스 감시, 패치 rebase, CI/서명/설치/업데이트/SBOM | 실제 upstream 버전 변경, 업데이트 장애 복구, 서명·라이선스·취약점 처리 실증 | NOT STARTED |
| C10 | Gate A~E 전체, 실제 웹 호환성, 성능·장애·IME·DRM 지원 범위, WebView2 의존 제거 | 모든 필수 증거와 출시 검토, 미지원 기능 고지 | NOT STARTED |
| C11 | 내부 Alpha → Beta/RC → 정식, 단계별 별도 승인 | 제품 출시 정책 및 보안 기준 충족, 기존 엔진은 보존 브랜치/이력으로 유지 | NOT STARTED |

C1 이후 C3의 일반 UI 개발과 C4~C7의 보안 개발은 병행 가능하지만 C2의 개발자용 실행을 실제 사용자 프로필과 연결하지 않는다. C9의 보안 업데이트 설계는 C1부터 시작한다. C4 이전에는 합성 데이터 외 사용 금지. 확장 프로그램, DRM/Widevine, 코덱, Google 계정 동기화는 Chromium 기본 호환을 가정하지 않고 정책·라이선스·실측에 따라 별도 승인한다.

## C0 의존성 조사 (소스 표본 확인)

| 실제 파일 | 관찰 내용 | 이전 계획 |
|---|---|---|
| apps/desktop/Veil.Desktop.csproj | .NET 10 WPF, Microsoft.Web.WebView2 1.0.4370.26, veil-vault.exe 패키징 | 기존 유지, 새 C++ 제품 별도 빌드 |
| apps/desktop/Program.cs | CreateEngine, WEBVIEW2_ 환경 차단, 경로/엔진 인수, 보호 세션 | Chromium 시작 전 보호 게이트, 외부 플래그 우회 방지 설계 |
| apps/desktop/BrowserSession.cs, Engine/IBrowserEngine.cs | WebView2BrowserEngine 및 직접 CoreWebView2Environment 접근, 좁은 뷰 계약 | 수명·정책을 참조하고 새 네이티브 구조 구현 |
| apps/desktop/BrowserWindow.Tabs.cs, BrowserWindow.Network.cs | 직접 CoreWebView2 탐색/이벤트/오디오 | Views, navigation throttle 등 실제 Chromium 통합 지점은 핀 선정 후 조사 |
| apps/desktop/DownloadVault.cs | CoreWebView2 인수, HttpClient 재요청, veil-vault serve | 다운로드 요청/응답 계층 직접 연결 연구; Rust 프로토콜 보존 검증 |
| apps/desktop/BrokerClient.cs, ProtectedProfileSession.cs | named pipe 서버 신원 확인 및 잠금, 열린 핸들 시 강제 분리 금지 | 기존 C# 서비스 보존, C++ 인증된 클라이언트/수명 통합 |
| apps/broker/Veil.VolumeBroker/ | 브로커 구현 트리 존재 | 보안 계약 검토 및 유지; 전수 소스 검토는 남음 |
| crates/veil-security/ | Rust 코어, vault CLI, password store, backup/credentials/generation 및 테스트 | 암호 형식 및 IPC 우선 재사용; ABI 신규 노출은 별도 설계 |
| .github/workflows/veil-checks.yml | windows-latest .NET restore/build, Linux Rust 검사 | 기존 CI 유지, 새 Chromium 작업은 별도 러너/경로 |

이 표는 위 기준 SHA의 트리와 일부 파일의 직접 확인이다. 전체 호출 그래프, XAML 및 패키지/스크립트/테스트 의존성 전수 확인은 NOT RUN. inventory 스크립트를 실제 checkout에서 실행한 뒤 결과를 검토할 것.

## C0 빌드 환경 / CI

공식 [Windows 빌드 가이드](https://chromium.googlesource.com/chromium/src/+/main/docs/windows_build_instructions.md)와 선택한 revision의 동일 문서를 확인한다. NTFS 100 GB 이상 여유 공간은 공식 최소 조건이며, VEIL은 300 GB 이상을 권장한다(추정, 실제 측정 필요). Windows x64, Git, depot_tools, VS/Windows SDK는 고정 revision의 요구 버전에 맞춘다. Windows와 WSL은 depot_tools checkout을 공유하지 않는다.

기존 PR CI는 유지한다. 신규 소스 동기화/전체 빌드는 별도 Windows 빌드 머신이나 격리된 러너에서만 실행하며, PR에서 신뢰할 수 없는 코드를 자격증명 있는 self-hosted runner로 실행하지 않는다. 전체 빌드는 pin 변경 및 출시 후보에서, 증분 빌드 및 대상 테스트는 관련 변경에서 수행한다. 보안 게이트/실제 VM 검증은 생략하지 않는다. Rust/기존 테스트 범위는 그대로 유지한다. 장기 보안 업데이트는 최신 Stable 및 긴급 취약점 감시·패치 병합·검증·서명 배포·지원 중지 기준을 마련한다.

## C0 후속 검증 준비 (2026-11-25)

[업스트림 후보 기록](chromium-upstream-candidate.md): 일반 Stable 158.0.8127.89 후보를 조사했다. 정확한 tag/commit은 아직 미확정이다. 인터넷 연결된 신뢰 머신에서 읽기 전용 resolve-tag 도구를 실행해 후보의 정확한 참조를 확인한다. 기존 CI에 inventory 실행, Windows 호스팅 러너 준비 상태 관측 및 태그 파서 합성 시험을 추가한다. CI 결과 확인 전 모두 NOT RUN이며 호스팅 러너 정보는 사용자 빌드 머신이나 Chromium 실행 증거가 아니다.

## C0 CI 실측 및 C1 준비 (2026-11-25)

PR #113 CI [45621410099](https://github.com/tharu8813/VEIL-Project/actions/runs/45621410099): 합성 태그 테스트 3/3 PASS. PR merge-test checkout 2410a59ef1d70e39f9fb159aaf703c6fad10af29에서 추적 범위 182개, 키워드 일치 105개, 읽기 실패 0. WebView2 43파일/226줄, 보호 프로필 34/138, 금고 34/147, 네트워크 34/606, 브로커 27/81 (중복 포함, 의존성 그래프 아님). 호스팅 AMD64/NTFS, 여유 109.9 GiB, gclient/gn/autoninja 미발견. 사용자 빌드 머신 증거 아님. 실제 Chromium 코드 검색 미러에서 158.0.8127.89 commit 1de57fa19722fa6d6841d4f596c394d015580656 관측. canonical remote 교차검증 및 depot_tools pin 미완료. [C1 준비 런북](../chromium/README.md)과 read-only 기본 source sync 도구, GN 후보 설정 추가. C0 IN PROGRESS / C1 준비만, 실제 checkout/build/보안 검증 NOT RUN.

## C0 증거 및 다음 행동

- [x] 전환 방향 문서화(이 PR에서 제안), 기존 보안 요구 유지.
- [x] 기준 SHA 트리 및 주요 엔진 결합 지점 표본 조사.
- [x] 독립 개발 브랜치 사용, 기존 소스/테스트 이동·삭제 없음.
- [x] PR #113 merge-test checkout에서 inventory 키워드 조사 실행 및 결과 검토. 전체 호출 그래프는 NOT RUN.
- [ ] Windows 빌드 머신, SDK/VS, Git/depot_tools, NTFS/공간 실제 확인 (NOT RUN).
- [ ] Stable 후보 tag/commit 및 toolchain 확인 (NOT RUN; C1 pin 선정 전).
- [ ] 이 PR 검토/CI 및 이후 자동 개발 작업의 로드맵 연결 확인 (미완료).
- [ ] C1 진입 승인 및 원본 빌드 (NOT RUN).

기존 보안 PR을 존중하고, 자동 개발은 병합된 main의 이 문서에서 C0~C11 상태를 확인한 뒤 이어간다. 기존 ADR-0010 관련 긴급 보안 수정은 계속 허용하되 Chromium 게이트를 대신하지 않는다. 현재 이 문서는 출시 승인이나 새 엔진 구현 증거가 아니다.
