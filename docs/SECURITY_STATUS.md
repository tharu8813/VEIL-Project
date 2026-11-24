# VEIL 보안 구현 상태

> **현재 상태 — 오프라인 VM에서 실제 BitLocker로 일반 사용자 Release 브라우저의 정상 종료·강제 종료·Windows 세션 잠금 후 재잠금과 열린 파일 복구를 확인했다. 절전은 모의 알림을 검증했다. 실제 전원 전환, 설치형 서비스, 출시 게이트 검증은 남아 있다.**
> `TESTED`는 연결된 시험 범위의 결과다. 전체 보안 검증은 별도로 판정한다.

| ID | 항목 | 현재 상태 | 증거 링크 / 기준 커밋 | 다음 검증 |
|---|---|---|---|---|
| SEC-001 | 브라우저 엔진 선정 | TESTED | [ADR-0001](decisions/ADR-0001-developer-engine.md) | 출시 엔진은 암호화 저장 및 다운로드 경로 실증 후 재평가 |
| SEC-002 | 영구 쿠키·사이트 데이터 복원 | TESTED | [Phase 0 실측](phase0-evidence.md) | 정상 재시작, WebView2 호스트 강제 종료, 저장 도중 엔진 강제 종료(호스트·BitLocker VM 각 5회), 디스크 가득 참 후 복원 확인. 전원 차단·세션 쿠키 정책 시험 |
| SEC-003 | 잠긴 프로필의 암호화 보호 | IN PROGRESS | [BitLocker VHDX 잠금과 복원 실측](05-encrypted-profile.md#2026-11-23-bitlocker-vhdx-실측), [전체 데스크톱 잠금 시험](05-encrypted-profile.md#전체-데스크톱-상태의-잠금-시험), [ADR-0003](decisions/ADR-0003-volume-broker.md) | 제품 실행 경로에서 보호 볼륨 식별, 자동 잠금 및 강제 종료 복구 |
| SEC-004 | 프로필 평문 임시 파일 방지 | IN PROGRESS | [Procmon 쓰기 추적](05-encrypted-profile.md#2026-11-23-bitlocker-vhdx-실측), [점프 목록 검사](05-encrypted-profile.md#최근-항목과-점프-목록-재검증) | 취소 다운로드의 Downloads 임시 파일 누출은 수정 후 VM 재추적에서 제거 확인. WebView2 맞춤 점프 목록의 프로필 경로 기록은 앱에서 막을 수 없다. 이 프로필을 가리키는 목록을 즉시 삭제하도록 완화했다(남은 목록 0, 일시 기록은 남음). 완화됨, 미해결. VEIL 금고 대화상자는 최근 문서 등록을 끄도록 설정했다. 보호 볼륨에서 대화상자 쓰기와 Windows 정책 효과 확인은 NOT RUN |
| SEC-005 | Rust 인증 암호화 코어 | TESTED | [Phase 2 실측](phase2-evidence.md) | 독립 코어의 왕복, 변조·잘림, 64 MiB 스트리밍, 강제 종료 시험 통과. 의존성 보안 감사 및 앱 통합 |
| SEC-006 | 마스터 키·잠금·백업/복구 | IN PROGRESS | [ADR-0002](decisions/ADR-0002-phase2-age.md), [BitLocker 복구 절차](05-encrypted-profile.md#bitlocker-복구-키-백업과-점검) | 새 설치에 복구 사본 재입력·실제 잠금 해제 확인 로직과 등록 후 드릴 추가. 가짜 공급자 로컬 PASS. 실제 BitLocker 복구·재등록·OS 노출 확인은 NOT RUN |
| SEC-007 | 다운로드 스트리밍 보호 | IN PROGRESS | [Phase 4-0 실측](07-download-vault.md#phase-4-0-실측-2026-11-23), [ADR-0004](decisions/ADR-0004-protection-policies.md) | GET·쿠키 인증·리다이렉트를 VEIL이 재요청해 Rust 코어로 스트리밍 봉인(1 KiB~64 MiB, 중단 시 미발행) PASS. 엔진이 취소 전 받은 본문은 보호 볼륨 임시 폴더·캐시에 잠시 존재(삭제·비움 확인). POST·Blob·Data·Service Worker는 차단. 실제 사이트 성공률, VM 볼륨 밖 쓰기 추적, 디스크 부족 중 봉인 미검증 |
| SEC-008 | 다운로드 금고·내보내기 | IN PROGRESS | [ADR-0005](decisions/ADR-0005-vault-keys-index.md), [금고 지속성 시험](../tests/phase4/vault-persist.mjs), [금고 캡처 시험](../tests/phase4/vault-capture.mjs) | 영구 금고, 인증 정보·내용 키 교체, 보호 프로필 암호 연동, 암호화 백업·복원, Ctrl+J 진행률·취소 로컬 PASS. 실제 설치형 브로커·BitLocker 연동, 보호 볼륨의 쓰기 추적·디스크 부족·전원 차단은 NOT RUN |
| SEC-009 | 표준 네트워크 보호 | IN PROGRESS | [ADR-0006](decisions/ADR-0006-network-guard.md), [네트워크 시험](../tests/phase5/network-guard.mjs) | HTTPS 우선(공용 주소만, 실패 시 경고 페이지, 다운그레이드는 네이티브 막대에서만), 추적 방지 엄격 로컬 PASS. 인터넷 VM에서 실제 사이트 8개 호환성 PASS, HTTP 전용 사이트 경고 PASS(2026-11-24). HTTPS 다운그레이드 리다이렉트는 NOT RUN. 보안 DNS(DoH, secure 모드, 폴백 없음) 로컬 PASS. 인터넷 VM에서 Cloudflare DoH 사용 시 평문 DNS 누출 없음 PASS(대조 이름 확인, 부트스트랩 평문 조회 관찰 안 됨). 프록시 모드와의 조합은 NOT RUN. SmartScreen 데모 차단(켜기)·통과(끄기) PASS. SmartScreen은 사용자 선택(ADR-0007), Edge 원격 구성 연결은 미결 |
| SEC-010 | 프록시 우회/누출 차단 | IN PROGRESS | [ADR-0006](decisions/ADR-0006-network-guard.md), [네트워크 시험](../tests/phase5/network-guard.mjs), [IPv4·IPv6 루프백 장애 시험](phase0-evidence.md) | 프록시 보호 모드(HTTP·SOCKS5, DIRECT 폴백 없음, 루프백 포함, WebRTC 비프록시 UDP 금지, QUIC 끔). 로컬: 탐색·금고 재요청 모두 프록시 경유, 프록시 중단 시 차단·직접 요청 0건, 엔진 인수 확인 PASS. WebRTC 스위치가 런타임에서 무시되던 결함을 2026-11-24에 수정했다(프록시 모드 UDP 후보 1개에서 0개로, 루프백 시험 PASS). DNS 경로·외부 IPv6·STUN을 쓰는 실제 WebRTC·절전·재개·엔진 밖 프로세스는 인터넷 VM NOT RUN(러너 준비됨). OS 수준 강제(WFP) 없음 |
| SEC-011 | 일반 브라우저 UI/기능 | TESTED | [데스크톱 통합 시험](../tests/integration/desktop.mjs) | 실제 키 입력·IME·접근성·DPI 및 주요 사이트 회귀 |
| SEC-012 | 엔진 보안 업데이트 및 배포 | IN PROGRESS | [ADR-0001](decisions/ADR-0001-developer-engine.md) | 패키지 복원, 서명, 라이선스, 설치·업데이트 실증 |
| SEC-013 | 보안 관리자·SmartScreen 설정 | IN PROGRESS | [ADR-0007](decisions/ADR-0007-security-manager.md), [16 보안 관리자](16-security-architecture.md), [시험](../tests/security/smartscreen.mjs) | SmartScreen 기본 켜짐·끄기 선택·프로필별 저장·재시작 복원·모든 탭과 창 적용·손상 값 켜짐 복원 로컬 PASS. 끈 상태에서 SmartScreen 조회 없음(로컬 DoH 관찰). 위협 보호 인터페이스와 Debug 전용 시험 엔진. 1.1(ADR-0008): 서명된 로컬 위협 목록 엔진. 탐색 경고와 네이티브 계속 막대, 금고 리다이렉트 단계·파일 해시 차단, fail-open과 오류 표시를 로컬 6개 시나리오에서 PASS. Release용 목록 서명 키와 피드는 아직 없다(Release 검사는 Unknown). SmartScreen 데모 차단은 인터넷 VM에서 PASS. Windows 정책 재정의와 SmartScreen 다운로드 판정은 NOT RUN |

## 미해결 핵심 위험

1. 두 기본 엔진 프로필과 앱 상태는 일반 디스크에 평문 데이터를 기록한다. 전체 쓰기 경로와 보호 백엔드는 미검증이다.
2. 두 엔진에서 GET 취소를 관찰했다. WebView2 최종 앱에서 여덟 가지 다운로드 요청을 취소했다. 일시적 디스크 쓰기와 스트리밍 금고는 미검증이다.
3. 잠금 해제 중 동일 사용자 악성코드에 대한 접근 제한 및 OS 페이지 파일/덤프의 노출 한계가 존재한다.
4. 프록시 보호 모드는 엔진 인수로만 강제한다. 로컬 시험에서 프록시 중단 시 엔진·금고 모두 직접 연결이 없었다. DNS·외부 IPv6·WebRTC 실측과 엔진 밖 프로세스·OS 수준 우회 방지는 미검증이다.
5. 별도 렌더러 프로세스가 관찰됐다. 샌드박스 적용 범위와 장기 엔진 패치·배포 체계는 미검증이다.
6. 손상된 앱 상태 JSON은 시작에서 차단한다. WebView2 호스트 종료, 암호화 코어 쓰기 도중 종료, 엔진 저장 트랜잭션 도중 엔진 종료, 디스크 가득 참 후 복원을 실측했다. 엔진 종료 시 마지막 수 초의 localStorage 쓰기는 사라질 수 있다(IndexedDB 완료 트랜잭션은 보존). 실제 전원 차단과 엔진의 상태 손상 복구는 미검증이다.
7. 독립 코어는 브라우저 프로필을 감싸지 않는다. 암호화된 파일을 발행할 때 상위 저장소의 파일명·백업 세대와 객체 교체 공격에 대한 인증은 별도 검증이 필요하다.

## 릴리스 판정

**개발자용 PoC만 허용. 정식 보안 브라우저 또는 실사용용 베타 배포 불가.**

| 게이트 | 판정 | 근거 |
|---|---|---|
| A 저장/키 | BLOCKED | 개발 프로필 평문 마커 검출. 정상·강제 종료 및 Windows 세션 잠금 뒤 재잠금 확인. 실제 절전과 제품 경로의 볼륨 밖 쓰기 검증 필요 |
| B 지속성/복구 | 일부 시험 PASS, 게이트 미통과 | 정상 재실행, 호스트 강제 종료, 저장 도중 엔진 강제 종료, 디스크 가득 참 후 복원 PASS. 엔진 종료를 감지해 상태 저장 후 모든 창을 닫는다. 실제 전원 차단, 비밀번호 분실·복구 키 정책 미정 |
| C 다운로드 | BLOCKED | 로컬 금고·내보내기 시험 PASS. 출시용 보호 볼륨의 쓰기 추적과 저장 공간 부족·전원 차단 시험 필요 |
| D 보호 네트워크 | BLOCKED | 프록시 보호 모드 로컬 PASS(직접 연결 0건). 인터넷 VM: DoH 평문 누출 없음, 프록시 모드 WebRTC 후보 0(STUN 사용) PASS. WebRTC 스위치 결함 수정. 외부 IPv6 경로, 절전·네트워크 전환, 엔진 밖 프로세스, OS 수준 강제(WFP) 미검증. 고강도 보호 표시 불가 |
| E 제품 품질 | 미통과 | 앱 탐색 통합 시험 PASS. 실제 키 입력, 호환성, 샌드박스 토큰, 서명과 설치·업데이트 미검증 |

## Phase 1 시험 범위

- PASS — Visual Studio에 설치된 WebView2 SDK 1.0.2903.40으로 .NET 10 빌드. 일반 사용자 Windows에서 WebView2 157.0.4341.60을 실행했다.
- PASS — 앱 탐색, 뒤로/앞으로/새로고침, 탭·팝업·닫은 탭 복원, 북마크·기록·선택 탭 및 사이트 데이터 재시작 복원, 손상된 앱 상태 차단.
- PASS — GET, 인증, 리다이렉트, POST, Blob, Data URL, Service Worker, 8 MiB 요청 취소 및 검사 대상 프로필에 파일명/임시 확장자 잔존 없음. 초기 통합 시험은 같은 페이지에서 연속 스크립트 다운로드 중 인증 케이스가 타임아웃됐다. 유형별 새 탭으로 시험을 분리해 재실행했다.
- NOT RUN — 물리 키 입력, 사용자 관찰, 실제 사이트 로그인·결제·미디어, 세션 쿠키·트랜잭션 도중 종료, 다운로드 실시간 쓰기 추적, NuGet 온라인 복원과 배포 검증.
- 기준 — `5868126` 및 작업 트리. 명령은 [README](../README.MD)의 자동 시험 섹션에 있다. 최종 실행의 단계별 결과는 `.veil-dev/desktop-test-KIwFMB/{write,read,corrupt}.json`에 기록됐다.

## 상태 변경 기록

| 날짜 | 변경 항목 | 수행한 검증 | 증거 | 결정자 |
|---|---|---|---|---|
| 초기 | 문서 생성 | 실제 코드/실험 없음 | 없음 | 미지정 |
| 2026-11-23 | Phase 0 | 두 엔진 정상 재실행, 평문 마커와 GET 취소·본문 접근 관찰, 로컬 프록시 장애 시험 | [Phase 0 결과](phase0-evidence.md) | 개발 작업 |
| 2026-11-23 | Phase 1 | 빌드, 앱 탐색·재시작·다운로드 취소 통합 시험 | [실행기](../tests/integration/desktop.mjs) | 개발 작업 |
| 2026-11-23 | Phase 0 추가 | 전용 WebView2 호스트 강제 종료 후 다섯 저장소 복원 | [Phase 0 실측](phase0-evidence.md) | 개발 작업 |
| 2026-11-23 | Phase 2 | Rust 단위/통합 실행 5건, 자식 강제 종료와 임시 암호문 검증, Clippy | [Phase 2 실측](phase2-evidence.md) | 개발 작업 |
| 2026-11-23 | Phase 0 추가 | IPv4·IPv6 루프백 프록시 장애와 대조군, 단일 GET의 응답 본문 해시 비교 | [Phase 0 실측](phase0-evidence.md) | 개발 작업 |
| 2026-11-23 | Phase 3 선행 | 지정 프로필 재사용, 프로필 경로·하위 경로 충돌, 환경 변수 재정의 차단 | [경로 시험](../tests/phase3/profile-routing.mjs) · [VM 구성](05-encrypted-profile.md#virtualbox-실험-vm-구성) | 개발 작업 |
| 2026-11-23 | 격리 VM 교차 검증 | Windows 11 VirtualBox 게스트에서 `348fe84` Git 무결성 검사, 잠금 파일 기반 오프라인 .NET 복원·빌드, Rust 암호화 코어 및 데스크톱 통합 시험 통과. 프로필 경로 시험은 엔진 오류 대화상자를 표시했고 종료 코드 1을 반환했다. | 게스트 `C:\VEIL-Project\.veil-dev\{rust,profile,desktop}.log` | 개발 작업 |
| 2026-11-23 | 격리 VM 기준점 | 전송 DVD 분리, VM 네트워크 연결 및 클립보드·드래그 앤 드롭 비활성, 전원 종료 확인 후 `01-veil-tools-offline` 생성. `00-windows-clean`과 이전 오프라인 기준점 보존. | VirtualBox 스냅샷 `e088084b-d435-44e5-b3ba-c7a8e043deed` | 개발 작업 |
| 2026-11-23 | Phase 3 프로필 오류 재시험 | WebView2 초기화 전에 엔진 폴더를 막는 파일을 감지해 프로필 열기를 중단하도록 수정. 호스트와 오프라인 VM에서 경로 시험 PASS. 호스트 데스크톱 통합 PASS. 게스트에서 BitLocker 및 디스크 이미지 관리 명령 확인. | 게스트 작업 트리의 `348fe84` + 경로 처리 패치 · `.veil-dev/profile-routing-xEcvRj/evidence.json` | 개발 작업 |
| 2026-11-23 | Phase 3 BitLocker 볼륨 실험 | 격리 VM 전용 1 GiB VHDX를 XTS-AES-256으로 100% 암호화한 뒤 일반 사용자 WebView2에서 합성 저장소 5종을 기록했다. 잠긴 원본의 프로필 열람 차단, 분리 사본 해시 일치, 사본 내 ASCII 마커 부재, 잠긴 사본의 잘못된 암호 거부를 관찰했다. | 게스트 `.veil-dev/bitlocker-before.json`, `.veil-dev/bitlocker-write.pml` | 개발 작업 |
| 2026-11-23 | Phase 3 BitLocker 사본 복원과 추적 | 올바른 임시 암호로 잠금을 해제한 사본에서 사이트 저장소 5종 복원. 시험 PID와 자식 PID의 성공한 드라이브 경로 파일 쓰기 1,824건은 V:, 1건은 게스트 C:의 의도된 결과 JSON이었다. | [실험 범위와 증거 위치](05-encrypted-profile.md#2026-11-23-bitlocker-vhdx-실측) | 개발 작업 |
| 2026-11-23 | 전체 데스크톱 잠금 시험 준비 | 별도 프로필 폴더와 잠금·해제 검증 지점을 통합 시험에 추가했다. 호스트에서 합성 경로 차단으로 시작 거부, 원점 요청 없음 및 재개 후 북마크·기록·탭과 사이트 데이터 복원을 확인했다. 이 실행에서는 실제 BitLocker 잠금을 사용하지 않았다. | [시험 절차](05-encrypted-profile.md#전체-데스크톱-상태의-잠금-시험) · `.veil-dev/desktop-test-fau8rb` | 개발 작업 |
| 2026-11-23 | 전체 데스크톱 BitLocker 실측 | 첫 실행의 기존 상태 파일 교체가 실패해 탭 스냅샷이 오래된 채 남았다. 동일 디렉터리에서 이름을 바꿔 발행하도록 변경하고 종료 후 상태 파일 검증을 추가했다. 재시험에서 실제 잠금 중 앱 시작 거부·원점 요청 없음·잠금 해제 후 북마크·기록·선택 탭·사이트 데이터 복원 및 손상 파일 차단을 확인했다. | 게스트 `C:\VEIL-Project\.veil-dev\desktop-test-tmRNbj` | 개발 작업 |
| 2026-11-23 | 전체 데스크톱 강제 종료 회귀 | 별도 합성 임시 상태 파일을 쓰는 브라우저 호스트를 강제로 종료했다. 마지막 정상 상태 보존과 사이트 데이터·북마크·기록·선택 탭 복원을 확인했다. 임시 상태만 남은 프로필은 시작을 중단했다. 전원 차단과 엔진 자체의 쓰기 도중 종료는 시험 범위 밖이다. | [테스트 실행기](../tests/integration/desktop.mjs) · 호스트 `.veil-dev/desktop-test-QQqSnB` | 개발 작업 |
| 2026-11-23 | 격리 VM 강제 종료 회귀 | 별도 복제본에서 잠금 파일 기반 오프라인 빌드 및 `--crash-cycle` 통과. 게스트 일반 디스크의 합성 프로필을 사용했다. BitLocker 볼륨에서의 강제 종료 조합은 별도 시험 대상이다. | 게스트 `C:\VEIL-Project\.veil-dev\crash-verify\.veil-dev\desktop-test-uONpSq`의 `crash.json`, `orphaned.json`, `read.json` | 개발 작업 |
| 2026-11-23 | 보호 프로필 권한 분리 결정 | 브라우저 일반 권한 + 볼륨 전용 로컬 서비스 구조 승인. 볼륨 식별, 호출자 인증, 시작 차단, 핸들 종료·자동 잠금, 설치·제거 기준 기록. 구현과 시험은 하지 않았다. 호스트에서 `--profile-parent`와 `--crash-cycle` 조합 PASS. | [ADR-0003](decisions/ADR-0003-volume-broker.md) · 호스트 `.veil-dev/desktop-test-RlmZGw` | 사용자 승인 |
| 2026-11-23 | 브라우저 시작 차단 검사 | ADR-0003 1단계. 관리자 권한 거부, 접합점·재분석 지점·네트워크 경로 거부, 최종 경로 일치, 볼륨 GUID 확인 및 브로커 인터페이스(개발용 미검증 구현) 추가. 호스트 경로 시험(접합점 2종 포함)과 데스크톱 통합·강제 종료 시험 PASS. 관리자 실행 거부, VM·BitLocker 재실행, 브로커 미구현. | [시작 차단 검사](05-encrypted-profile.md#브라우저-시작-차단-검사-adr-0003-1단계) · 호스트 `.veil-dev/profile-routing-8h1Fnq` | 개발 작업 |

## Phase 3 보호 프로필 실행 흐름 구현 (2026-11-23)

코드: [ProtectedProfileSession.cs](../apps/desktop/ProtectedProfileSession.cs),
[ProfileUnlockDialog.cs](../apps/desktop/ProfileUnlockDialog.cs),
[BrokerClient.cs](../apps/desktop/BrokerClient.cs),
[Broker.cs](../apps/broker/Veil.VolumeBroker/Broker.cs).

- IMPLEMENTED / **NOT RUN** — 등록된 브로커에 Attach / Unlock / Lock / Detach 클라이언트 호출 추가. 보호 상태 확인 후 암호를 입력받아 잠금을 해제하고, WebView2와 앱 상태는 그 후에만 연다.
- IMPLEMENTED / **NOT RUN** — 이미 잠금 해제된 볼륨을 이용한 인증 우회 거부. 브로커도 중복 Unlock 요청에 `AlreadyUnlocked` 반환. 가짜 백엔드 자동 시험에 거부 조건 추가.
- IMPLEMENTED / **NOT RUN** — 비밀번호 임시 `char[]`와 전송 요청 버퍼 정리, 정상 종료 시 비강제 잠금 요청. 클라이언트 종료 감시는 기존 브로커 코드 사용. 열려 있는 핸들이 있을 때 자동 잠금이 성공한다고 주장하지 않는다.
- IMPLEMENTED / **NOT RUN** — Release 실행 시 등록된 보호 설정이 없는 경우 평문 개발 프로필로 폴백 차단. Debug 개발 흐름 유지.
- **PASS — 호스트**에서 앱·브로커 Debug/Release 빌드, 합성 브로커 named pipe 시험 및 데스크톱 강제 종료 회귀를 실행했다. 잠긴 볼륨의 보호 상태 미확정과 잠금 해제 후 보호 미확인을 별도로 처리한다. 합성 시험에서 보호 검증 실패 시 브로커가 잠금 해제를 실패로 처리하고 볼륨을 다시 잠갔다. 증거는 `.veil-dev/broker-test-bfI3EP`, `.veil-dev/desktop-test-gqTDVq`에 있다.
- **PASS** — 오프라인 `Veil-VM-TESTER`에서 전용 1 GiB VHDX를 XTS-AES-256으로 전체 암호화한 뒤, [실제 BitLocker 콘솔 시험기](../tests/broker/bitlocker-vm.mjs)로 `Status → Attach → Unlock → Lock → Detach`를 실행했다(`BROKER_TEST_EXIT=0`). 연결 직후 잠금 상태에서 `readyToUnlock=true`를 관찰했고, 잠금 해제 직후 `fullyEncrypted`, `protectionOn`, `unlocked`, `protectorPresent`가 모두 `true`였다. 재잠금 후 `unlocked=false`, 분리 후 Windows `Get-DiskImage`의 `Attached=False`, 브로커 정상 종료를 각각 확인했다. 게스트 증거는 `C:\VEIL-Project\.veil-dev\broker-real\broker.log`에 있다.
- **시험 범위** — 관리자 콘솔의 개발용 브로커와 Node 클라이언트를 사용했다. 시험용 암호는 게스트에서 생성해 표준 입력으로 전달했다. 시험 후 VM을 정상 종료했고 기존 오프라인 스냅샷, 네트워크 연결 없음, 클립보드 및 끌어 놓기 비활성화를 확인했다.
### 일반 사용자 브라우저 실측 (2026-11-23)

- **PASS** — Release 데스크톱과 Debug BitLocker 브로커를 빌드하고 오프라인 게스트 `C:\VEIL-Project\.veil-dev\desktop-real`에 배치했다. 게스트에서 패키지 SHA-256을 대조한 뒤 전송용 가상 DVD를 분리했다.
- **최초 실행 실패 및 수정** — 첫 연결에서 BitLocker 조회가 기존 8초 요청 제한을 넘겼다. 브로커는 핸들러에서 확인한 상태를 재사용하고, 잠금·분리 뒤에는 상태를 한 번 새로 조회한다. 클라이언트는 상태 조회에 8초, 변경 명령에 45초의 응답 제한을 적용한다.
- **PASS** — 일반 사용자 Release WPF가 잠금 해제 대화상자를 표시하고 보호 프로필의 WebView2 새 탭을 열었다. 열린 상태에서 `V:\Profile\webview2\EBWebView` 존재와 Windows BitLocker의 `Unlocked`, `FullyEncrypted`, `ProtectionStatus=On`을 확인했다. 정상 창 닫기 후 동일 브라우저 PID의 `Lock=ok`, `Detach=ok`, 프로세스 종료와 Windows `Get-DiskImage`의 `Attached=False`를 확인했다. 브로커 로그에서 종료 시 첫 상태 확인부터 분리 성공까지 약 22초였다.
- **시험 범위** — 관리자 ACL이 적용된 제품 클라이언트 등록을 사용했다. 브로커는 격리 VM에서만 서명되지 않은 시험 빌드를 허용한 개발용 콘솔이었다. 게스트 `C:\VEIL-Project\.veil-dev\desktop-real`의 `broker.log`, `desktop-result.json`에 증거를 남겼다. 시험용 등록을 제거하고 VM을 정상 종료했다.
- **PASS** — 가짜 브로커 회귀와 데스크톱 `--crash-cycle` 통합 시험. 다운로드 아이콘 시험은 디스패처의 UI 갱신을 기다리도록 수정했다. 호스트 증거는 `.veil-dev/broker-test-qxujsW`, `.veil-dev/desktop-test-J4mXWZ`에 있다.
- **NOT RUN** — 실제 전원 전환 중 재잠금, 설치형 서비스와 서명 인증, 제품 모드의 볼륨 밖 쓰기 계측.
- **출시 불가** — Gate A, Gate B는 모두 기존과 동일하게 미통과. VM 검증 및 보안 리뷰가 필요하다.

### 실제 볼륨의 강제 종료·열린 파일 복구 시험 (2026-11-23)

- **PASS — 호스트**에서 가짜 BitLocker 브로커 회귀 시험을 재실행했다. 증거는 `.veil-dev/broker-test-PYf3Yb`에 있다.
- **PASS — 강제 종료** — 오프라인 `Veil-VM-TESTER`의 시험용 BitLocker VHDX에서 일반 사용자 Release 브라우저로 WebView2를 열고 브라우저 프로세스만 종료했다. 브로커의 첫 재잠금 시도가 4.6초 후 성공했고, Windows `Locked`와 프로필 접근 불가를 확인했다.
- **PASS — 열린 파일 복구** — 같은 보호 볼륨의 합성 파일에 대한 독점 핸들을 남기고 브라우저를 종료했다. 브로커는 `LockRefused`를 세 차례 기록했다. Windows가 `Unlocked`를 보고하고 열린 핸들이 유효한 상태에서 핸들을 닫았다. 3.5초 후 네 번째 시도가 성공했고, Windows `Locked`와 프로필 접근 불가를 확인했다.
- **범위와 증거** — 게스트 `C:\VEIL-Project\.veil-dev\desktop-crash`의 `broker-live.log`와 `crash-result.json`에 기록했다. 최초 UI 자동화는 포커스를 잡지 못해 중단됐으며, 두 성공 사례는 게스트 콘솔에서 단계별로 실행했다. 개발용 콘솔 브로커의 재잠금을 확인한 뒤, 시험 종료 절차에서 관리자가 VHDX를 분리했다. 재시도 횟수를 소진한 경우의 복구는 미검증이다.

### Windows 세션 잠금과 절전 알림 (2026-11-23)

- **구현** — 보호 프로필 인증 전에 현재 세션의 잠금 알림을 등록한다. 세션 잠금과 절전·절전 복귀 알림은 보호 브라우저를 종료한다. 열린 창 상태를 한 번 저장한 뒤 WebView2 뷰와 프로필 핸들을 닫고 기존 비강제 재잠금 경로로 진행한다. Windows 이벤트 콜백은 볼륨 작업을 기다리지 않는다.
- **PASS — 호스트** — 데스크톱 Debug/Release 빌드, 가짜 브로커 회귀, 데스크톱 `--crash-cycle` 통합 시험. 증거는 `.veil-dev/broker-test-C0gwJW`, `.veil-dev/desktop-test-tChpVh`에 있다.
- **PASS — 모의 절전 알림** — 오프라인 VM에서 `PBT_APMSUSPEND`를 일반 사용자 Release 브라우저에 전달했다. 실제 BitLocker VHDX에서 `Lock=ok`, `Detach=ok`, 프로세스 종료와 프로필 접근 불가를 확인했다. 시험의 완료까지 19.8초였으며, `desktop-events/SuspendMessage.json`을 게스트에 남겼다.
- **PASS — 실제 세션 잠금** — 보호 프로필이 열린 상태에서 `LockWorkStation`으로 Windows 잠금 화면을 띄웠다. 24.6초 후 브라우저 종료, 해당 브라우저 PID의 `Lock=ok`와 `Detach=ok`, Windows 이미지 분리, 프로필 접근 불가를 확인했다. 게스트 `C:\VEIL-Project\.veil-dev\desktop-events\SessionLock.json`과 `broker.log`에 증거를 기록했다. 시험용 등록을 제거하고 VM을 정상 종료했다.
- **NOT RUN — 실제 절전·복귀** — 시험 VM의 BIOS/펌웨어는 `powercfg /a` 기준 S0 저전력 유휴, S1/S2/S3, 최대 절전 모드를 제공하지 않는다. Windows의 실제 전원 전환이나 전환 완료 전에 잠금이 끝나는지는 검증하지 않았다. [Windows 절전 알림 처리 시간](https://learn.microsoft.com/windows/win32/power/pbt-apmsuspend)

### Phase 3 설치 서비스 시작 및 복구 보강 (2026-11-23)

- **IMPLEMENTED / CI PENDING** — 설치 시 볼륨 브로커를 수동 시작이 아니라 자동 시작으로 등록한다. 예상치 못한 종료는 SCM에서 유한한 3회 재시작을 시도하도록 구성한다. 서버는 재기동 직후 등록된 볼륨을 먼저 확인하고 잠금/분리한 뒤 명령을 받는다.
- **IMPLEMENTED / CI PENDING** — 클라이언트 제품 구성은 서비스 PID 확인을 우회하지 못하도록 `requireWindowsService=true`, 브로커 실행 파일의 절대 경로, 보호 프로필 절대 경로를 필수로 검증한다. 누락되거나 잘못된 브로커 구성 값은 제어된 오류로 거부한다.
- **TEST COVERAGE** — Windows PowerShell 5.1 설치 스크립트 파싱과 비파괴적 Node 정적 정책 검증을 CI에 추가한다. 이는 설치·서비스 동작을 실행한 시험이 아니며 격리 VM에서 설치/재부팅/서비스 충돌 후 재잠금/제거 순서 및 Windows 11 Home 대상 지원 여부를 검증해야 한다.
- **SECURITY LIMIT** — 브로커 강제 종료 시 이미 열린 브라우저나 WebView2 핸들이 잠금을 거부할 수 있으며, 이 때 재시작이 데이터를 즉시 보호한다는 보장은 없다. 실제 VM 재시작 회복 실측은 미실행. Gate A/B 판정은 그대로 BLOCKED이다.

### BitLocker 복구 사본 등록과 점검

- **IMPLEMENTED** — 등록 전 잠긴 볼륨에서 복구 사본을 확인한다. 새 설치에는 숫자 보호기 ID만 기록하고, 정상 실행 시 해당 보호기 존재를 확인한다. 기존 등록 형식은 유지한다. 복구 사본 확인 기능을 쓰려면 검증된 숫자 보호기 ID가 있는 등록이 필요하다.
- **PASS — 로컬 가짜 공급자** — 새 보호기 생성·재시도, 미등록 보호기 선택 거부, 틀린 암호 및 다른 유효 보호기 거부, 다른 볼륨 거부, 재잠금 실패 시 등록 중단을 확인했다. 실제 BitLocker 호출과 핵심 서비스 등록은 이 시험에서 실행하지 않는다.
- **NOT RUN — 실제 복구** — 집계된 VM 세션에서 새 설치와 기존 복구 보호기가 있는 설치, 등록 후 복구 드릴, 암호 변경과 새 보호기 ID 재등록, 기록에 비밀번호가 남는지 확인한다.

## Phase 4 영구 금고 로컬 검증 (2026-11-23)

- **PASS — 로컬 Windows** — Debug·Release 빌드, 데스크톱 통합 시험, 다운로드 캡처 및 지속성 시험. 브라우저 재시작 후 다운로드 5개를 암호화 목록에서 읽고 바이트 단위로 동일하게 내보냈다. 잘못된 암호를 거부했다.
- **PASS — 자동 검사** — Rust 테스트 9건 및 Clippy, Node 러너 구문 검사, 설치 스크립트 계약·쓰기 추적 파서 테스트 11건. Rust 강제 종료 시험의 자식 프로세스 전용 테스트는 단독 실행에서 제외된다.
- **PASS — 손상·복구** — 목록 삭제, 메타데이터 변조, 잘못된 객체 파일명 및 남은 스테이징을 함께 주입했다. 목록을 재구성하고 손상된 메타데이터의 항목을 원본 객체에서 복구했다. 잘못된 파일명의 객체는 보존했다. 변조된 객체의 내보내기는 거부했다.
- **PASS — 인증 정보 일괄 교체** — Windows에서 기존 번들의 이름 변경을 막아 마지막 발행을 실패시켰다. 기존 번들이 바이트 단위로 보존되고 금고가 다시 잠겼다. 재시도 성공 후 이전 암호와 코드를 모두 거부하고 새 인증 정보로 각각 열었다. Rust 시험에서는 기존 두 파일 형식의 전환과 이전 파일 정리가 중단된 경우도 확인했다.
- **PASS — 보호 프로필 암호 연동 로컬 시험** — 합성 암호로 실제 금고 프로세스를 실행했다. 새 금고 복구 코드 확인, 기존 암호로 연결, 복구 코드로 재연결, 취소 시 잠금 유지, 한글과 탭이 포함된 암호 전달, 다음 시작 시 추가 입력 없이 목록을 여는 동작을 확인했다.
- **PASS — 암호화 백업과 복원** — 합성 항목 5개의 메타데이터를 복원했고, 64 MiB 항목은 원본과 바이트 단위로 대조했다. Rust 시험에서 틀린 암호·변조·잘림·대상 충돌을 거부하고 실패 후 새 금고가 발행되지 않는 것을 확인했다. 내보내기 충돌 시에도 기존 대상 파일을 보존한다.
- **PASS — 내용 키 교체** — 다운로드·백업 키를 교체하고 예전 키가 새 암호문을 열지 못하는 것을 확인했다. Windows에서 활성 세대 파일 교체를 차단한 경우 기존 세대와 복구 코드가 유지됐다. 성공적으로 전환한 금고는 새 복구 코드로 다시 열었고, 다운로드 5개의 메타데이터와 64 MiB 파일의 바이트가 보존됐다.
- **PASS — 다운로드 진행률과 취소** — 로컬 Ctrl+J의 WPF 컨트롤로 전체 크기를 아는 전송과 알 수 없는 전송을 취소했다. 대기 항목 하나를 취소했을 때 다른 전송은 계속됐다. 취소 3건은 암호화 객체·목록·스테이징을 남기지 않았고, 같은 금고 세션의 다음 다운로드가 인증됐다.
- **PASS — 종료 후 파일 검사** — 합성 다운로드 본문은 검사 시점에 프로필 파일에서 검색되지 않았다. 엔진이 취소 전에 보호 경로의 임시 파일에 쓴 양은 별도로 기록했다.
- **NOT RUN — 통합 VM 시험** — 설치형 브로커와 실제 BitLocker 볼륨에서 한 번의 입력으로 브라우저·금고 잠금 해제, 정상 종료·세션 잠금 후 재잠금, 보호 볼륨에서 Release 금고의 쓰기 추적·디스크 부족·전원 차단과 내보내기 스테이징 잔여물 검증. 내용 키 교체를 중단한 뒤 남은 암호화 파일과 복구도 포함한다. Gate C는 계속 출시 차단 상태다.
- **IMPLEMENTED — 통합 러너와 계측** — 오프라인 VirtualBox 게스트와 폐기용 BitLocker 볼륨 사전 점검, 일반 사용자 Debug 시험 실행, 항목별 증거 수집, Procmon 활동 확인을 스크립트로 연결했다. 로컬에서는 계획 출력과 실패 격리, 볼륨 경로 전달, 증거 분석을 검증했다. 게스트 실행과 설치형 Release 판정은 **NOT RUN**이다. [통합 VM 실행 절차](10-test-strategy.md#phase-34-통합-vm-실행)
- **GitHub Actions** — [PR #5의 Windows·Rust 검사](https://github.com/tharu8813/VEIL-Project/actions/runs/45121435499)가 통과했다. 후속 키 교체, 보호 프로필 암호 연동, 백업·복원, 진행률·취소의 원격 검사 결과는 [PR #6의 검사 기록](https://github.com/tharu8813/VEIL-Project/pull/6/checks)에서 확인한다.

### Rust 코어 강제 종료 테스트 CI 안정화 (2026-11-23)

- `tester` 커밋 `ed1a525`의 GitHub Linux Rust 테스트는 **FAILED**: `forced_termination_never_publishes_partial_ciphertext`에서 테스트 자식 프로세스가 준비 마커를 15초 안에 기록하지 못했다. 나머지 4개 활성 테스트는 통과했다 (GitHub Actions run 45103920485).
- 수정을 통해 scrypt 기반 키 잠금 해제가 과부하 환경에서 오래 걸리는 상황을 고려해 **준비 마커 대기 시간만 90초로 변경**하고, 조기 종료/소요 시간 진단을 추가했다. 모든 검증 조건 (임시 암호문, 완성본 부재, 기존 암호문 불변, 인증 실패)은 그대로 유지한다.
- CI는 KDF 메모리/CPU 경쟁을 줄이기 위해 Rust 테스트를 순차 실행한다. **CI 결과 확인 전에는 수정 성공으로 표시하지 않으며**, 실패가 재현되면 실제 crypto/환경 이슈를 조사한다.
| 2026-11-23 | SEC-004 볼륨 밖 쓰기 대응 | 앱 프로세스와 WebView2 하위 프로세스가 상속하는 TEMP/TMP를 프로필의 `temp` 폴더로 이동하고 시작·종료 시 비우도록 변경. Procmon CSV 분석기(프로세스 트리 추적, 직접·생성·이름 변경·매핑 쓰기 분류) 단위 시험 6건 PASS, 호스트 데스크톱 통합·강제 종료·경로 시험 PASS. 하위 엔진 프로세스의 TEMP 상속과 실제 볼륨 밖 쓰기는 VM 추적 전까지 미검증. | [분석기](../tests/security/outside-writes.mjs) · [VM 절차](../tests/security/trace-writes.ps1) | 개발 작업 |
| 2026-11-23 | SEC-004 격리 VM 쓰기 추적 | 일회성 BitLocker VHDX와 일반 사용자 토큰으로 데스크톱 `--crash-cycle` 하네스를 Procmon으로 2회 추적. 1차에서 취소된 다운로드 8건의 본문이 `Downloads\<GUID>.tmp`에 기록됨을 발견해 엔진 기본 다운로드 폴더를 프로필 볼륨으로 이동. 2차에서 해당 누출 제거와 V: 쓰기 4,803건 확인. WebView2 점프 목록(엔진 프로필 경로 포함) 쓰기 2개 파일은 미해결. 분석기 단위 시험 7건 PASS. | 게스트 `C:\VEIL-Trace-20261123-130538\.veil-dev\write-trace` · [절차](05-encrypted-profile.md#볼륨-밖-쓰기-추적-sec-004) | 개발 작업 |
| 2026-11-23 | SEC-004 점프 목록 장시간 탐색 추적 | 루프백 합성 페이지로 세션 3회(서로 다른 페이지 90개, 재방문, 닫은 탭 24개)를 호스트와 오프라인 VM에서 실행. VM Procmon 추적에서 V: 쓰기 8,683건, 볼륨 밖은 점프 목록 2개 파일뿐이며 내용은 프로필 경로만, URL·제목 없음. 앱 수준 점프 목록 차단 시도(Chromium 스위치, Edge 기능 플래그, APPDATA/USERPROFILE 변경)는 효과 없어 코드에 남기지 않음. | 게스트 `C:\VEIL-Trace-20261123-133611\.veil-dev` · [러너](../tests/security/browse-session.mjs) | 개발 작업 |
| 2026-11-23 | Phase 3 지속성·복구 시험 | 엔진 브라우저 프로세스 종료 감지(상태 1회 저장 후 모든 창 종료, 렌더러 실패는 해당 탭만 안내) 추가. IndexedDB·localStorage 연속 쓰기 중 엔진 강제 종료 5회: 호스트 PASS, 격리 VM BitLocker 볼륨 PASS(완료 트랜잭션 보존·번호 연속, 마지막 수 초의 localStorage는 유실 가능). 128 MiB 볼륨을 가득 채운 상태에서 VEIL 정상 종료, 공간 확보 후 상태·저장소 복원 PASS(VM). 실제 전원 차단 시험은 시간 관계로 건너뜀(NOT RUN). 민감 데이터 저장 위치와 OS 기록 정책 문서 추가. | [엔진 종료 시험](../tests/phase3/engine-kill.mjs) · [디스크 가득 참](../tests/phase3/disk-full.mjs) · [저장 위치 목록](15-sensitive-data-inventory.md) · 게스트 `C:\VEIL-P3-20261123-140133\.veil-dev` | 개발 작업 |
| 2026-11-23 | Phase 4-0 다운로드 금고 경로 검증 | 정책 결정 ADR-0004 기록. 엔진 다운로드는 즉시 취소하고 GET 계열은 VEIL이 쿠키를 붙여 재요청, `veil-vault.exe`로 스트리밍 봉인. Rust 브리지 시험 3건(정상·중단·길이 불일치·잘못된 암호, 실패 시 객체·스테이징 없음) PASS. 호스트 금고 시험 2회 PASS: 1 KiB·8 MiB·64 MiB·쿠키 인증·리다이렉트 보관 및 인증, 연결 끊김 미발행, POST 차단. 엔진이 다운로드 본문 앞부분을 HTTP 디스크 캐시에 남기는 것을 발견해 취소 시 캐시 비움 추가, 재시험에서 프로필 전체에 페이로드 없음. 기존 데스크톱 8가지 다운로드 시험 기대값 갱신. | [실측](07-download-vault.md#phase-4-0-실측-2026-11-23) | 개발 작업 |
| 2026-11-23 | Phase 5 네트워크 보호 1단계 | ADR-0006. 표준 보호: HTTPS 우선과 경고 페이지, 추적 방지 엄격. 프록시 보호: 고정 프록시·루프백 포함·WebRTC 비프록시 UDP 금지·QUIC 끔, 금고 재요청도 같은 프록시만 사용, 시작 시 경로 확인 표시. 시험 중 스크립트가 `veil://allow-http`로 HTTP 다운그레이드를 일으키는 결함을 발견해(사이트가 연 팝업에도 적용 가능) 페이지 링크를 제거하고 네이티브 막대로만 허용하도록 수정. 로컬 network-guard 시험 PASS(프록시 경유 5건, 직접 0건, 중단 시 차단) 및 기존 회귀 전체 PASS. DNS·IPv6·WebRTC 실측·절전은 VM NOT RUN. | [ADR-0006](decisions/ADR-0006-network-guard.md) · [시험](../tests/phase5/network-guard.mjs) | 개발 작업 |
| 2026-11-23 | Phase 5 보안 DNS | 런타임 문자열 조사로 Chromium 보안 DNS 환경설정을 확인하고, 엔진 Local State에 secure 모드와 템플릿을 쓰면 적용됨을 실측(죽은 DoH 주소로 example.com 해석 실패). 설정 화면에 시스템 DNS·Cloudflare·Quad9·Google·직접 입력 추가. 로컬 DoH 응답기 시험 PASS: 합성 이름이 DoH로만 해석되고 응답기 중단 시 실패, VEIL이 설정을 기록. 시험용 인증서 신뢰 훅은 Debug 전용이며 Release 바이너리에 없음을 확인. 엔진 배경 조회(config.edge.skype.com, nav-edge.smartscreen.microsoft.com) 기록. | [ADR-0006](decisions/ADR-0006-network-guard.md) · [시험](../tests/phase5/dns-protection.mjs) | 개발 작업 |
| 2026-11-23 | 보안 관리자 기반(ADR-0007) | 프로필 단위 보안 관리자와 Microsoft SmartScreen 설정(기본 켜짐, 공식 API `IsReputationCheckingRequired`, 첫 탐색 전 모든 WebView 적용, 변경 즉시 전 화면 반영·다시 읽기 확인, 저장·적용 실패 표시) 추가. 위협 보호 인터페이스(Malicious/NotDetected/Unknown/Error)와 Debug 전용 시험 엔진, Release 등록 차단. SEC-009의 SmartScreen 배경 연결 결정 완료(사용자 선택). 로컬 smartscreen 시험 PASS, 로컬 DoH 관찰로 꺼짐 시 SmartScreen 조회 0건. 실제 경고·Windows 정책·다운로드 판정 NOT RUN. | [ADR-0007](decisions/ADR-0007-security-manager.md) · [시험](../tests/security/smartscreen.mjs) | 개발 작업 |
| 2026-11-23 | Rust CI 시간 단축 | 병목은 Debug 빌드에서 최적화 없이 실행되는 scrypt(log_n=17, 128 MiB)였다. 호스트 테스트 13건 합계 571초 → 의존성만 최적화(`profile.dev.package."*"`)한 뒤 12.9초. Release 테스트 빌드 12.2초와 비슷하며 이 크레이트의 디버그 검사는 유지된다. 직렬과 병렬 비교: 1스레드 12.9초·142 MiB, 4스레드 4.5초·527 MiB. 과거 불안정성 때문에 CI는 직렬을 유지한다. KDF·암호화 파라미터, Release 프로필, 시험 내용은 바꾸지 않았다. Cargo 캐시와 테스트별 시간 요약을 추가했다. | [실행기](../scripts/rust-timed-tests.sh) · [CI](../.github/workflows/veil-checks.yml) | 개발 작업 |
| 2026-11-23 | 단일 오프라인 VM 일괄 시험 (`baf7848`) | 격리 VM(Veil-VM-TESTER, 네트워크 미연결)에서 폐기용 BitLocker XTS-AES-256 볼륨 V:(1 GiB)와 W:(256 MiB)를 쓰고, Procmon 추적 중 Debug 데스크톱을 일반 사용자로 실행했다. **PASS 8건**: profile-routing, engine-kill, vault-capture, vault-persistence, desktop-crash-cycle, network-guard, profile-disk-full(W:). 보호 볼륨 안 쓰기는 99,988건(엔진 77,443건). **FAIL**: jump-list(엔진이 `CustomDestinations`에 프로필 경로 기록, URL·제목은 없음. 기존에 알려진 미해결 문제)와 physical-writes. 볼륨 밖 쓰기 원인을 분석했다. 처음 분석에서는 347건·92개 경로였다. 이 가운데 하네스 PowerShell, Windows Prefetch(OS 기록), 실행기 증거 파일의 System 플러시, 시험 보고서 파일은 VEIL 데이터가 아니므로 분석기에서 별도 분류했다. PID 재사용으로 프로세스를 잘못 귀속하는 문제도 고쳤다. 같은 추적을 다시 분석한 결과 34건·14개 경로가 남았다. 점프 목록 12개 경로가 유일한 제품 경로 누출이다. 나머지 2개는 Debug 전용 `--probe`의 0바이트 TEMP 쓰기 가능 검사(Delete-On-Close)였다. 이 문제는 엔진 생성 직전 TEMP를 프로필 안으로 옮기도록 수정했고, 호스트 회귀 시험 5건에서 PASS했다. VM 재추적은 하지 않았다. **NOT RUN**: dns-protection·smartscreen(게스트에 openssl 없음, 호스트에서는 PASS), 설치형 Release·복구 드릴·전원 차단·금고 디스크 부족, 인터넷 VM 항목. 실패 증거를 남기려고 시험 볼륨은 잠그지 않고 연결된 상태로 보존했다. Windows Prefetch에 엔진이 시작할 때 연 파일 경로가 남을 수 있는지는 별도 검토 대상이다. | 게스트 `C:\VEIL-Batch\RESULTS.ZIP`, `C:\VEIL-Batch\src\.veil-dev\phase34-trace\outside-writes-v3.json` · [분석기](../tests/security/outside-writes.mjs) | 개발 작업 |
| 2026-11-24 | SEC-004 점프 목록 완화 | 목록 내용은 Edge "새 창"·"새 InPrivate 창" 작업이고, 각 작업에 `--user-data-dir=<엔진 프로필>`이 들어 있다. AppID 해시(CRC64)를 확인한 결과 사용자 Edge 목록과는 다른 파일이지만, AppID 문자열은 찾지 못했다. 셸이 임시 파일에 먼저 쓰는 구조라 앱에서 기록을 막을 수 없다. 대신 `JumpListScrubber`를 추가했다. 시작할 때, 변경을 감지했을 때, 종료 후에 이 프로필 경로를 담은 맞춤 목록만 삭제한다. 호스트 결과: 남은 목록이 실행마다 1개에서 0개로 줄었다. 일시 기록은 1회, 약 255 ms 안에 삭제됐다. smoke `jumplist`(대상만 삭제, 감시자 동작)와 회귀 시험 PASS. 시스템 디스크의 일시 기록은 남으므로 물리 쓰기 판정은 FAIL을 유지하고 시스템 드라이브 BitLocker에 의존한다. 강제 종료 시에는 다음 시작 때 삭제된다. VM 재추적은 NOT RUN. | [05 점프 목록](05-encrypted-profile.md#최근-항목과-점프-목록-재검증) · [러너](../tests/security/browse-session.mjs) | 개발 작업 |
| 2026-11-24 | Gate D 인터넷 VM 준비 · **WebRTC 보호 결함 수정** | 인터넷 VM용 러너(`tests/vm/internet-batch.mjs`)와 게스트 pktmon 포트 53 캡처 스크립트를 추가했다. 대상 항목: DNS 평문 누출(대조 이름 필수), WebRTC 표준·프록시, SmartScreen 데모 켜기·끄기, 실제 사이트 HTTPS 우선, IPv6. 루프백 사전 점검에서 **프록시 보호 모드가 WebRTC UDP 후보를 수집하는 결함**을 발견했다. 원인은 Chrome 전용 `--force-webrtc-ip-handling-policy`를 런타임이 무시한 것이다. 런타임이 지원하는 `--webrtc-ip-handling-policy`로 바꿔 UDP 후보를 0개로 만들었다. 회귀 시험 `webrtc-guard` 추가(오프라인 VM 일괄 시험에도 포함). 1단계의 인수 존재 검사는 효과를 증명하지 못했음을 기록한다. 인터넷 VM 실행은 NOT RUN. | [ADR-0006](decisions/ADR-0006-network-guard.md) · [WebRTC 시험](../tests/phase5/webrtc-guard.mjs) · [인터넷 러너](../tests/vm/internet-batch.mjs) | 개발 작업 |
| 2026-11-24 | Gate D 인터넷 VM 시험 (`a61a280`) | 오프라인 VM은 그대로 두고, 스냅샷 `01-veil-tools-offline-348fe84`에서 연결 복제 VM(`Veil-VM-INTERNET`)을 만들어 NAT를 연결했다. 포트 전달·공유 폴더·클립보드·드래그 앤 드롭은 끄고, 합성 프로필만 썼다. **PASS**: `dns-path`(Cloudflare DoH 이름은 평문 DNS에 없고 시스템 DNS 대조 이름은 있음), `webrtc-proxy`(STUN 사용 시 후보 0), `smartscreen-demo`(켜기: "안전하지 않다고 신고된 사이트: 탐색이 차단됨" / 끄기: 데모 페이지 열림), `site-compat`(실제 사이트 8/8, HTTP 전용 경고). site-compat는 첫 실행에서 naver 제목을 일찍 읽은 하네스 문제로 FAIL했고, 수정 후 재실행해 PASS. **OBSERVED**: 표준 모드 WebRTC 후보(host mDNS 2, srflx IPv4 1. 표준 보호는 IP를 숨기지 않음), DoH 부트스트랩 평문 조회 없음, IPv6 도달 불가(NAT에 IPv6 없음). **NOT RUN**: 외부 IPv6, 절전·네트워크 전환, Windows SmartScreen 설정 재정의, QUIC 실효, 프록시 모드와 DoH 조합. 시험 후 복제 VM은 종료했다. | 게스트 `C:\VEIL-Inet\src\.veil-dev\internet-vm-l8ySL4`, `C:\VEIL-Inet\src2\.veil-dev\internet-vm-7UdbwE` | 개발 작업 |
| 2026-11-24 | 보안 엔진 1.1 (ADR-0008) | 사용자 결정: Unknown과 Error는 열고 상태를 표시하고(fail-open), 목록은 VEIL 서명 목록만 쓰며 피드는 아직 두지 않는다. 구현 내용: `LocalThreatList`(호스트와 하위 도메인·주소 해시·파일 해시, ECDSA P-256 서명, 만료일과 발급일 확인), 탐색 경고 페이지와 네이티브 계속 막대(페이지 스크립트로 우회 불가, 실행 동안 해당 호스트만 허용), 금고 리다이렉트 단계별 주소 검사, 스트리밍 SHA-256 검사 후 커밋 전 중단, 상태 표시줄과 보안 화면의 목록 상태, 서명 도구. `threat-engine.mjs` 6개 시나리오 PASS: 유효 목록 14개 검사, 목록 없음, 서명자 불일치, 내용 변조, 만료, 서명 없음. Release에는 시험 키 훅과 모의 엔진이 없고 로컬 엔진만 있음을 확인. 출시 서명 키와 피드는 없으므로 Release 검사는 Unknown이다. 하위 프레임과 리소스는 검사하지 않는다. | [ADR-0008](decisions/ADR-0008-local-threat-list.md) · [시험](../tests/security/threat-engine.mjs) | 사용자 결정 · 개발 작업 |
