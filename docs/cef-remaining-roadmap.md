# VEIL CEF 남은 작업 로드맵

기준일: 2026-11-27 (KST). 이 문서는 기존 [CEF 전환 로드맵](cef-transition-roadmap.md)의 후속 실행 계획이다. main의 실제 제품 엔진은 WebView2이며, CEF 실험 코드의 구현 여부와 제품 런타임 검증 여부를 분리한다. 코드 병합, 제품 활성화, 공개 배포는 서로 다른 결정이다.

## 0. 병합 인벤토리 및 현재 차단점 (P0)

조회 당시 main 코드 기준: `fd04e0088048acd783c8e7eddb8fec3991e9ece0`. 통합 PR은 [#226](https://github.com/tharu8813/VEIL-Project/pull/226), head `e48f6bebb0918c58a3fad58021325406f27244fd`. 다음 목록은 조회 시점 스냅샷이며, 이후 작업에서는 정확한 최신 SHA를 다시 확인한다.

| PR | 조회 당시 head | 관계 / CI | 남은 병합 조치 |
|---|---|---|---|
| #222 | cafa33b | #226 조상, 일반/native PASS | 개별 중복 병합 금지; 통합에 포함 |
| #223 | de2f223 | #226 조상, 일반/native PASS | 동일 |
| #224 | f646e9a | #226 조상, 일반 PASS | 동일 |
| #225 | 2a9370f | #226 조상, 일반/native PASS | 동일 |
| #227 | 7665a9e | #226 조상, 일반 PASS | 동일 |
| #226 | e48f6be | draft; 일반 FAIL, native 실행 중(조회 시점) | P0 실패 해결 및 최신 통합 CI/독립 리뷰 |
| #180 | 531882e | 별도 분기; 일반/native PASS, main과 충돌 | 충돌 해소, redirect fixture 재검증 |
| #181 | 6d170b2 | #180 기반 draft; 일반 PASS | #180 의존성 유지, 인증 쿠키 fixture 재검증 |

[#228](https://github.com/tharu8813/VEIL-Project/pull/228)은 #226에 이미 통합된 이력으로, main에 별도로 병합하지 않는다. #222~#225와 #227의 정확한 head가 #226의 조상임을 비교 API로 확인했지만 최종 통합 내용이 모든 기능을 보존하는지는 리뷰로 검증한다. 현재 승인된 독립 리뷰 없음. Copilot quota 실패는 승인 아님.

- [ ] P0-01: [최신 실패 CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45960862547)의 합성 다중 프로세스 시험 실패를 해결한다. 로그: `Fixture pair refused (mode 1): root-membership`. root process handle 신원 재관측, 실제 Job 소속, active/total/terminated accounting 각각을 분리 진단한다. nested Job이 descendant의 별도 job 생성/상속과 호환되는지 실제 Windows에서 확인한다. 검사 삭제, timeout/오류의 PASS 변환, production cap=1 완화 금지.
- [ ] P0-02: #226의 최신 정확한 head에서 일반 CI와 native CI 모두 PASS. 합성 root/descendant 쌍, 외부 PID, 변조 생성시각, cap=1, 두 Job 종료/강제 종료, 프로세스 핸들 보존을 검사한다.
- [ ] P0-03: #226의 통합 범위와 기존 PR 변경의 보존 여부를 독립 리뷰. 필요한 리뷰와 CI를 충족하면 중복 선행 PR들을 통합 병합으로 정리한다. branch protection을 우회하거나 승인 리뷰를 가장하지 않는다.
- [ ] P0-04: #180을 최신 main 또는 통합 기준에 재조정하고 #181의 stacked 변경을 보존한다. 충돌 파일별 의도 검토, 합성 GET/redirect/401/HttpOnly cookie/취소/원본 hash 및 Rust 금고 회귀. 실제 CEF 테스트는 별도 결과 기록.
- [ ] P0-05: 병합 직전 main과 head SHA 재확인, 정확한 head CI/리뷰/충돌 없음 확인, GitHub 정상 PR merge. 병합 후 main CI 및 중복 PR 상태 확인. 실패 시 추가 병합 중단. 브랜치/PR 이력 및 실패 증거 보존.

**완료 조건:** 통합 코드가 main에 있고 필수 CI 성공 및 필요한 검토 기록이 존재하며, 기존 WebView2 제품이 회귀하지 않는다. 제품 CEF 활성화는 여전히 금지.

## 1. 보호된 사전 검사 실측 (P0/P1)

#226에는 별도 CefPreflight 구성, 보호 세션/프로필 lock, 등록 런타임 SHA-256 및 retained file handles, broker one-shot admission, 독립 Job, 종료 확인, 패키지 CMS/게시 및 복구 저널, 비활성 SCM 등록/시험, HKLM bootstrap 등록, 일반 사용자 토큰 관측과 시험 기록 재검증 코드가 작성되어 있다. 현재 main 미병합, 실제 Windows BitLocker 설치 시험 NOT RUN.

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
4. 테스트·보안 검증 실패, 독립 리뷰 미완료, 충돌이 있으면 해당 코드 병합을 보류하고 차단점을 갱신한다. 문서 반영은 제품 배포 승인이 아니다.
5. 기존 자동 개발 작업에 Chromium 직접 포크 지시가 남아 있다면 별도 사용자 승인으로 CEF 전환 로드맵에 맞춰 변경한다. 중복 작업과 병합 충돌을 방지한다.
