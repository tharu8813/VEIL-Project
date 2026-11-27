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

- [ ] P2-01: 이미 작성된 nested Win32 smoke bundle을 일반 사용자 Windows에서 실제 실행. 두 Job containment, renderer token 제한 및 종료를 실측. native compile 또는 CI artifact를 런타임 PASS로 처리하지 않는다.
- [ ] P2-02: CEF browser/renderer/GPU/utility/network 등의 공식 bootstrap 역할과 실제 프로세스/모듈 목록을 조사. role별 제한 token, integrity, AppContainer/job, site isolation, command-line 및 실행 경로를 관측한다. 단순 PID/부모 metadata나 sampling만으로 전체 수명을 보증하지 않는다.
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
