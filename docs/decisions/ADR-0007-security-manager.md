# ADR-0007 보안 관리자, SmartScreen 설정, 위협 보호 인터페이스

- 상태: 채택(1.0 기반 구현)
- 작성일: 2026-11-23
- 관련: [16 보안 관리자](../16-security-architecture.md), [ADR-0006](ADR-0006-network-guard.md), SEC-013

## 배경

ADR-0006 DNS 실측에서 엔진이 `nav-edge.smartscreen.microsoft.com`을 조회하는 것을 확인했다. SmartScreen은 방문 주소를 Microsoft로 보내므로 보안과 개인정보 사이의 절충이 필요했다. 또한 앞으로 VEIL 자체 보호 엔진을 붙일 자리가 필요했다.

## 결정

- 프로필 단위 **보안 관리자**를 둔다. 정책은 기존 상태 파일의 `Security`에 저장한다.
- **SmartScreen 기본값은 켜짐**이고 사용자가 끌 수 있다. WebView2 공식 API `CoreWebView2Settings.IsReputationCheckingRequired`를 모든 WebView에 첫 탐색 전에 같은 값으로 설정한다. 이 값은 사용자 데이터 폴더 단위로 동작하며, 하나라도 켜져 있으면 켜진다. 탭별 설정은 하지 않는다.
- 변경은 저장 후 열린 모든 WebView에 적용하고 다시 읽어 확인한다. 다시 시작할 필요가 없다.
- 저장된 설정과 적용 상태를 구분해 표시한다. Windows 설정으로 꺼진 경우는 감지할 수 없으므로 켜짐을 보장한다고 표시하지 않는다. OS 전체 Defender와 SmartScreen은 건드리지 않는다.
- 손상된 보안 값은 켜짐으로 복원한다. 상태 파일 전체가 손상되면 기존대로 시작을 거부한다.
- **위협 보호 인터페이스**(Malicious, NotDetected, Unknown, Error)를 정의한다.
  - 1.0에는 프로덕션 엔진이 없고, Release는 엔진 등록을 거부한다.
  - 시험 엔진은 Debug 전용이다.
  - SmartScreen 결과를 VEIL 판정으로 바꾸지 않는다.

## 결과

- 로컬 시험(`tests/security/smartscreen.mjs`)에서 저장, 복원, 전 화면 적용, 프로필별 동작, 손상 값 처리, 인터페이스를 확인했다. 로컬 DoH 관찰로 끈 상태에서는 SmartScreen 조회가 없음을 확인했다.
- 실제 경고 페이지, Windows 정책 재정의, 다운로드 판정은 인터넷 VM에서 확인할 항목이다(NOT RUN).
