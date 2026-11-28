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
