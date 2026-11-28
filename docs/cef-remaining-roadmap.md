# VEIL CEF 남은 작업 로드맵

기준일: 2026-11-27 (KST). 이 문서는 기존 [CEF 전환 로드맵](cef-transition-roadmap.md)의 후속 실행 계획이다. main의 실제 제품 엔진은 WebView2이며, CEF 실험 코드의 구현 여부와 제품 런타임 검증 여부를 분리한다. 코드 병합, 제품 활성화, 공개 배포는 서로 다른 결정이다.

## 0. 통합 이력 및 현재 차단점 (P0)

**2026-11-27 사용자 결정:** 기존 작업을 즉시 main에 통합하고 실패 및 미검증 항목은 이 로드맵으로 관리한다. 아래 PR 표는 통합 전 스냅샷이다. 통합 커밋은 기존 main, #226, #180, #181의 정확한 head를 부모로 보존하고 #222~#225/#227/#228의 조상 이력도 유지한다. 다운로드 fixture 충돌은 최신 per-view/parallel 구현에 redirect/인증 GET 변경을 재적용하고 최신 표준 사용자 runtime runner를 유지하는 방식으로 해소한다. 코드 통합 자체는 CI 성공, 독립 리뷰, 제품 활성화 또는 배포 승인이 아니다.

조회 당시 main 코드 기준: `fd04e0088048acd783c8e7eddb8fec3991e9ece0`. 통합 PR은 [#226](https://github.com/tharu8813/VEIL-Project/pull/226), head `e48f6bebb0918c58a3fad58021325406f27244fd`. 다음 목록은 조회 시점 스냅샷이며, 이후 작업에서는 정확한 최신 SHA를 다시 확인한다.

| PR | 조회 당시 head | 관계 / CI | 남은 병합 조치 |
|---|---|---|---|
| #222 | cafa33b | #226 조상, 일반/native PASS | 개별 중복 병합 금지; 통합에 포함 |
| #223 | de2f223 | #226 조상, 일반/native PASS | 동일 |
| #224 | f646e9a | #226 조상, 일반 PASS | 동일 |
| #225 | 2a9370f | #226 조상, 일반/native PASS | 동일 |
| #227 | 7665a9e | #226 조상, 일반 PASS | 동일 |
| #226 | e48f6be | draft; 일반 FAIL, native PASS | 통합 후 P0 실패 해결 및 최신 main CI/독립 리뷰 |
| #180 | 531882e | 별도 분기; 일반/native PASS, main과 충돌 | 충돌 해소, redirect fixture 재검증 |
| #181 | 6d170b2 | #180 기반 draft; 일반 PASS | #180 의존성 유지, 인증 쿠키 fixture 재검증 |

[#228](https://github.com/tharu8813/VEIL-Project/pull/228)은 #226에 이미 통합된 이력으로, main에 별도로 병합하지 않는다. #222~#225와 #227의 정확한 head가 #226의 조상임을 비교 API로 확인했지만 최종 통합 내용이 모든 기능을 보존하는지는 리뷰로 검증한다. 현재 승인된 독립 리뷰 없음. Copilot quota 실패는 승인 아님.

- [x] P0-01: [#226 합성 다중 프로세스 시험 실패](https://github.com/tharu8813/VEIL-Project/actions/runs/45960862547)를 로컬 Windows에서 재현하고 수정했다. root handle 신원과 Job 소속은 정상이고, `CREATE_NO_WINDOW`로 자식을 시작했을 때 `conhost`까지 포함되어 accounting이 active=3, total=3, terminated=0이었다. 자식이 루트의 콘솔을 상속하도록 수정한 뒤 기존 검사와 production cap=1을 유지한 상태로 통과했다. 별도 Job을 생성하는 실제 CEF는 P2-01에서 검증한다.
- [x] P0-02: #226의 CI 실패 이력을 보존하고, 후속 [#229](https://github.com/tharu8813/VEIL-Project/pull/229)의 검증 커밋에서 일반 CI와 native compile CI가 모두 PASS했다. 합성 root/descendant 쌍, 외부 PID, 변조 생성시각, cap=1, 두 Job 종료/강제 종료, 프로세스 핸들 보존을 검사했다.
- [ ] P0-03: 통합 범위와 기존 PR 변경의 보존 여부를 독립 리뷰. 승인 리뷰를 가장하지 않는다. 사용자 결정으로 이번 통합은 독립 리뷰 전에 수행하며, 이후 보안 검토 및 제품 활성화 기준은 유지한다.
- [x] P0-04: 통합된 #180/#181의 합성 GET/redirect/401/HttpOnly cookie/취소/원본 hash, 기존 per-view 및 parallel 동작을 실제 CEF로 검증했다. Rust 금고 회귀도 통과했다.
- [ ] P0-05: 이번 통합 후 정확한 main SHA의 CI 및 PR 상태 확인. 실패를 삭제하거나 성공으로 바꾸지 않고 해결 작업 기록. 이후 일반 병합에는 CI/리뷰/충돌 검사 기준 적용. 브랜치/PR 이력 및 실패 증거 보존.

**완료 조건:** 통합 코드가 main에 있고 필수 CI 성공 및 필요한 검토 기록이 존재하며, 기존 WebView2 제품이 회귀하지 않는다. 제품 CEF 활성화는 여전히 금지.

## 1. 보호된 사전 검사 실측 (P0/P1)

#226에는 별도 CefPreflight 구성, 보호 세션/프로필 lock, 등록 런타임 SHA-256 및 retained file handles, broker one-shot admission, 독립 Job, 종료 확인, 패키지 CMS/게시 및 복구 저널, 비활성 SCM 등록/시험, HKLM bootstrap 등록, 일반 사용자 토큰 관측과 시험 기록 재검증 코드가 작성되어 있다. 이번 통합 대상에 포함, 실제 Windows BitLocker 설치 시험 NOT RUN.

- [ ] P1-01: 폐기용 Windows x64 VM, 별도 관리자/일반 사용자, 독립 서명 인증서, 시험용 VHDX 및 복구 보호기를 준비한다. 실사용 설치/파일/계정 금지.
- [ ] P1-02: 정확한 통합 커밋에서 CEF 후보/desktop/broker 빌드, 서명, 전체 CMS 검증, 제한 ACL staging, 등록 형식 2, 게시 및 HKLM 등록, SCM Disabled/Stopped 등록. VM runbook은 #226의 `docs/cef-preflight-vm-runbook.md` 참조.
- [ ] P1-03: 실제 ACL/registry link/ancestor reparse/hardlink 및 경합을 검증하고 설치 트랜잭션의 중간 실패·전원 차단·부분 등록 대응을 검증한다. 관리자 작업 직렬화 및 증거 교체 방지 보완. 실제 SCM DACL/서비스 설정 재검증.
- [ ] P1-04: 표준 사용자 Secondary Logon 토큰 관측, BitLocker unlock, 정확한 등록 GUID, 독점 lock, protected child env, native broker redemption, 코드 29, 정상 drain/relock/detach. 정상 종료/강제 종료, owner death, lock/절전, attach/detach 변경, 알림 펌프 중단/경합, 오류 및 timeout을 검증한다.
- [ ] P1-05: 읽기 전용 검사로 signed package, registry, broker registration, service-only probe, protected session evidence를 대조. Procmon/ETW 등 독립 OS 증거를 보존한다. 관리자 수집 저널 자체는 독립 실행 증명이 아니다.

**완료 조건:** 실제 VM에서 정확한 사전 검사 증거 및 실패 시 재잠금/분리 증거 확보. 코드 29는 verified-but-disabled이며 브라우징 성공이 아님.

## 2. CEF 다중 프로세스와 샌드박스 (P1)

- [x] P2-01: 일반 사용자 Windows에서 nested Win32 smoke를 실제 실행해 제한 토큰과 낮은 무결성의 renderer 표본을 확인하고, 두 Job이 정상 종료 후 비었음을 검증했다. 역할별 전체 수명 검증은 P2-02 이후에 남아 있다.
- [x] P2-02: CEF browser/renderer/GPU/utility/network의 역할, 모듈, 토큰, AppContainer 및 Job 소속을 합성 실행에서 관측했다. 교차 사이트 renderer 분리와 오프라인 VM의 독립 Procmon 추적을 확인했다. 이 결과는 시험한 실행에 한정되며, 제품 전체 수명 보증은 P2-03/P2-04에 남아 있다.
- [ ] P2-03: 등록된 image와 runtime lease 및 broker 세션 generation을 전체 브라우저 수명에 결합하는 별도 다중 프로세스 admission 설계. 하위 프로세스마다 동일 계정/세션 및 적절한 sandbox 권한/역할, retained object, 모듈 핀을 검증. unknown role/image, breakaway, sandbox disable, 권한 철회/재인증 실패를 fail closed 처리. 기존 one-shot disabled candidate와 별도 protocol 및 tests.
- [ ] P2-04: 보호 세션 권한 철회/desktop 또는 broker crash 때 모든 프로세스와 runtime lease 정리, job empty 확인 후 relock. timeout/강제 정리를 정상 종료와 구별.
- [ ] P2-05: 별도 시험 빌드에서 제한된 보호 경로와 로컬 합성 페이지로 실제 CefInitialize/CefExecuteProcess/CefShutdown 연결. 암호화 볼륨 부재 또는 초기화 오류 시 시작 차단, 임시 프로필 fallback 금지.

**완료 조건:** 실제 CEF 다중 프로세스 sandbox 및 보호 세션 수명/정리 검증. 제품 startupEnabled=false는 완료 기준 충족 전 유지.

## 3. 브라우저 기능 통합 (P1/P2)

- [ ] P3-01: 실제 VEIL WPF 창과 IBrowserEngine/IBrowserEngineView 연결. 주소창, 탭, 팝업, 탐색/리다이렉트/오류, 새로고침, 종료의 실제 사용자 시나리오.
- [ ] P3-02: 다중 탭/창, private context, veil:// 내부 페이지, 북마크/기록, 권한, 사이트 데이터, 로그인 저장/채우기, 다운로드 UI. 개발용 capability를 product로 허위 승격 금지.
- [ ] P3-03: 보호된 쿠키/스토리지/캐시/임시 파일/로그/덤프 경로 및 실제 재시작 지속성. private 탭의 비지속성, 디스크 부족/강제 종료/복구. 보호 볼륨 밖 쓰기 전수 추적과 Windows OS 잔여 정보 평가.
- [ ] P3-04: IME, DPI, 키보드/접근성, GPU/동영상, 탭 안정성, 메모리/CPU/시작 시간 회귀. WebView2와 기능 비교 및 안전한 오류 UX.

**완료 조건:** 실제 보호 CEF 제품 후보에서 기능 및 보호 증거 확보. 현재 WebView2를 그대로 유지하는 동안 격리된 시험만 진행.

## 4. 다운로드 및 네트워크 보호 (P1/P2)

- [ ] P4-01: #180/#181 통합 후 로컬 fixture에서 redirect, 인증 쿠키, 401, 중복 요청 방지, 정확한 GET origin, 잘림/큐 포화/취소를 실제 CEF로 실행. 원본과 복호화 SHA-256 일치.
- [ ] P4-02: 금고 연결을 제품 다운로드 UI/실제 보호 프로필로 통합. 지원 가능한 요청만 허용, POST/blob/data/service worker/지원 불명 유형은 실증 전 차단. 디스크 부족, 취소, 강제 종료, 암호화 복구, 다운로드 경로 외 쓰기 추적.
- [ ] P4-03: 프록시 및 DoH/HTTPS 보호 연동, DNS/IPv6/WebRTC/QUIC 직접 연결 우회 검증. proxy outage와 live 전환 fail closed. Gate C/D 결과와 네트워크 모드별 지원 상태 명확화.

**완료 조건:** 지원된 다운로드/네트워크 모드의 Gate C/D 독립 증거 확보.

## 5. 이전, 배포 및 최종 엔진 전환 (P2)

- [ ] P5-01: 기존 WebView2 데이터 보존, 별도 CEF 프로필의 설정/북마크/기록 등 지원 범위 이전 및 롤백. 비밀번호/쿠키는 안전하고 호환되는 명시적 경로만 제공; 이전 불가 항목을 안내.
- [ ] P5-02: pinned prebuilt CEF 버전/해시, 라이선스·NOTICE·SBOM, 서명 및 보안 업데이트 정책, 패키지 크기/설치, update/rollback/uninstall/recovery.
- [ ] P5-03: 깨끗한 Windows VM의 실제 설치, 권한/잠금/복구/업데이트/중단 시험 및 [Gate A~E](11-security-release-gates.md), [Internal Alpha 기준](decisions/ADR-0010-internal-alpha.md) 재검증. 기존 WebView2 증거를 CEF 증거로 재사용하지 않는다.
- [ ] P5-04: 별도 후보 빌드에서 출시 판단, 사용자 안내 및 독립 검토 후 CEF 기본 엔진 전환. 오류 시 비보호 fallback 금지. 안정화 후에만 WebView2 제거 및 의존성 정리.
- [ ] P5-05: 최종 문서/README/SECURITY_STATUS/CHANGELOG 및 릴리스 기록 갱신. 실제 통과한 범위만 지원·보안 주장.

## 운영 규칙

1. 우선순위는 P0 병합 차단점 → P1 실제 보호 런타임 → P2 제품 기능 및 배포. 여러 단계의 독립 작업은 병행 가능하되 선행 검증에 의존하는 활성화는 금지.
2. 각 일감에 정확한 SHA, PR/CI 링크, 변경 파일, 시험 종류(compile/synthetic/runtime/VM), 결과(PASS/FAIL/NOT RUN), 남은 위험을 기록한다.
3. 정량 진행률은 실제 검증 근거가 없으면 추정치로만 표시한다. 임의로 전체 완료율을 높이지 않는다.
4. 이번 통합은 사용자가 명시적으로 요청한 예외로 기록한다. 이후 기본 정책은 테스트·보안 검증 실패, 독립 리뷰 미완료, 충돌 시 해당 코드 병합 보류 및 차단점 갱신이다. 코드 통합은 제품 배포 승인이 아니다.
5. 기존 자동 개발 작업에 Chromium 직접 포크 지시가 남아 있다면 별도 사용자 승인으로 CEF 전환 로드맵에 맞춰 변경한다. 중복 작업과 병합 충돌을 방지한다.

## 이번 통합 시점의 오류 및 검증 부채

- #226 일반 CI 45960862547 FAIL: `Fixture pair refused (mode 1): root-membership` (CefContainmentCheck/Program.cs:180). 정확한 원인은 아직 미확정. Rust/.NET/소스 검사 통과, 해당 job 시험 실패. native CI 45960862278 PASS (compile 및 제한된 검사, 실제 브라우저 실행 아님).
- #180/#181의 오래된 전역 상태와 최신 per-view 소유 요청 구현 간 충돌을 수동 해소. 통합 후 native/managed compile 및 소스 계약, 실제 Windows CEF 테스트는 NOT RUN. 최신 runtime wrapper를 유지했으므로 기존 `-ReuseExtractedCef` 옵션은 도입하지 않는다.
- 독립 리뷰 없음. 실제 BitLocker/SCM/HKLM/CEF 실행, OS 쓰기 추적, Gate A~E는 미검증. 기존 WebView2와 startupEnabled=false 유지.

## 통합 후 P0 검증 기록 (2026-11-27 KST)

- 기준 커밋 `66b4c8221563ef8d905e3ee0b77ba10fcdef543b`. [#226](https://github.com/tharu8813/VEIL-Project/pull/226)은 이 커밋으로 병합됐고, PR head는 `e48f6bebb0918c58a3fad58021325406f27244fd`다. PR 일반 CI FAIL, native compile CI PASS, 승인 리뷰 없음.
- [기준 커밋의 일반 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45962004465) FAIL. 다운로드 handoff와 parallel fixture의 소스 계약 두 개가 통합 전 코드 형태를 검사하고 있었다. `tests/security/tab-lifetime-contract.test.mjs`에서 현재의 동일 출처 검사, parallel별 원본 경로, handoff의 원본 URL 및 일회성 상태 초기화를 검사한다. 로컬 소스 계약 139개 PASS.
- 로컬 미커밋 수정으로 `apps/broker/Veil.VolumeBroker/CefBrokerOwnedJob.cs`에 시험 전용 거절 진단을 추가하고, `tests/security/CefContainmentCheck/Program.cs`에서 자식의 콘솔 상속과 두 Job 각각의 명시적 강제 종료를 검증했다. Windows 합성 시험 PASS. 프로세스 핸들을 보존해 두 프로세스의 종료와 강제 종료 코드 35를 확인했다.
- 로컬 Rust 금고 회귀 PASS, CEF WPF 관리 코드 Release compile PASS. 수정본의 원격 일반/native CI, 실제 CEF 다운로드·nested sandbox 실행과 보호 VM 검증은 NOT RUN. P0-02~P0-05는 열린 상태로 유지한다.

## 후속 PR 및 CEF 실행 기록 (2026-11-27 KST)

- [초안 #229](https://github.com/tharu8813/VEIL-Project/pull/229), `d378a5eda5e5d4b27f5423b68beebe32961be802`. [일반 CI PASS](https://github.com/tharu8813/VEIL-Project/actions/runs/45963056358), [native compile CI PASS](https://github.com/tharu8813/VEIL-Project/actions/runs/45963053759). #226 및 #180/#181과 #222~#225/#227의 로드맵 head가 통합 커밋의 조상임을 재확인했다. 독립 승인 리뷰는 대기 중이다.
- 같은 SHA의 로컬 Windows 일반 사용자 합성 실행에서 native/managed build, Rust 금고 빌드, Win32, nested Win32 및 WPF 다운로드 PASS. 다운로드는 한 번의 인증 GET, HttpOnly 쿠키 전송, 익명 요청의 자격 증명 부재, redirect 미추적, 잘림·포화·병렬 취소, 각 정상 응답을 복호화한 전체 바이트와 예상 원본의 일치를 확인했다. 명시적 SHA-256 비교와 인증 실패 401 응답의 실제 CEF 경로는 별도 증거가 필요하다.
- nested Win32는 종료 코드 0, 두 Job 정리 및 낮은 무결성의 제한 토큰 표본을 기록했다. 프로세스 역할별 표본 대응과 전체 수명 검증은 남아 있다.
- 전체 로컬 실행 FAIL. 긴 임시 경로에서 탭 단계 2의 CacheStorage.open이 내부 오류로 실패했다. 짧은 경로를 사용한 진단에서는 양쪽 저장소 초기화와 쿠키 검증을 통과했으나 활성 탭 닫기에서 종료가 완료되지 않았다. 실패 로그와 실행 증거는 로컬 임시 디렉터리 `veil-cef-p0-d378a5e-17dd7ad5b9124af3b3f869781ba6468a/evidence`에 보존했다. 보호 VM과 제품 게이트는 NOT RUN.
- P0-04 후속 변경은 인증 handoff를 두 번 명시적으로 요청해 각각 401/200을 한 번씩 관측한다. 401에서는 응답 바이트와 금고 객체가 없고, 성공한 인증 및 병렬 전송에서는 금고에서 복원한 SHA-256이 HTTP 원본과 일치한다. 새 WPF 합성 다운로드 시험 PASS, 소스 계약 139개 PASS. 변경 파일은 `apps/cef-smoke/wpf/FixtureVault.cs`, `apps/cef-smoke/wpf/WpfSmoke.cs`, `tests/cef/wpf-smoke.mjs`다. 전용 시험의 실행 로그는 같은 evidence 폴더의 `download-auth-wpf.stdout.log`에 보존했다.
- 기준 `00427191d80bcf1bd8e49fe9ebc18cddd8e9de5a`에서 [일반 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45965912473) 및 [native compile CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45965912543) PASS. 후속 탭 수정은 CEF의 개별 탭 닫기 준비 알림을 WPF 호스트에 전달하고, 디스크 캐시 삭제를 합성 전용 `Default` 요청 컨텍스트로 제한한다. 짧은 임시 경로의 전용 탭 실행 PASS. 러너는 빌드 폴더와 별도로 짧은 임시 경로를 만들고 결과 JSON에 기록한다.
- 탭 수정 커밋 `5947ea2410f6f864e09f0a3f480380832c7f0fc0`의 [일반 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45967544723), [native compile CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45967544558), 일반 사용자 전체 합성 실행 PASS. `veil-cef-5947ea2-f7fec5ef81164bef8df495524f519f75/evidence/result.json` SHA-256은 `88E4783BEC79AB28B2D9992C833ED971DE84E416EF2E4D30B6740A26C1BC11C8`이다.
- 독립 nested 번들의 첫 실행은 종료 코드 0 및 양쪽 Job 정리 후, Windows 캐시 파일이 번들 안에 생성되어 인벤토리 FAIL. `CefNestedSmoke`의 시험 전용 자식 환경에서 Windows 캐시 경로를 scratch로 고정한 뒤 번들 실행과 전후 인벤토리 PASS. 역할별 renderer 토큰 대응과 제품 보호 VM 검증은 남아 있다.
- 기준 `abd3af3e1647ddfbfa27c38e65a32d6b8ab5c024`의 [일반 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45968280506), [native compile CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45968280510), 전체 Windows 합성 실행 및 독립 nested 번들 PASS. 후속 nested 러너는 한 프로세스 핸들에서 역할·토큰을 대조해 renderer, GPU, network, utility 및 AppContainer 여부를 기록한다. 일반 사용자 로컬 실행에서 무결성 RID 0의 제한 renderer를 확인했다. 원본 명령행은 기록하지 않으며, Windows 내부 명령행 조회를 사용할 수 없으면 해당 역할을 unknown으로 취급한다. 새 증거는 제한된 renderer 표본을 요구하고 이전 증거에는 renderer 판정을 부여하지 않는다.
- 역할 시험 커밋 `699450ed873f90bcb9d6f461e1f95be572dab617`의 [일반 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45974362401), [native compile CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45974362240), 일반 사용자 nested 번들 및 증거 검사 PASS.
- P2-02 진행: 시험용 러너에서 살아 있는 process handle로 각 표본이 양쪽 Job에 속하는지 확인한다. browser와 descendant의 모듈 기본 파일명 및 실행 경로 기준 분류(runtime/Windows/other)를 기록하고, 조회가 불가능하면 목록을 null로 남긴다. 실제 실행에서 browser, renderer, GPU, utility, network 표본과 런타임 디렉터리의 `libcef.dll` 로드를 확인했다. 이는 모듈 해시 핀이나 전체 수명 보증이 아니다. 사이트 간 renderer 분리와 전체 수명 관측은 미검증이다.
- 모듈 관측 커밋 `ec1f16a38627833fbf1377f8f434c28dd6a935ed`의 [일반 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45982006989), [native compile CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45982007022), 일반 사용자 nested 번들 및 증거 검사 PASS.
- P2-02 사이트 격리 시험: CEF Win32 smoke가 `127.0.0.1` 상위 페이지에 동일 사이트 프레임과 `localhost` 교차 사이트 프레임을 실제 로드했다. 로컬 CDP 호출에서 교차 사이트 프레임의 정확한 URL에 해당하는 out-of-process iframe target 한 개와 동일 사이트 대조군의 target 부재를 확인했다. 전용 런타임 시험 PASS. 제품 구성 및 전체 renderer 수명 관측은 남아 있다.
- 사이트 격리 시험 커밋 `affd601e7bd6c2587b5085b5e7e5a24697df68d1`의 [일반 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45983496116), [native compile CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45983496193), 일반 사용자 전체 합성 실행 8단계 PASS.
- P2-02 수명 관측: root가 정지된 상태에서 두 Job을 완료 포트에 연결해 시작·종료·비어 있음 알림을 수집한다. 일반 사용자 초기 실행에서 두 Job 각각 시작 10건, 종료 10건, 비어 있음 1건을 확인했다. 증거 검사기는 알림에만 나타난 PID, 시작 또는 종료 알림에 없는 표본, 짝이 없는 시작·종료를 표시한다. Windows의 Job 알림은 전달을 보장하지 않고 PID를 전달하므로, 최종 종료 판정은 기존 root 핸들과 양쪽 Job의 실제 상태 확인을 사용한다. 완전한 수명 추적 및 독립 OS 증거는 남아 있다.
- Job 관측 커밋 `6728ba8aa8c617059e0dc12b810ac0b8544e8426`의 [일반 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45993327625), [native CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45993327493), 일반 사용자 전체 합성 실행 8단계 및 독립 nested 번들 PASS. 두 Job에서 알림으로만 관측한 PID는 각각 3개였다.
- 독립 OS 추적 준비: 호스트 일반 사용자 세션의 CEF 전용 WMI 시작 이벤트 구독이 거부됐다. 오프라인 `Veil-VM-TESTER`는 확인 당시 전원이 꺼져 있었다. Procmon CSV와 번들 증거를 대조하는 분석기에 대해 일치, 누락, 다른 이미지, PID 재사용을 합성 시험했다. 실제 관리자 VM Procmon 수집은 NOT RUN.

### P2-02 Procmon 대조 실행

폐기용 오프라인 VM의 관리자 세션에서 Procmon으로 프로세스 활동을 캡처한다. 일반 사용자 세션에서 같은 SHA의 nested 번들을 한 번 실행한다. 번들 실행 전에 수집을 시작하고 종료 후 수집을 멈춘다. 하위 프로세스 이름이나 경로로 수집 단계에서 제외하면 예기치 않은 자식이 빠질 수 있다. 내보낼 때 `Process Create`와 `Process Exit`를 유지하고, 영어 열 이름과 원래 이벤트 순서를 보존한다.

```powershell
node scripts/check-cef-process-trace.mjs <commit> <bundle-manifest.json> <bundle-evidence-dir> <trace.csv> <guest-smoke-exe-path>
```

원본 PML, 내보낸 CSV, 번들 매니페스트, `result.json`과 `stdout.json`을 같은 실행의 증거로 보존한다. 분석기는 root 생성과 각 종료, 두 Job과의 불일치, 살아 있는 표본이 없는 PID, PID 재사용 모호성을 출력한다. `MATCHED_CAPTURE`는 내보낸 추적의 대조 결과이며, 수집 손실 확인과 제품 게이트 판정은 별개다.

**실제 오프라인 VM 결과.** 커밋 `60b4f37b87f3d5f7202afd2e65b648437f11de01`의 nested 번들을 일반 사용자로 실행하고, 별도 관리자 셸의 Procmon으로 동시에 수집했다. 번들 PASS, 두 Job 종료 후 비어 있음 확인. Procmon은 root와 자식 7개의 시작 및 종료를 모두 기록했고, 모든 시작은 두 Job 알림과 일치했다. GPU PID 2844의 정상 종료는 Procmon에만 있었으며 두 Job의 종료 알림에는 없었다. 그 외 종료 불일치, 다른 실행 이미지 및 PID 모호성은 관측되지 않았다. 분석기 결과 `INCOMPLETE_OR_MISMATCHED_CAPTURE`를 보존했다. 알림 누락 원인과 제품 게이트는 미검증이다.

호스트 로컬 증거는 임시 폴더 `veil-cef-guest-export-dc3e1303`의 `trace.pml`, `trace.csv`, `comparison.json`, `manifest.json`, `evidence/result.json`, `evidence/stdout.json`에 있다. CSV SHA-256은 `371B69CA2931D8218A1E0AF7303C87D5524A07607D3DBBA8D28B8AB896783BD7`이고, PML SHA-256은 `7DF40EBFC993213447651BAFF29D4BD3EAF5E68783874F9B3E3EAB4F2CF7F5AA`다. 호스트의 시험 전용 임시 VirtualBox 공유는 내보낸 뒤 해제했다.

GPU PID 2844의 Procmon 종료는 root 종료보다 약 60ms 앞섰다. 이 기록만으로 완료 포트 알림이 생성되지 않았는지 또는 전달 과정에서 빠졌는지는 판단할 수 없다. 후속 시험 러너는 살아 있을 때 검증한 각 프로세스의 핸들을 보존하고, 반복 표본의 핸들이 동일한 객체인지 확인한 뒤 정상 정리 후 각 핸들의 종료 신호와 종료 코드를 기록한다. 로컬 CEF 실행에서 표본 7개의 종료 코드 0과 두 Job의 정리를 확인했다. 새 증거 검사는 핸들로 확인한 종료와 Procmon 종료의 차이를 별도로 표시한다. 이전 VM의 Job 알림 불일치는 보존한다.

**보존 핸들 검증 후속 VM 실행.** 커밋 `308e8e832b6b5e7beb91ad452ff9c054923105ff`의 번들을 일반 사용자로 실행했다. Procmon 시작·종료 각 7건과 두 Job의 알림이 일치했고, 관측한 7개 프로세스 모두 보존 핸들에서 종료 코드 0을 확인했다. 두 Job은 비었으며, 분석기 결과는 `MATCHED_CAPTURE`다. 이전 실행의 GPU 종료 알림 누락 원인은 여전히 미확정이다. 호스트 임시 폴더 `veil-cef-retained-guest-308e8e8-r`에 증거를 보존했다. CSV SHA-256 `55282378DB9DE91D11284A86B9E14BA0DE81AA7AD9088A4F4B55BF2C55B4F266`, PML SHA-256 `849C887024EE2EED79F4608CE1AA33B0F70A9DD16173EDEA03B4463720E27CD0`. VirtualBox 임시 공유는 내보낸 뒤 해제했으며, 보호 VM 설치·제품 게이트는 NOT RUN이다.

### P1 보호 VM 준비 기록

- 커밋 `308e8e832b6b5e7beb91ad452ff9c054923105ff`의 소스 아카이브로 Desktop CefPreflight, Broker Release와 CEF 비활성 후보를 빌드했다. 네이티브 후보 인벤토리 234개 PASS. 빌드에서 나온 PDB와 WebView2 API XML 문서는 전달본에서 제외했다. 비서명 묶음 SHA-256은 `2678F22C4B3D74855CAC782EF1D369874F2ADF9988536980B46E381327D72EB2`다.
- 오프라인 `Veil-VM-TESTER`의 기존 VEIL 서비스, Program Files/ProgramData 설치 폴더 및 HKLM 등록 키가 없는 것을 확인했다. 게스트에서 아카이브 해시와 CefPreflight 빌드 표식을 확인하고 `C:\Users\vboxuser\VeilPreflight308Input`에 풀었다. 임시 공유를 해제했다.
- 폐기용 자체 서명 코드 서명 인증서의 개인 키는 게스트 관리자 계정의 반출 불가 저장소에 두고, 공개 인증서는 오프라인 게스트의 신뢰 저장소에 등록했다. 인증서 지문은 `016D4ED89AE4F7ACADFACEFBCDEC8AF1E2CE45AE`다. 관리자 제한 ACL에서 데스크톱, 브로커, CEF 후보의 EXE/DLL 6개 Authenticode 검증 PASS. 시험 서명에는 타임스탬프를 사용하지 않았다.
- CEF 후보 CMS 및 후보 스테이징 재검증 PASS. 독립 런타임 핀 계획의 최초 실행은 상위 staging 폴더까지 열거해 CMS 확장자를 후보 파일로 처리하면서 FAIL. `scripts/plan-cef-runtime-pins.ps1`의 대상 경로를 실제 `candidate` 하위 폴더로 수정했다.
- 수정한 도구의 해시를 게스트에서 대조한 뒤 독립 런타임 핀 계획, 전체 payload 분리 CMS, 제한 ACL 전체 스테이징 및 복사본 재검증 PASS. 계획의 `startupEnabled=false`와 `installable=false`를 확인했다. VM 결과 화면은 호스트 임시 폴더 `veil-preflight-308e8e8-7704bf5e/vm-media/staging-result.png`에 보존했다.
- 게스트의 제한 ACL 폴더에 동적 2 GiB 시험 VHDX `C:\Program Files\VEIL.cef-preflight-test-media\profile-test.vhdx`를 생성하고 GPT/NTFS로 초기화했다. 볼륨 레이블 `VEILCEFTEST`, 관측한 볼륨 GUID `4e366859-18a3-4bf4-8d77-620faef2bb8b`, 분리 상태 `Attached=False`를 확인했다. 결과 화면은 같은 호스트 임시 폴더의 `vhdx-ntfs.png`에 보존했다.
- 게스트에서 별도 로컬 계정 `VeilCefTest`를 임의 암호로 생성했다. 계정 활성 상태와 Users 직접 구성원을 확인했고 Administrators 직접 구성원 검사도 통과했다. 임시 DPAPI 파일의 삭제를 확인하고 운영자가 시험용 Windows 암호를 직접 설정할 보안 프롬프트를 열었다. BitLocker PowerShell 명령의 존재와 VHDX 분리 상태를 확인했다. 보호기 설정, 게시, HKLM/SCM 등록 및 보호 세션 실행은 NOT RUN.
- VHDX를 다시 연결하여 동일 GUID를 Windows `Win32_EncryptableVolume`에서 확인했다. 드라이브 문자 없음, `ProtectionStatus=0`, 검사 후 `Attached=False`. 운영자 Windows 암호 및 별도 BitLocker 암호의 연속 보안 입력을 열었다. 보호기 생성 스크립트는 호스트 임시 폴더에서 구문 검사를 통과했다.
- 운영자 입력 후 시험 VHDX의 BitLocker 암호화 100%, 보호 활성, 잠금 및 분리 상태를 확인했다. 초기 복구 암호가 콘솔 경고에 출력되어 해당 보호기를 교체하고 그 값이 담긴 임시 화면 파일을 삭제했다. 현재 Password 보호기 ID는 `3131BE9A-0075-4896-8B84-35EA659A1776`, RecoveryPassword 보호기 ID는 `89ED40F1-D12C-4C40-9229-8A33F09BC225`다. 기본 암호로 잠금 해제한 뒤 볼륨의 `VeilProfile`에 SYSTEM/Administrators FullControl 및 `VeilCefTest` Modify ACL을 확인하고 재잠금·분리했다. 등록 초안 도구의 운영자 외부 복구 백업 및 확인은 진행 중이다.
- 최초 등록 초안 실행은 GUID 형식의 프로필 경로를 거부했다. 실제 권한을 확인한 드라이브 문자 경로 `E:\VeilProfile`와 별도 볼륨 GUID로 재실행하여 기본 암호 보안 프롬프트에 도달했다. 외부 복구 백업 검증 및 초안 재검증은 대기 중이다.

### 2026-11-28 진단 시험 준비 보완

- P2-04 코드 경로: 서명된 진단 패키지, 별도 스테이징/등록/게시, Disabled SCM 등록, 독립 기준 신뢰 등록, 표준 사용자 코드 30 준비 및 코드 31 정지 프로세스 시험 실행 도구를 연결했다. 코드 31은 동일 등록의 검증된 코드 30 정리 기록을 요구한다.
- 위 기록은 코드/합성 시험 증거이며 실제 Windows VM, BitLocker, 독립 키, SCM/HKLM/ACL/OS 추적 및 CEF 초기화 증거가 아니다. P1 및 P2-03~05 완료 체크는 유지하지 않고 미완료로 둔다. 기존 WebView2 및 startupEnabled=false 유지.

### P2-05 초기화 설정 준비 (미완료)

- 보호 경로용 네이티브 설정 계획 및 CEF 설정 어댑터를 컴파일 후보에 추가했다. 독립 등록과 고정 CEF 하위 경로를 요구하고 환경/경로를 두 차례 검증하며 임시 프로필 fallback을 허용하지 않는다. 고정 data 시험 페이지는 실행하지 않는다.
- 이 단계는 설정 준비뿐이며 기존 `RunWinMain`은 변경하지 않았다. 브로커의 borrowed creation resume 차단, 일회성 기존 redemption, 실제 지속 권한 및 하위 프로세스 admission 문제는 미해결이다. 다음 단계에서 별도 런타임 권한 및 종료 프로토콜이 필요하다. 실제 CefInitialize/CefExecuteProcess/CefShutdown, BitLocker/VM 및 네트워크 격리 NOT RUN. P2-03~05 및 Gate A~E 미완료 유지.

### P2-05 네이티브 수명주기 준비 (미완료)

- 별도 컴파일 어댑터와 합성 수명주기 시험을 추가했다. 명시적 역할/권한 콜백이 필요한 CEF 실행 분기, 보호 설정, 초기화 실패, 브라우저 닫힘 확인 후 단일 종료 호출 및 권한 철회 시 성공 거부를 모델링한다. 브라우저 닫힘 미확인 시 정상 종료 증거를 생성하지 않고 외부 소유 브로커의 drain에 의존한다.
- 기존 제품 진입점은 호출하지 않는다. 실제 권한 콜백, 하위 프로세스 admission, 고정 페이지 브라우저 생성/닫힘 검증, 네트워크 차단 및 실제 VM 시험은 아직 없다. P2-03~05 미완료, startupEnabled=false 유지.

### P2-05 실행 권한 철회 모델 보완 (미완료)

- 네이티브 수명주기에서 거부/예외를 해당 호출에 영구 반영하고 동일 검증 콜백을 시험 fixture에 전달한다. 하위 프로세스 반환 후도 재검증하며 철회 후 정상 성공을 반환하지 않는다. 브로커 주기 감시는 점유 슬롯의 정확한 generation/객체를 관측 중 보유하고, 불일치·실패·철회 시 정리 경로로 전환한다.
- 새 IPC, 실제 권한 공급자, 지속적인 프로세스 admission, fixture 및 보호 VM 실행은 미구현/미검증이다. 주기 샘플은 실시간 연속 보안 보증이 아니며 기존 정지 후보 활성화 차단은 유지한다. P2-03~05 미완료.

### P2-05 실행 권한 IPC 준비 (미완료)

- 진단 빌드에만 별도 `CheckCefRuntimeAuthority` 명령과 정확한 응답을 추가했다. 등록된 보호 볼륨의 현재 generation 및 실제 retained browser root 객체만 검사 대상으로 하며, 전체 Job/모듈 관측 전후 권한을 재검증한다. 기존 redemption과 응답은 혼용할 수 없다. 일반 데스크톱/개발 브로커/하위 프로세스는 허용하지 않는다.
- 현재 borrowed creation의 활성화는 여전히 금지되어 긍정 응답을 생성할 수 있는 실제 실행 경로가 없다. 네이티브 요청 함수는 제품 진입점에 연결하지 않았다. 연속 호출 사이의 동일 SCM 프로세스 고정 및 하위 프로세스별 admission, 실제 권한/CEF/VM 검증은 후속 작업이다. P2-03~05 및 Gate A~E 미완료.

### P2-05 브로커 객체 고정 (미완료)

- 네이티브 권한 요청을 호출마다 SCM 프로세스를 새로 열던 함수 대신 한 실행의 고정 `RuntimeAuthoritySession`으로 변경했다. 원본 프로세스 핸들을 유지하며 각 연결 전후 SCM/pipe PID 및 원본 생존 상태를 검사한다. 거부/예외/재진입은 해당 객체에 영구 반영된다. 새 객체 생성으로 복구하는 실행 경로는 추가하지 않았다. 합성 latch 및 wire 테스트만 추가했다. 실제 SCM 재시작/IPC/CEF/보호 VM 검증과 하위 프로세스 admission은 미완료. 기존 시작 차단 유지.

### P2-05 하위 프로세스 권한 검증 준비 (미완료)

- 진단 전용 권한 요청자를 브라우저 루트 외에 두 private Job의 실제 멤버까지 검사하도록 확장했다. IPC의 retained process handle을 브로커가 독립 수집한 멤버 객체와 비교하며, 전체 관측의 Job accounting, 이미지, 모듈, 토큰 및 세션 검증을 유지한다. 클라이언트가 역할/PID를 제출하지 않는다.
- 현재 역할 정책은 Browser 및 Renderer의 기존 잠정 토큰 기준만 허용 대상으로 분류한다. GPU/Utility/Network/Unknown 및 불일치 역할은 거부한다. 잠정 기준은 실제 sandbox admission 증명이 아니다. 기존 활성화 금지 및 production Job cap=1을 유지하며 네이티브 진입점 연결, 실제 멀티프로세스 CEF, SCM/BitLocker/VM 검증은 NOT RUN. P2-03~05 미완료.

### P2-05 고정 페이지 fixture 준비 (미완료)

- 네이티브 수명주기 어댑터를 고정 data 페이지 시험에 연결했다. 정확한 URL/제목과 단일 브라우저 식별자, `OnBeforeClose` 관측을 요구하며, 로드 및 정리 시간 예산과 주기 권한 검사 및 실패 시 닫기 요청을 추가했다. 추가 브라우저나 모호한 닫힘은 정상 `CefShutdown` 허가가 아니며 외부 broker drain이 필요하다. 합성 관측 정책 시험 9개를 추가했다.
- 시간 예산은 CEF/IPC 호출 자체가 반환된다는 전제의 제한이며 OS 수준 hard timeout이 아니다. 고정 data 페이지는 네트워크 격리 증명이 아니다. GPU/Utility/Network 역할은 실제 독립 보안 기준이 없으므로 계속 거부한다. 제품 진입점, Job cap=1 및 borrowed activation 차단을 유지한다. 실제 CEF 생성/렌더링/닫힘, 보호 볼륨/VM 및 다중 프로세스는 NOT RUN. P2-03~05 미완료.

### P2-05 고정 페이지 증거 강화 (미완료)

- 정확한 제목을 관측한 뒤 모순되는 제목/페이지 콜백이 오면 성공을 영구 거부한다. 알려지지 않은 브라우저의 load/title 콜백 또는 생성 전 콜백은 모호한 수명으로 분류하여 정상 shutdown 증거를 금지한다. 초기 제목 변경은 허용한다. 닫기 메시지 펌프에서도 권한을 주기적으로 확인하고 철회 후 닫기 시도는 계속하되 성공을 금지한다. 합성 회귀 6개 추가.
- 실제 CEF 콜백 순서/닫기, Windows native CI 및 보호 VM 실행은 이 변경 시점에 NOT RUN. #265의 진입점 차단, Job cap=1, GPU/Utility/Network 거부와 제품 WebView2 유지. P2-03~05 미완료.

### P2-05 주기 감시 역할 정책 일치 (미완료)

- 브로커의 활성 진단 세션 주기 감시와 IPC 실행 권한 확인에 동일한 `RequireMemberRole` 검사를 적용한다. 두 private Job에서 독립 수집한 retained handle이 유지되는 동안 모든 멤버에 Browser-root/Renderer-descendant 잠정 정책을 적용한다. GPU/Utility/Network/Unknown 및 잘못된 역할 또는 token decision은 거부하며, 기존 관측 실패→slot retirement→drain 경로를 사용한다. 역할/결정 조합 및 미정의 enum, GPU 관측 후 slot 정리 합성 시험과 source contract를 추가했다.
- #265/#266의 직전 정확한 head Windows/native CI는 PASS, 독립 리뷰는 없음. 새 CI와 실제 보호 VM, 연속 admission, 실제 CEF 역할별 sandbox 기준은 NOT RUN. 샘플링은 전체 수명 보안 증명이 아니다. 생산 Job cap=1, borrowed activation 차단 및 WebView2 제품 시작을 변경하지 않는다. P2-03~05 미완료.

### P2-05 런타임 토큰 관측 결합 (미완료)

- 기존 role/token 요약뿐 아니라 Windows kernel TokenId, ModifiedId, AuthenticationId를 포함한 내부 스냅샷을 모듈 관측 전후 비교한다. 각 멤버는 루트와 동일 사용자 SID, 세션 및 인증 LUID를 요구하고 전체 관측 전후 루트 스냅샷을 비교한다. 값은 IPC나 로그로 내보내지 않는다. 합성 변조/다른 logon 거부, 동일 실제 프로세스의 독립 handle 비교 및 종료 후 거부 시험을 추가했다.
- #267 정확한 head Windows CI PASS, 독립 리뷰 없음. 새 CI 및 실제 CEF sandbox 토큰 호환성/보호 VM 검증 NOT RUN. 같은 LUID는 잠정적 엄격한 요구사항이며 실제 역할별 baseline이 아니다. 샘플 사이 변경/복원 및 스레드 impersonation은 검증하지 않는다. GPU/Utility/Network/Unknown 차단, Job cap=1, borrowed activation 차단 및 WebView2 유지. P2-03~05 미완료.

### P2-05 Utility 역할 및 sandbox claim 관측 (미완료)

- 기존 role 관측을 닫힌 enum 조합으로 세분화한다. Network/Audio/Other service 및 Missing/Network/Audio/Other sandbox claim을 구분하되 원문 인수는 내보내지 않는다. Network는 정확한 subtype 및 network claim 조합을 요구하며 알려진 claim과 다른 service 조합은 거부한다. Audio의 누락 claim 및 기타 서비스는 여전히 미검증 Utility로만 관측한다. GPU와 Renderer에 Utility claim을 허용하지 않는다. 모듈 관측 전후 role record 비교에 세부 정보가 포함되고 진단 결과에도 유지된다. 합성 분류 및 불일치 회귀 추가.
- #268 정확한 head Windows CI PASS, 독립 리뷰 없음. 이 분류는 명령행 주장일 뿐 실제 sandbox 정책 증명이 아니며 기타 값의 동일 범주 내 변경도 구분하지 못한다. 실제 pinned CEF GPU/Audio/Network 샌드박스 호환성, OS 권한/mitigation 검증 및 보호 VM NOT RUN. 기존 역할 차단, Job cap=1, startupEnabled=false 유지.

### P2-05 Windows 프로세스 완화 정책 관측 (미완료)

- `GetProcessMitigationPolicy`의 6개 DWORD 정책(ASLR, DynamicCode, SystemCallDisable, CFG, ImageLoad, ChildProcess)을 읽기 전용으로 조회한다. 원본 retained handle과 동일한 live object의 query/synchronize handle만 사용하고 두 차례 스냅샷 및 모듈 관측 전후를 비교한다. 0은 관측된 실제 값이며 실패는 예외로 구분한다. 원본 비트를 보존하되 허용 정책으로 해석하지 않는다. 합성 조회 실패/정책 변경 및 실제 합성 프로세스 handle/종료 검사, 소스 계약을 추가했다.
- API 기준: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessmitigationpolicy 및 https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-process_mitigation_policy. #269 정확한 head Windows CI PASS, 독립 리뷰 없음. 새 CI 및 실제 CEF GPU/Utility/Network 정책 실측은 NOT RUN. 다른 정책, thread opt-out, 변경 후 복원 및 전체 sandbox 동작은 미검증. 조회 권한 거부도 실패로 처리하며 실제 CEF 제한 token 호환성은 별도 확인 필요. 역할 허용 범위, Job cap=1, borrowed activation 차단 및 WebView2 유지.

### P2-05 독립 nested smoke mitigation 증거 준비 (미완료)

- #270 최초 Windows CI 46163370054의 실패는 CefMitigationTests.cs의 int→uint 변환(CS0266/CS1662)이며 `0u` 수정 head `0e64dfbaca0f377dec454976339728e0e90a8d26`에서 재검증 중(46163853866). Rust 검사 PASS. 수정 head 성공은 아직 미확인.
- 기존 독립 `CefNestedSmoke`가 Browser/Renderer/GPU/Utility/Network 마지막 live 표본에 6개 Windows mitigation raw flag와 관측 상태를 추가한다. 원래 process와 query handle의 동일 object 및 양쪽 Job 소속/생존을 확인하고 두 스냅샷을 비교한다. 실패·불안정은 null flags 및 명시 상태로 보존하며 0으로 대체하지 않는다. 추가 필드는 schema 5의 선택적 진단 확장이다. 기존 종료 및 renderer 제한 확인 기준은 변경하지 않으며, mitigationCoverage는 마지막 표본만 집계한다. 전체 생애, 모든 자식, 정책의 적정성 또는 보호 VM 증명이 아니다. 새 Windows CI, 번들 검증 및 실제 일반 사용자 VM 측정 NOT RUN. 기존 역할 차단, Job cap=1 및 startupEnabled=false 유지.

### P2-05 mitigation 증거 구조 및 역할별 집계 (미완료)

- 기존 hash-bound `check-cef-nested-evidence.mjs`에서 schema 5의 선택적 mitigation 확장을 검증한다. 확장이 없던 과거 증거는 null로 유지하되 일부 필드만 존재하거나 관측 수/상태/정확한 6개 uint32 flags/오류가 모순이면 거부한다. 역할별 관측 수, 누락 PID 및 서로 다른 raw flag 조합을 집계하며 0을 누락과 혼동하거나 값 평균으로 정책 차이를 숨기지 않는다. 새로운 구조 검증 및 이전 schema 호환 회귀 시험을 추가했다. 자체 보고의 일관성일 뿐 실제 신뢰성/전체 수명/허용 기준은 NOT ESTABLISHED.
- #270 수정 head Windows CI 46163853866 PASS, #271 head Windows CI 46163947798 PASS, Native 46163947800 실행 중 확인. 독립 리뷰 없음. 새 CI 및 실제 일반 사용자 CEF/보호 VM 실행 NOT RUN. 역할 허용 범위, Job cap=1, startupEnabled=false, WebView2 유지.

### P2-05 실측 수집 목표 검사 및 실행 번들 확보 (미완료)

- #271 exact head Windows 46163947798 / Native 46163947800 PASS, #272 exact head Windows 46164440580 PASS. Native 실행에서 mitigation 지원 nested bundle artifact 15270802631 확인(merge checkout `287cd139cc779765c59d09221914647f009c948a`, PR head `08f52fa25c67d1741581a7317a3cfcf412b69453`). 만료 전 사용하거나 정확한 새 checkout으로 재빌드한다. README에 일반 사용자 실행 및 분석 명령을 기록했다.
- 기존 분석 CLI에 `--require-mitigation-collection` 선택 모드를 추가한다. Browser/Renderer/GPU/Network/Utility 표본, 정책 조회 성공, Unknown 부재 및 기록된 Job start의 live sample 대응을 요구한다. 미충족은 JSON 진단과 exit 1, 이전 증거는 일반 검토에서 호환한다. 다섯 역할이 모든 환경에서 반드시 실행된다는 보장은 없으며 best-effort Job 알림 및 마지막 표본에 한정된다. 실제 VM 실측, 독립 리뷰, 새 CI 및 역할별 보안 허용 기준 NOT RUN/NOT ESTABLISHED. 제품 실행 차단/Job cap=1/WebView2 유지.

### P2-05 mitigation 수집 및 독립 lifecycle trace 대조 (미완료)

- #273 정확한 head Windows CI 46164985493 PASS, Native 46164985482 실행 중 확인. 별도 미병합 #230의 head `17be1ebd8c7f7f7dc3658ff46e6ea75a2bee0814`에 있는 엄격한 CSV parser, 추적 중 잘못된 lifecycle row/보존 handle exit 누락 거부 및 회귀를 두 파일의 base blob이 동일함을 확인해 이 stack에 재적용했다. #230 자체는 병합/닫지 않았다.
- `check-cef-process-trace.mjs`에 `--require-mitigation-correlation` 추가. 기존 수집 목표, 일치하는 OS trace 및 모든 trace PID의 live sample/보존 exit 대응을 함께 요구하며 불완전하면 JSON 진단과 exit 1. manifest/report/sample/trace digest를 출력한다. 새 회귀 및 README 명령 추가. 실제 VM 수집, 보안 완화 flags 독립 증명, 전체 수명 및 역할 허용 기준은 NOT RUN/NOT ESTABLISHED. 새 CI와 독립 리뷰 미확인. startupEnabled=false, Job cap=1, WebView2 및 기존 역할 차단 유지.

### P3-01 개발용 WPF view 명령 수명 검증 (미완료)

- P2 실제 VM 차단과 독립적인 제품 통합 준비. CEF development adapter의 GoBack/GoForward/Reload/Stop 및 ZoomFactor 읽기에 dispatcher/live view guard를 추가한다. RequireView는 disposed, zero HWND/ID, 닫힌 native ID를 거부한다. Dispose 시작 시 권한 세대를 폐기하고 late callbacks를 거부한다. 개발용 합성 WPF runner에서 미연결/폐기 view의 명령 거부 및 기존 view mute 보존을 확인하는 코드, JS 로그 계약과 source tests를 추가했다. 제품 엔진/기능을 변경하지 않는다.
- #273 exact Windows/native PASS, #274 Windows PASS, Native 46165492128 실행 중 확인. 새 CI, 실제 WPF runtime 및 보호 CEF VM NOT RUN. P2-03~05 및 P3-01 미완료; startupEnabled=false 및 WebView2 유지.

### P3-01 native navigation callback 수명 및 redirect 검증 (미완료)

- #275 최초 Windows CI 46166085021 FAIL: 기존 source contract가 base.Dispose 이후 disposed 설정을 요구했다. 신규 사전 권한 폐기 순서를 검사하도록 수정 head `78af961bea2ca05792b9904cb994ba69891d04d7`. 수정 head CI 대기. #274 Windows/native PASS.
- CEF WPF fixture OnBeforeBrowse에서 URL username/password 거부, ID overflow 차단, host callback 후 closing/callback/active ID/URL 재확인으로 stale decision 거부, 닫힌/비활성 load completion 무시를 추가한다. 기존 loopback HTTP 제한은 유지한다. 합성 WPF fixture는 non-HTTP redirect를 원래 navigation ID의 canceled completion으로 관측하고 정상 탐색 재개를 요구한다. 소스 계약 추가. 실제 runtime NOT RUN, URL 기반 completion은 동일 URL의 오래된 callback을 완전히 식별하지 못하며 전체 수명/네트워크 보호 증명이 아니다. 신규 CI, 독립 리뷰, 보호 VM 미검증. WebView2 및 startupEnabled=false 유지.

### P3-01 탭별 탐색 상태 및 UI 이벤트 분리 (미완료)

- #275 수정 head `78af961bea2ca05792b9904cb994ba69891d04d7` Windows 46166527119 / Native 46166527123 PASS. #276 exact head Windows 46166617260 PASS, Native 46166617290 실행 중 확인. 독립 리뷰 없음.
- 개발용 TabbedDevelopmentHost에서 tab별 pending/last navigation ID 및 상태 메시지를 보존한다. 동일 ID의 redirect 및 현재 ID의 completion만 적용하고, 닫히거나 제거된 tab과 shutdown 중 이벤트를 무시한다. 선택된 tab만 전역 상태 표시를 변경하며 전환 시 해당 tab 상태를 복원한다. child failure 및 command error도 tab별로 보존한다. 실제 두 tab 탐색 완료와 합성 background status 변경의 분리 검사를 fixture 및 source contracts에 추가했다. 합성 상태 갱신은 실제 background renderer crash 실측이 아니다. 동일 URL의 오래된 native callback 한계 유지. 신규 CI, WPF runtime, 보호 VM NOT RUN. WebView2 및 startupEnabled=false 유지.

### P3-02 개발용 탭별 팝업 차단 피드백 (미완료)

- #276 exact head Windows 46166617260 / Native 46166617290 PASS, #277 exact head Windows 46167133738 / Native 46167133734 PASS. 승인 리뷰 없음.
- 개발용 탭 toolbar에 선택된 live view의 native popup denial count만 읽는 별도 안내를 추가한다. 닫힌/종료 중 view와 0건에서는 숨기고 기존 탐색 상태 메시지를 덮어쓰지 않는다. 팝업 URL을 전달하거나 재실행하지 않고 기존 OnBeforePopup 무조건 거부 및 NewWindowRequested 미지원 유지. 합성 loopback 페이지에서 한 번 window.open 요청을 발생시켜 첫 탭 1건, 두 번째 0건, 전환 UI 및 native view count=2, 대상 HTTP 요청 부재를 요구하는 runtime/source 회귀 추가. 실제 WPF 실행 및 새 CI NOT RUN, 이 구현은 다중 창 adoption 지원이나 제품 popup 허용이 아니다. WebView2 및 startupEnabled=false 유지.

### P3-02 다중 창 개발 fixture의 소유권/종료 수명 (미완료)

- #277 Windows/native PASS, #278 exact head Windows 46167628799 PASS, Native 46167628733 실행 중 확인. 독립 승인 리뷰 없음.
- 기존 WpfSmoke 두 창의 secondary reservation은 이전 창 및 native view drain 이후에만 교체한다. 각 closure의 managed view/window와 current native ID, closing/loaded 상태를 대조하고 일치하는 pending navigation만 완료 처리한다. 종료 시 login 세대 및 pending ID 폐기, 비동기 Loaded 후 소유권 재검사. 기존 창 재사용 회귀에서 이전 completion delegate의 합성 실패 이벤트를 직접 호출해 새 창 상태가 유지되는지 검사한다. 실제 stale native callback/renderer crash 실측 아님. 새 CI/WPF runtime/보호 VM NOT RUN. 일반 사용자 제품의 새 창/popup adoption 기능은 여전히 미지원이며 WebView2/startupEnabled=false 유지.

### P3-02 페이지 내 검색 개발 fixture (미완료)

- #278 Windows/native PASS, #279 exact head Windows 46168165728 PASS, Native 46168165795 실행 중 확인. 독립 리뷰 미완료.
- CEF native Find/GetFindHandler 연결과 per-view ticket, final update/monotonic CEF ID, 취소·탐색·종료·timeout 시 stale result 거부, 미완료 검색이 중단되면 해당 view의 검색 재사용 차단을 추가했다. 관리 코드의 document/selection epoch 검사를 유지하며 결과 unknown은 null로 처리한다. 256 UTF-16 unit 제한, fixture_enabled 전용, fresh search만 지원(다음/이전 일치 순환 및 검색 UI 미구현). CEF가 내부 ID를 부여하므로 실제 callback 상관 관계 검증 필요. 두 창의 동일 검색어 match/no match와 두 번째 fresh search의 회귀 코드를 작성했다. 실제 WPF 실행, 새 CI, 독립 리뷰 및 보호 VM 미검증. 제품 엔진/보안 게이트/startupEnabled=false 유지.

### P3-02 개발용 검색창 및 WPF 단축키 (미완료)

- #279 Windows/native PASS, #280 exact head Windows 46168780848 PASS, Native 46168780842 실행 중 확인. 독립 리뷰 없음.
- TabbedDevelopmentHost에 fixture 전용 검색 버튼/검색창/결과 안내, WPF routed Ctrl+F/Esc/Enter/Shift+Enter 연결. 검색 요청 객체와 tab/native ID/navigation ID/source/query를 모두 대조하고 선택/탐색/주소 변경/오류/종료/검색어 변경에서 결과를 폐기한다. 한 번에 한 요청만 dispatch. 실제 두 tab의 match/no match와 이전 요청을 통한 합성 stale publication 거부 검사를 추가했다. 검색창은 native HWND 키 포커스 통합, 지속 강조, 항목 순환 및 제품 검색 기능을 주장하지 않는다. 새 CI, 실제 WPF runtime, 보호 VM, 독립 리뷰 NOT RUN. 기존 WebView2 및 startupEnabled=false 유지.

### P3-02 개발용 확대/축소 및 배율 표시 (미완료)

- #280 exact Windows/native PASS. #281 최초 Windows 46169261111 FAIL: ProcessFailed 소스 계약이 검색 UI 폐기 추가 이전 순서를 검사했다. 수정 head `e35778eee85145ca3b26c8b697d3ed06a0d69cb5`, CI 대기.
- TabbedDevelopmentHost에 25%p 확대/축소, 100% 초기화 버튼 및 WPF routed Ctrl+Plus/Minus/0, 현재 선택된 live view의 실제 native readback 표시를 추가한다. managed/native 50~300% 유한값 제한, native closing/UI thread 검사. 서로 다른 loopback origin의 두 view에서 125%/100% readback 및 선택/초기화 회귀를 작성했다. Chromium origin별 zoom 공유 가능성을 유지하므로 동일 origin tab 독립성이나 저장/복원 보증은 하지 않는다. native HWND 단축키 전달, 실제 WPF runtime, 새 CI, 보호 VM 및 독립 리뷰 미검증. WebView2/startupEnabled=false 유지.

### P3-02 개발용 브라우저 전체 화면 및 페이지 전체 화면 분리 (미완료)

- #281 수정 head Windows 46169740661 / Native 46169740665 PASS, #282 exact Windows 46169834248 PASS / Native 46169834303 실행 중 확인. 독립 리뷰 없음.
- WPF 창의 F11/버튼 host fullscreen은 기존 style/state/resize를 복원하고 탭/toolbar만 숨기며 하단 종료 제어는 남긴다. 페이지 전체 화면은 선택된 view의 관측 상태만 별도 안내하며 host fullscreen을 자동 활성화하지 않는다. 사용자 요청 시 native IsFullscreen/ExitFullscreen(false) 및 managed ID/epoch/timeout 확인을 추가한다. 실제 페이지 fullscreen 진입/종료 실측은 없음. 합성 fixture는 WPF host fullscreen 전환/복원과 page state 불변 조건을 요구한다. 새 CI, 실제 WPF runtime, native HWND keyboard routing, 보호 VM, 독립 리뷰 미검증. WebView2/startupEnabled=false 유지.

### P3-02 개발용 북마크 라이브러리 준비 (미완료)

- #282 exact Windows 46169834248 / Native 46169834303 PASS, #283 exact Windows 46170338813 / Native 46170338825 PASS. 독립 리뷰 없음.
- 기존 제품 BrowserWindow의 PageEntry/SettingsReady/암호화 상태 저장 경로는 변경하지 않았다. 개발 TabbedDevelopmentHost fixture에서만 64개 한도의 메모리 북마크 목록, 추가/열기/삭제, 현재 live 성공 탐색 source 검증, exact duplicate 차단, fixture port 및 loopback HTTP/userinfo 제한, 종료 시 폐기를 추가했다. 두 origin의 추가/중복/정확 삭제 및 외부/credential URL 거부 회귀를 작성했으며 실제 bookmark reopen, 저장/복원, 폴더, 제품 보호 데이터 연동 및 방문 기록은 미구현/미검증. 신규 CI/WPF runtime/보호 VM/독립 리뷰 NOT RUN. WebView2/startupEnabled=false 유지.

### P3-02 개발용 방문 기록 (미완료)

- #284 exact Windows 46170874531 / Native 46170874524 PASS, 독립 리뷰 없음.
- fixture 전용 128개 메모리 history, 성공 completion의 native ID/navigation ID/expected URL/source 일치 검증, 동일 ID 중복 방지, 최신순 조회/열기/선택 삭제/전체 삭제 및 종료 폐기. title callback에는 탐색 ID가 없어 URL+시간만 기록한다. 두 창의 성공 기록, 삭제/전체 삭제, HTTP 404 제외 및 후속 정상 탐색 기록을 요구하는 회귀 작성. history reopen, 제품 암호화 저장/복원, same-URL stale native callback 구분 및 실제 WPF runtime 미검증. 새 CI/보호 VM/독립 리뷰 NOT RUN. WebView2/startupEnabled=false 유지.

### P3-02 제품 라이브러리 공통 저장 경계 준비 (미완료)

- #285 exact Windows 46172371655 / Native 46172371697 PASS, 독립 리뷰 없음.
- 기존 PageEntry/BrowserData JSON schema 및 mounted profile 경로를 유지한다. 엔진 독립 LibraryMutation 동기 transaction을 추출해 기존 제품 북마크 버튼/라이브러리 편집에서 사용하고 변경 전/저장 직전 권한 재검사, 실패/예외 시 entry/folder rollback, 성공 후 UI publish 분리를 유지했다. BrowserSession.TrySaveLibrary는 소유 dispatcher, private/ending/engine lost/not ready 및 호출자 권한을 확인하며 기존 session 소유 경로만 사용한다. 이는 세션 수명 검사이며 물리 볼륨 잠금/ACL/암호화 권한의 독립 증명은 아니다. 기존 WebView2에도 편집 저장 경계 변경이 적용된다. 기존 desktop smoke에 합성 revoke/save-fail/success 케이스와 source contract 추가. CEF fixture 메모리 데이터는 제품 저장소에 연결하지 않으며 방문 기록 자동 저장 경로/보호 볼륨 실측/새 CI/실제 runtime/독립 리뷰 미검증. startupEnabled=false 유지.

### P3-02 자동 방문 기록 공통 저장 경계 (미완료)

- #286 exact Windows 46174176704 PASS; native workflow not triggered. 독립 리뷰 없음.
- 기존 제품 완료 이벤트에서 동일 navigation ID 중복 방지, live owner/session/tab/navigation epoch/current source 검증과 LibraryMutation history rollback, 저장 성공 후 history UI publish를 연결했다. TrySaveLibrary 선택적 captureTabs로 기존 완료 시 tab+history 한 번의 저장을 유지하며 복원/이동 중에는 마지막 완전한 layout을 보존한다. 실패 시 history를 복구하고 자동 중복 저장을 피한다(메모리 tab layout snapshot은 기존 방식처럼 이후 저장 대상). 기존 JSON schema 및 저장 경로 유지. 기존 WebView2 동작에 적용되며 CEF fixture 영구 저장은 여전히 비활성. Desktop smoke에 합성 실패/성공 및 중복 완료와 실제 state readback 검사를 작성. 신규 CI/desktop runtime/보호 VM/독립 리뷰 미검증. 실제 물리 volume lock/relock 증거 아님. startupEnabled=false 유지.

### P3-02 방문 기록과 탭 배치 저장 실패의 동시 rollback (미완료)

- #287 정확한 head `5ff4262ea91afe88d64769fbd63536adad680386` Windows/Rust CI 46175279128 PASS. 독립 리뷰 없음. #229의 main 실패 수정 Windows/Rust/native CI PASS이나 아직 미병합.
- 방문 기록 저장 실패/권한 거부/예외 때 history와 같은 상태 파일에 포함되는 primary tabs, selected, pinned count, additional windows의 이전 메모리 스냅샷을 함께 복원한다. 모든 창의 snapshot 수집 후에만 배치를 갱신한다. 새 합성 desktop smoke 및 source contract 작성. 일반 Save() 실패의 rollback, 물리 디스크/BitLocker 및 실제 WPF runtime 검증은 범위 밖. CEF fixture는 메모리 전용, 제품 WebView2 및 startupEnabled=false 유지. 신규 CI/독립 리뷰 미확인.

### P3-02 일반 탭 배치 저장과 엔진 종료 결과 (미완료)

- #288 exact head `51805c07a8588a51976d60267fc0dc8382fa3b04` Windows/Rust CI 46175662851 PASS, 독립 리뷰 없음.
- 일반 BrowserSession.Save에도 이전 배치 rollback을 적용하고 실제 쓰기 성공 여부를 bool로 반환한다. dispatcher 확인, private/ending/transfer 및 불완전 복원 중 쓰기 거부. 엔진 손실 직후에는 기존 managed snapshot 저장을 허용하되 종료 후 쓰기는 거부한다. 엔진 crash 안내가 skip을 성공으로 표시하지 않도록 수정했다. 보호 종료 finally 순서 유지. fixture 프로필 scratch에서 .new 경로를 디렉터리로 막아 실제 파일 열기 실패, 기존 바이트/메모리 복원, 정상 재시도/readback을 확인하는 smoke 및 source contracts 작성. 새 CI, 실제 WPF runtime 및 보호 VM NOT RUN; 설정 데이터 직접 저장 경로는 별도 작업. CEF startupEnabled=false, WebView2 유지.

- #289 최초 head `29633997f15180e524332f5c3f444d7c1f337c91` Windows CI 46176414282 FAIL: 이전 탭 이동 source contract가 `void Save()`의 return 형식을 요구했다. 일반 저장의 새 bool 반환/종료/transfer guard 및 실제 쓰기 전 snapshot 거부 조건을 검사하도록 수정했다. Rust PASS. 새 head CI 확인 필요; 기존 실패 증거 유지.

### P3-02 검색 설정 공통 저장 경계 (미완료)

- #289 수정 exact head `44d711133b2c27145f22ea812be49e93e89c396f` Windows/Rust CI 46176834359 PASS, 독립 리뷰 없음.
- 검색 엔진/로컬 주소창 제안을 하나의 transaction으로 변경. 허용 provider, UI 및 session/dispatcher/engine 수명, private/ending/lost 상태를 검사하고 기존 library profile writer로 저장. 거부/예외 시 두 값 복원, 성공 후 일반 session의 창 UI만 갱신. 기본 provider 및 nullable suggestion schema 유지. 합성 거부/철회/실패/예외/성공 및 source contract 추가. 실제 WPF/보호 VM, 새 CI/독립 리뷰 미검증. 기존 WebView2 동작에 적용, CEF fixture 지속 저장 미연결, startupEnabled=false 유지.

### P3-02 사이트 권한 공통 저장 경계 (미완료)

- #290 exact head `c6b2ea360e8276ae3c6abc2fd127572af198cb2a` Windows/Rust CI 46177523014 PASS, 독립 리뷰 없음.
- 기존 WebView2 사이트 권한 편집을 session-owned profile writer에 연결. 독립 복제한 규칙 목록의 HTTPS canonical origin, 지원 종류, Ask/Allow 및 중복 검사, private/ending/lost/dispatcher/engine/UI 권한 검사를 유지한다. 실패/예외/철회 시 원본 목록 복원, 빈 목록은 null/default deny, 성공한 이후 안내. 합성 회귀 7개와 source contract 작성. 이미 사용 중인 권한의 즉시 철회나 CEF permission callback 연결은 아님. 실제 WPF/보호 VM, 신규 CI/독립 리뷰 NOT RUN. WebView2 및 startupEnabled=false 유지.

### P3-02 네트워크 설정 공통 저장 경계 (미완료)

- #291 exact head `a1042ded8588eb7d6a5efb4361c3a244e779b341` Windows/Rust CI 46177932325 PASS, 독립 리뷰 없음.
- 네트워크 저장을 session-owned profile writer에 연결. 기존 NetworkSettings.Problem 검증 및 private/ending/lost/dispatcher/engine/UI guard 유지. 저장 거부/철회/예외에서 이전 선택을 복원하며, 현재 적용된 session.Network, 엔진, vault transport는 변경하지 않는다. 표준 기본값 null 및 DoH 저장 형식 유지. 합성 회귀 6개 및 source contract 작성. 기존 프록시/DoH 런타임 정책 변경 없음. 신규 CI 및 실제 WPF/보호 VM/독립 리뷰 미검증, CEF startupEnabled=false 유지.

### P3-02 SmartScreen 보안 설정 공통 저장 경계 (미완료)

- #292 exact head `109e3c1ed3eabc1fefc99a1802d38c9d1b532c32` Windows/Rust CI 46178371861 PASS, 독립 리뷰 없음.
- 보안 설정 저장을 session-owned writer에 연결. private/ending/lost/dispatcher/engine/UI authorization 유지. SecurityManager.Change는 bool persist 거부 시 Saved/State/뷰를 변경하지 않으며 예외에서도 저장 이전 값을 보존한다. 저장 성공 후에만 기존 다중 창 적용을 수행하고 Pending/Failed를 적용 성공으로 표시하지 않는다. 기본 SmartScreen on/null 및 복구 마커 의미 유지. 합성/기존 live smoke 확장 및 source contracts 작성. 실제 CEF SmartScreen 대체 구현이 아니며 신규 CI/실제 Windows 및 보호 VM/독립 리뷰 미검증. CEF startupEnabled=false 유지.

### P3-02 CEF fixture 공통 라이브러리 트랜잭션 연결 (미완료)

- #293 exact head `c0edbcb3efa6cf7d203d3a461ace5c40bec26375` Windows/Rust CI 46178804539 PASS, 독립 리뷰 없음.
- WebView2 LibraryMutation의 스냅샷/검증/권한 재확인/저장/rollback을 engine-neutral CollectionMutation으로 추출하고 CEF fixture bookmark/history 추가·삭제·초기화에 연결했다. UI는 commit 후 변경, 종료 정리는 무조건 메모리 삭제. CEF fixture commit은 메모리 전용이며 파일 writer/제품 profile 경로를 받지 않는다. 실제 fixture에 권한 철회 및 쓰기 거부 rollback 검사 추가. 기존 WebView2 folder validation과 writer 유지. P1/P2 보호 runtime 및 session lifetime 미완료로 제품 CEF 영구 저장 연결은 차단 상태; preflight lease는 launch 권한 아님. 신규 CI, 실제 CEF/WPF/보호 VM/독립 리뷰 NOT RUN. startupEnabled=false 유지.

### P3-02 CEF 라이브러리 수명/권한 철회 경계 (미완료)

- #294 exact head `30dde1caa6a1ac3f69ec96aa6dcd0ec019bc89e6` 일반 Windows/Rust CI 46179282326 PASS; native compile CI 46179282448 미완료 (runtime 아님), 독립 리뷰 없음.
- 공통 CefLibraryLifetime에서 owner thread, 재진입, terminal revocation, 세션 probe 예외/거부, 변경 후 및 writer 직전 재확인. 개별 요청 거부는 세션 철회와 구분한다. CEF fixture는 메모리 전용 callback 유지하고 shutdown 시작 시 revoke. 일반 CI에서 실행되는 NetworkPolicyCheck에 synthetic lifetime 회귀 검사 추가. 실제 보호 runtime authority는 아직 연결하지 않으며 preflight lease를 launch/save 권한으로 승격하지 않는다. 저장 중 lock 경합은 실제 보호 writer/broker lifecycle에서 별도 검증해야 한다. 새 CI/보호 VM/실제 CEF/독립 리뷰 미검증. startupEnabled=false 유지.

### P3-02 보호 라이브러리 권한 바인딩 모델 (미완료)

- #294 exact head native compile CI 46179282448 최종 PASS (runtime 아님). #295 exact head `c5fc5cf4f7849cae121ec81f866e91590c1213cf` Windows/Rust CI 46179722555 PASS; native compile CI 46179722552 미완료, 독립 리뷰 없음.
- CefProtectedLibraryAuthority prospective model: 정확한 unlock generation/retained runtime lease/profile lease identity, authenticated owner, 별도 runtime admission, lease current, lock 요청, 보호 상태와 volume GUID의 일치가 모두 필요. CefLibraryLifetime에 연결하여 전/후 변경 및 writer 진입 시 검사; 불일치 후 재사용 거부. NetworkPolicyCheck에 15개 부정 context의 전/후 변경 검사와 예외/명시 철회/직전 lock/success 추가. 실제 broker IPC·runtime admission·파일 writer·제품 연결 없음. 이 모델은 검증된 로컬 관측을 미래 caller가 제공해야 하며 preflight나 일반 Status로 대체 불가. 실제 writer publish 경합은 broker 수명과 별도 결합 필요. P1/P2/실제 VM 미완료, 새 CI 및 독립 리뷰 미검증, startupEnabled=false 유지.

### P3-02 라이브러리 staged publication 및 철회 경합 준비 (미완료)

- #295 exact native compile 46179722552 최종 PASS (runtime 아님). #296 exact head `81d9c81699be2d1eeacea2deb229d95b5a9fc87a` Windows/Rust CI 46180159555 PASS; native compile CI 46180159559 미완료, 독립 리뷰 없음.
- CefLibraryLifetime/Authority에 stage → authority/request/data 재검증 → irreversible publish → abort cleanup 경계 추가. cleanup 실패 시 lifetime revoke, 이미 완료된 publish는 이후 revoke로 rollback하지 않는다. NetworkPolicyCheck의 실제 synthetic JSON 파일에서 flush, same-directory rename, lock/lease 교체/요청 거부/예외/충돌/cleanup 실패/commit 후 철회와 디스크·메모리 일치 회귀 작성. 실제 파일 접근은 test harness만 수행, 제품/CEF fixture 연결 및 브로커 IPC/atomic lock-replace는 미구현. 실제 publish와 revocation의 직렬화 및 retained handle 기반 경로 검증이 필요하다. 신규 CI/보호 VM/실제 CEF/독립 리뷰 미검증. startupEnabled=false 유지.

### P3-02 브로커 저장/잠금 공통 직렬화 경계 (미완료)

- #296 exact native compile 46180159559 최종 PASS (runtime 아님). #297 exact head `7ac765e528b2cf236812d90511eb6787af7265c2` Windows/Rust CI 46180598778 PASS; native compile CI 46180598781 미완료, 독립 리뷰 없음.
- 기존 Broker.serial의 SemaphoreSlim(1,1)을 동등한 WaitAsync/Release를 제공하는 CefBrokerOperationGate로 감싼다. 기존 lock/detach/SCM revoke/crash/monitor와 동일 gate 아래 미래의 synchronous trusted publisher를 위한 TryPublishAsync 추가. 획득 후 current 재검증 및 cancellation 확인, 예외에도 release, publish 이후 cancellation으로 commit 결과 뒤집지 않음. NetworkPolicyCheck에 lock-first/old generation/cancelled waiter/cancel during observation/refusal/exception/publish-first 동시성 회귀 작성. 실제 publisher/IPC/파일 경로 권한 부여 없음; 기존 broker 명령에서 신규 publish 호출 금지. future caller는 retained handle/current generation 및 별도 runtime admission 검증, bounded publication, non-reentrant 규칙 및 OS lock race 검증 필요. 신규 CI/실제 보호 VM/독립 리뷰 미검증, startupEnabled=false 유지.

### P3-02 고정 CEF 라이브러리 파일 핸들 검사 (미완료)

- #297 exact native compile 46180598781 최종 PASS (runtime 아님). #298 exact head `e4598c6018925e7eeac315fdb45809281e675a6b` Windows/Rust CI 46181058474 PASS, 독립 리뷰 없음.
- CefProfileLayout에 CEF 전용 library-state.json/library-state.stage 고정 경로 정의. 기존 broker-owned exclusive profile lease 하에서 두 기존 파일의 실제 핸들, 단일 링크, no-follow, DOS/GUID 위치, 파일 identity 및 보호 상태 검사 재사용. no-delete-sharing 유지, 권한 변경 후 lease 재사용 금지. 기존 파일 모두 있어야 하며 첫 생성/rename/실제 writer 권한은 제공하지 않는다. 실제 Windows synthetic 파일 핸들 검사 추가 (읽기 전용 profile 거부, 교체 거부, hardlink, authority loss, missing stage); 실제 BitLocker 상태는 합성. 보존된 핸들을 버린 뒤 경로 기반 교체하는 방식 금지. 최종 handle-relative replace, 파일 내용 변경 방지 및 token/ACL, broker generation/runtime admission 및 cleanup coupling 미완료. 신규 CI/실제 보호 VM/독립 리뷰 미검증. startupEnabled=false 유지.

### P3-02 합성 전용 native handle first publication (미완료)

- #299 exact head `26ba190c59ec3b0200d059f524e45dde3e4ee0eb` Windows/Rust CI 46181468760 PASS; native compile CI 46181468761 success, 독립 리뷰 없음.
- VEIL_CEF_PROFILE_ACCESS_TEST 한정 실제 Windows NtSetInformationFile(FileRenameInformation) no-overwrite, 같은 디렉터리 고정 단일 이름, retained directory/data-access handle 및 DELETE-capable source handle, no-write/delete sharing, canonical/volume/single link 및 권한 검사. 기존 파일 덮어쓰기/파일 생성/제품 호출/IPC 없음. 실제 synthetic Windows collision, revoked callback, hardlink, busy source, first publication 회귀 작성. Microsoft FILE_RENAME_INFORMATION/NtSetInformationFile 문서 참조. 기존 파일 교체는 destination identity와 rename 사이 경합 및 사용자 ACL/토큰·브로커 직렬화·보호 runtime 증명 필요; 일반 경로 rename fallback 금지. 새 CI/실제 Windows 실행/보호 VM/독립 리뷰 미검증. startupEnabled=false 유지.

- #299 exact native compile 46181468761 최종 PASS (runtime 아님). #300 최초 head `fa27975fe90f81cbc20a129223800cc96d301afe` CI 46181952385 Windows FAIL: source contract 131 (installed broker acquires existing exclusive profile lock without takeover or mutation), 166/167 PASS; Rust PASS. 기존 ExclusiveProfileLock 검사 범위에 새 test-only native helper가 위치하여 CreateFileW 금지 assertion 실패. helper 블록을 lock class 앞쪽으로 이동하고 기존 assertion과 실제 코드 내용은 변경하지 않았다. 해당 CI는 Windows native fixture 실행 이전에 중단되었으므로 native first publication 결과는 미검증. 수정 head CI 확인 필요; 기존 파일 교체 및 제품 activation은 보류.

### P3-02 라이브러리 사용자 토큰 접근 검사 (미완료)

- #300 수정 exact head `b87477734e2228affaf0b1823f022aee105cc063` Windows/Rust CI 46182351424 PASS (native first-publication fixture 포함), 독립 리뷰 없음.
- LibraryFileInspection의 기존 retained identity pin 및 exclusive profile lock 아래 실제 사용자 impersonation으로 CEF directory list/add-file/traverse/delete-child/read-attributes, committed read-data, staged read/write/append를 OPEN_EXISTING 검사한다. 검사 후 동일 identity 및 session 재확인, 실패 시 owner retire. LaunchTokenLease에서 비상속 impersonation token duplicate와 전후 token snapshot 일치 확인을 추가. 파일 자체 DELETE 접근은 no-delete-sharing pin과 충돌하므로 이번 단계에서는 검사/승인하지 않는다. 실제 rename/replace authority 아님. Windows 합성 ACL fixture에 read/write/append/add-file/delete-child 거부 및 복원 후 재사용 거부, 데이터 불변성 검사 추가. 실제 standard-user token 경로는 opt-in, 실환경 미검증. 제품 writer/IPC/기존 파일 교체 미연결, 신규 CI/독립 리뷰/보호 VM 미검증. startupEnabled=false 유지.

- #301 최초 head `5bd810a9401e961369ef234b14b30bc4534eb580` CI 46182790386 Windows FAIL: source contract 133 (registered CEF environment requires actual user-token filesystem access without service fallback), 167/168 PASS; Rust PASS. 기존 테스트가 첫 impersonation 블록을 검사하여 새 LibraryFileInspection의 블록을 잘못 선택했다. outer profile RequireUserAccess 메서드로 정확히 범위를 한정하고 library impersonation도 별도 범위에서 권한 호출 순서 및 broker status/authority/file mutation 금지를 검사한다. 기존 제품 코드/권한 검사 완화 없음. 실제 ACL fixture 실행 전 중단된 CI이므로 수정 head 전체 CI와 실제 ACL 검증은 미완료. 제품 활성화 및 기존 파일 교체는 보류.

- #301 수정 head `f75c793063bf7e4aabfdbe7ae087fa0d23067e83` CI 46183187321: source checks 통과, Rust PASS; Windows CefProcessBindingCheck에서 fixture line 486 File.ReadAllText(library-state.json) UnauthorizedAccessException. 기존 native first publication 및 profile/handle 회귀 PASS 로그 확인. ACL 거부 후 GetAccessControl로 읽기만 한 원본 객체를 SetAccessControl에 넘기면 변경 플래그가 없어서 DACL 복원이 반영되지 않는 것으로 판단. 파일/CEF directory/scratch 원본 Access descriptor bytes 저장, finally에서 SetSecurityDescriptorBinaryForm(..., AccessControlSections.Access)로 명시 복원 후 DACL SDDL readback 확인. 기존 revoked lifetime은 계속 거부하고 새 exclusive lifetime의 user access 재검증 및 기존 데이터 불변성 검증 유지. 제품 코드/ACL 완화 없음. 수정 head CI 및 실제 보호 VM 미검증, 제품 startupEnabled=false 유지.

- #301 exact head `e779c8a93ddd84dde087cc886a817c9dd519d4f5` CI 46183615053 Windows FAIL: explicit DACL readback mismatch at fixture line 473; source checks/Rust and native first publication PASS. Instead of reapplying old descriptor, change only disposable fixture: require no preexisting explicit deny for fixture SID, add one deny ACE, reload actual ACL in finally, RemoveAccessRuleSpecific for that exact rule, persist, compare complete normalized access rule list and inheritance protection. Verify retired leases remain refused, fresh exclusive user access (library and scratch) succeeds and bytes unchanged. No product ACL changes or relaxed access checks. Revised head CI and actual protected VM unverified; startupEnabled=false.

### P3-02 legacy native replacement boundary experiment (미완료)

- #301 final exact head `e6f1b35bdb10ee382438e0a83a127aff423ff0fc` Windows/Rust CI 46184033011 PASS; ACL denial/restoration fixture passed, no independent review.
- Test-only VEIL_CEF_PROFILE_ACCESS_TEST legacy FileRenameInformation ReplaceIfExists probe uses retained source/data-access directory and existing single-link destination data handle. Test pinned destination refusal, authority loss, linked/busy target refusal and isolated unpinned replacement success. Releasing the destination handle creates an identity-to-rename race; the controlled success case is deliberately NOT a product publication implementation. No POSIX sharing override, no path fallback, no broker call. Microsoft reference https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information . Next assess a race-safe namespace/immutable generation strategy before connecting writer; prove user-token and retained broker/runtime authority and actual protected VM. New CI/review pending; startupEnabled=false.

### P3-02 immutable generation namespace and no-overwrite experiment (미완료)

- #302 exact head `5dbd706373bd0a1b10dd2f47085a5d4ef8db2af7` Windows/Rust CI 46184524319 PASS, no independent review. Legacy pinned target refusal and controlled unpinned replacement pass; no production authority.
- Introduce prospective pure broker catalog: canonical library-<19 ASCII decimal>.json/.stage, bounded 1..1024, complete contiguous committed names, explicit empty initialization, independently trusted minimum required for rollback detection, single pending next stage blocks further allocation; no pruning, rollover, or auto recovery. Unknown names and duplicate/gaps fail closed. Separate test-only native generation append uses no-overwrite source handle and fixed derived name, verifies prior pinned generation does not block next publication, revocation and competing target collision. No payload verification, authenticated rollback anchor, protected namespace ACL, durable flush, staging ownership, runtime admission, or product integration. Test-only directory uses synthetic status; no change to actual profile layout. Next design authenticated content chain/recovery and retained ACL enforcement; bound requires reviewed compaction. New CI/protected VM/review pending; startupEnabled=false.
