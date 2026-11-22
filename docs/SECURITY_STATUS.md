# VEIL 보안 구현 상태

> **현재 상태 — Phase 1 개발자용 브라우저와 Phase 2 독립 암호화 코어를 실측했다. Phase 3 경로 오류 시험을 통과했다. 실제 보호 볼륨 통합 시험은 VM 준비를 기다리고 있다.**
> `TESTED`는 연결된 시험 범위의 결과다. 전체 보안 검증은 별도로 판정한다.

| ID | 항목 | 현재 상태 | 증거 링크 / 기준 커밋 | 다음 검증 |
|---|---|---|---|---|
| SEC-001 | 브라우저 엔진 선정 | TESTED | [ADR-0001](decisions/ADR-0001-developer-engine.md) | 출시 엔진은 암호화 저장 및 다운로드 경로 실증 후 재평가 |
| SEC-002 | 영구 쿠키·사이트 데이터 복원 | TESTED | [Phase 0 실측](phase0-evidence.md) | 정상 재시작과 WebView2 호스트 강제 종료 후 복원 확인. 트랜잭션 도중 종료·엔진 손상·세션 쿠키 정책 시험 |
| SEC-003 | 잠긴 프로필의 암호화 보호 | BLOCKED | [Phase 0 실측](phase0-evidence.md), [Phase 3 경로 시험](../tests/phase3/profile-routing.mjs) | 지정된 프로필에서 정상 복원 및 3종 오류 시 탐색 중단 확인. BitLocker 볼륨과 오프라인 복사는 VM 준비 후 시험 |
| SEC-004 | 프로필 평문 임시 파일 방지 | BLOCKED | [프로필 마커 실측](phase0-evidence.md) | 승인된 격리 환경에서 Procmon/ETW로 앱·하위 프로세스·TEMP 추적 |
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
| A 저장/키 | BLOCKED | 평문 프로필 마커 검출. 암호화 프로필·잠금·키 관리 미구현 |
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
