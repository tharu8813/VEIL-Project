# VEIL 보안 구현 상태

> **현재 상태 — 오프라인 VM에서 실제 BitLocker로 일반 사용자 Release 브라우저의 정상 종료·강제 종료·Windows 세션 잠금 후 재잠금과 열린 파일 복구를 확인했다. 절전은 모의 알림을 검증했다. 실제 전원 전환, 설치형 서비스, 출시 게이트 검증은 남아 있다.**
> `TESTED`는 연결된 시험 범위의 결과다. 전체 보안 검증은 별도로 판정한다.

| ID | 항목 | 현재 상태 | 증거 링크 / 기준 커밋 | 다음 검증 |
|---|---|---|---|---|
| SEC-001 | 브라우저 엔진 선정 | TESTED | [ADR-0001](decisions/ADR-0001-developer-engine.md) | 출시 엔진은 암호화 저장 및 다운로드 경로 실증 후 재평가 |
| SEC-002 | 영구 쿠키·사이트 데이터 복원 | TESTED | [Phase 0 실측](phase0-evidence.md) | 정상 재시작과 WebView2 호스트 강제 종료 후 복원 확인. 트랜잭션 도중 종료·엔진 손상·세션 쿠키 정책 시험 |
| SEC-003 | 잠긴 프로필의 암호화 보호 | IN PROGRESS | [BitLocker VHDX 잠금과 복원 실측](05-encrypted-profile.md#2026-11-23-bitlocker-vhdx-실측), [전체 데스크톱 잠금 시험](05-encrypted-profile.md#전체-데스크톱-상태의-잠금-시험), [ADR-0003](decisions/ADR-0003-volume-broker.md) | 제품 실행 경로에서 보호 볼륨 식별, 자동 잠금 및 강제 종료 복구 |
| SEC-004 | 프로필 평문 임시 파일 방지 | IN PROGRESS | [Procmon 쓰기 추적](05-encrypted-profile.md#2026-11-23-bitlocker-vhdx-실측) | 전체 앱의 TEMP·덤프·캐시 및 충돌 경로, 매핑된 쓰기와 OS 기록 검증 |
| SEC-005 | Rust 인증 암호화 코어 | TESTED | [Phase 2 실측](phase2-evidence.md) | 독립 코어의 왕복, 변조·잘림, 64 MiB 스트리밍, 강제 종료 시험 통과. 의존성 보안 감사 및 앱 통합 |
| SEC-006 | 마스터 키·잠금·백업/복구 | IN PROGRESS | [ADR-0002](decisions/ADR-0002-phase2-age.md), [Phase 2 실측](phase2-evidence.md) | 키 봉투와 객체 사본 복원 확인. 브라우저 잠금, 키 회전, 백업 정책, 메모리/OS 노출 검증 |
| SEC-007 | 다운로드 스트리밍 보호 | BLOCKED | [Phase 0 실측](phase0-evidence.md), [앱 다운로드 취소 시험](../tests/integration/desktop.mjs) | 시험 옵션 적용 시 단일 GET의 메모리 본문 해시 일치. 유형별 본문, 보호 스트리밍, 쓰기 경로 검증 |
| SEC-008 | 다운로드 금고·내보내기 | NOT STARTED | [취소 목록 구현](../apps/desktop/BrowserWindow.xaml.cs) | 인증 저장, 내보내기·충돌·중단 검증 |
| SEC-009 | 표준 네트워크 보호 | NOT STARTED | 없음 | HTTPS/추적 정책 동작 |
| SEC-010 | 프록시 우회/누출 차단 | BLOCKED | [IPv4·IPv6 루프백 HTTP 정상·장애 시험](phase0-evidence.md) | HTTPS CONNECT, DNS/WebRTC/외부 IPv6/QUIC, 절전·재개, OS 경로 격리 검증 |
| SEC-011 | 일반 브라우저 UI/기능 | TESTED | [데스크톱 통합 시험](../tests/integration/desktop.mjs) | 실제 키 입력·IME·접근성·DPI 및 주요 사이트 회귀 |
| SEC-012 | 엔진 보안 업데이트 및 배포 | IN PROGRESS | [ADR-0001](decisions/ADR-0001-developer-engine.md) | 패키지 복원, 서명, 라이선스, 설치·업데이트 실증 |

## 미해결 핵심 위험

1. 두 기본 엔진 프로필과 앱 상태는 일반 디스크에 평문 데이터를 기록한다. 전체 쓰기 경로와 보호 백엔드는 미검증이다.
2. 두 엔진에서 GET 취소를 관찰했다. WebView2 최종 앱에서 여덟 가지 다운로드 요청을 취소했다. 일시적 디스크 쓰기와 스트리밍 금고는 미검증이다.
3. 잠금 해제 중 동일 사용자 악성코드에 대한 접근 제한 및 OS 페이지 파일/덤프의 노출 한계가 존재한다.
4. 로컬 프록시가 중단된 상황의 IPv4·IPv6 루프백 HTTP 요청을 관찰했다. 그 외 경로의 우회·누출 방지는 미검증이며 앱에 보호 모드를 노출하지 않는다.
5. 별도 렌더러 프로세스가 관찰됐다. 샌드박스 적용 범위와 장기 엔진 패치·배포 체계는 미검증이다.
6. 손상된 앱 상태 JSON은 시작에서 차단한다. WebView2 호스트 종료 직후의 복원과 암호화 코어 쓰기 도중 강제 종료를 각자 실측했다. 엔진 저장 트랜잭션 도중 종료, 전원 차단, 디스크 부족, 엔진의 상태 손상 복구는 미검증이다.
7. 독립 코어는 브라우저 프로필을 감싸지 않는다. 암호화된 파일을 발행할 때 상위 저장소의 파일명·백업 세대와 객체 교체 공격에 대한 인증은 별도 검증이 필요하다.

## 릴리스 판정

**개발자용 PoC만 허용. 정식 보안 브라우저 또는 실사용용 베타 배포 불가.**

| 게이트 | 판정 | 근거 |
|---|---|---|
| A 저장/키 | BLOCKED | 개발 프로필 평문 마커 검출. 정상·강제 종료 및 Windows 세션 잠금 뒤 재잠금 확인. 실제 절전과 제품 경로의 볼륨 밖 쓰기 검증 필요 |
| B 지속성/복구 | 일부 시험 PASS, 게이트 미통과 | 정상 재실행과 합성 WebView2 호스트 강제 종료 후 복원에 성공했다. 트랜잭션 도중 종료 및 복구 정책 미검증 |
| C 다운로드 | BLOCKED | 여덟 유형 취소 확인. 원본 쓰기 추적·금고·내보내기 미검증 |
| D 보호 네트워크 | BLOCKED | 단일 로컬 프록시 장애 시나리오만 PASS |
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
