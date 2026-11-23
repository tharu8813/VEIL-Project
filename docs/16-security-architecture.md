# 16. 보안 관리자와 위협 보호 기반 (VEIL 1.0)

- 관련: [ADR-0007](decisions/ADR-0007-security-manager.md), [ADR-0006 네트워크 보호](decisions/ADR-0006-network-guard.md), SEC-013
- 코드: `apps/desktop/SecurityManager.cs`, `SecuritySettingsDialog.cs`, `BrowserWindow.Security.cs`
- 시험: `node tests/security/smartscreen.mjs` (Debug 빌드, openssl 필요)

## 1. 보안 관리자(Security Manager)의 역할

프로필 하나에 하나씩 있고 `BrowserSession`이 만든다. 모든 창과 탭이 같은 WebView2 엔진과 사용자 데이터 폴더를 공유하므로 보안 정책도 프로필 단위로 관리한다.

- 사용자 보안 정책을 갖고 있다. 1.0에는 Microsoft SmartScreen 켜기·끄기만 있다.
- 시작할 때 저장된 정책을 복원하고, 새로 만드는 WebView마다 첫 탐색 전에 적용한다. 새 탭, 새 창, 다른 창으로 옮긴 탭 모두 해당한다.
- 적용한 뒤 값을 다시 읽어 확인한다. 값이 거부되거나 남지 않으면 `적용 실패`로 표시한다.
- `저장된 설정`(사용자가 고른 값)과 `적용 상태`(대기 / 적용됨 / 적용 실패)를 따로 보고한다.
- 앞으로 추가할 VEIL 위협 엔진을 등록하는 곳(`ThreatProtection`)이다.
- 정책을 이유로 외부와 통신하지 않고, 주소를 기록하지 않는다.

## 2. SmartScreen과 VEIL 엔진의 차이

| | Microsoft SmartScreen | VEIL 위협 엔진 |
|---|---|---|
| 제공 | WebView2 런타임 내장(Microsoft) | VEIL 자체 확장 지점 |
| 1.0 상태 | 기본 켜짐, 사용자가 끌 수 있음 | 엔진 없음. 프로덕션 등록 차단 |
| 판정 위치 | 엔진 내부(경고 페이지·다운로드 차단) | 로컬 실행만 허용 |
| 외부 통신 | 방문 주소·다운로드 정보를 Microsoft로 전송 | 없음(설계 요구 사항) |
| 결과 | VEIL은 받지 않음 | Malicious / NotDetected / Unknown / Error |

SmartScreen의 판정은 VEIL 결과로 바꾸지 않는다. VEIL 엔진이 NotDetected라고 해도 SmartScreen을 끄거나 우회하지 않는다. 두 기능은 독립적이다.

## 3. 저장과 적용 흐름

1. **저장**: 기존 프로필 상태 파일 `browser-state.json`의 `Security` 항목(`{"SmartScreen": false}`)에 둔다. 기본값(켜짐)일 때는 `null`이라 항목이 생기지 않는다. 쓰기는 기존 원자적 교체(`.new` 후 이름 바꾸기)를 그대로 쓴다.
2. **시작**:
   - `BrowserData.Load`가 값을 읽는다.
   - 값이 없거나 `null`이면 기본값(켜짐)이다.
   - 값이 손상되면(잘못된 형식이나 자료형) **켜짐으로 복원**하고 `복원됨`으로 표시한다. 꺼짐으로 해석하는 일은 없다.
   - 모르는 필드(새 버전 설정)는 무시한다.
   - 상태 파일 전체가 손상되면 기존 정책대로 시작을 거부하고 파일을 건드리지 않는다.
3. **WebView 생성**(`OpenTab`): `EnsureCoreWebView2Async` 직후, 첫 탐색 전에 `Settings.IsReputationCheckingRequired`를 설정한다.
   - 순서가 중요하다. API 문서에 따르면 값을 설정하지 않은 새 WebView가 탐색하면 기본값(켜짐)이 같은 사용자 데이터 폴더의 모든 WebView에 다시 적용된다.
4. **변경**(메뉴 → 보안):
   1. 먼저 상태 파일에 저장한다.
   2. 저장에 실패하면 메모리와 적용 상태를 바꾸지 않고 오류를 표시한다.
   3. 저장에 성공하면 열린 모든 창의 모든 WebView에 새 값을 설정하고 다시 읽어 확인한다.
   4. 엔진은 다음 탐색이나 다운로드부터 새 값을 쓴다. 다시 시작할 필요는 없다.
5. **표시**:
   - 상태 표시줄에는 SmartScreen이 꺼졌거나, 적용에 실패했거나, 보안 설정이 복원된 경우에만 표시를 덧붙인다.
   - 보안 화면에는 현재 상태 문장을 보여 준다.
   - "적용됨"은 VEIL이 값을 설정하고 확인했다는 뜻이다. Windows 설정이 SmartScreen을 끈 경우까지 보장하지 않으므로 켜짐 상태 문장에 그 한계를 함께 적는다.

## 4. 인터페이스 명세

```csharp
enum ThreatVerdict { Malicious, NotDetected, Unknown, Error }
record ThreatResult(ThreatVerdict Verdict, string Engine, string Reason); // 주소를 담지 않음

interface IThreatEngine
{
    string Name { get; }
    bool ProductionReady { get; }            // 시험용 엔진은 false
    ValueTask<ThreatResult> CheckAsync(Uri address, CancellationToken cancel);
}

class ThreatProtection
{
    static bool ProductionEnginesEnabled => false;  // 1.0: 프로덕션 엔진 없음
    bool Register(IThreatEngine engine);            // Release: 위 값이 false이면 항상 거부
    Task<ThreatResult> CheckAsync(Uri address, TimeSpan timeout, CancellationToken cancel = default);
}
```

- **Unknown**: 엔진이 정상 동작했지만 판단할 정보가 없다. 등록된 엔진이 없을 때도 Unknown이다(안전하다는 뜻이 아님).
- **Error**: 엔진이 실패했다(예외나 시간 초과). Unknown과 구분한다.
- **결합 규칙**: 하나라도 Malicious면 Malicious다. 아니면 하나라도 Error면 Error다. 아니면 하나라도 NotDetected면 NotDetected다. 나머지는 Unknown이다.
- **MockThreatEngine**:
  - `#if DEBUG`로 Release에는 포함되지 않는다. Release DLL에 타입 이름이 없음을 확인했다.
  - 고정 표의 호스트만 판정하고, 나머지는 Unknown이다. 모든 주소를 안전하다고 하지 않는다.
- 엔진 결과에는 주소, 쿼리, 토큰을 넣지 않는다. 시험으로 확인한다.

## 5. 1.0에서 구현한 것

- 보안 관리자: 정책 저장과 복원, 모든 WebView 적용과 다시 읽기 확인, 저장된 설정과 적용 상태 구분, 저장 실패와 적용 실패 표시.
- Microsoft SmartScreen 설정: 기본 켜짐, 사용자가 끌 수 있음, 프로필별 저장, 재시작 후 복원, 재시작 없이 열린 모든 탭과 창에 적용.
- 보안 화면: "Microsoft SmartScreen 사용" 토글, Microsoft로 전송되는 정보 안내, 끌 때 위험 안내, Windows와 엔진 한계 안내. VEIL 엔진은 화면에 표시하지 않는다.
- 위협 보호 인터페이스, 결합 규칙, 시간 초과는 Error, Debug 전용 시험 엔진.

## 6. 1.0에서 구현하지 않은 것

- VEIL 자체 탐지, 평판 데이터베이스, 차단 목록 피드, 외부 API.
- 탐색이나 다운로드 경로에서 위협 엔진 호출. 인터페이스만 있고 연결하지 않았다.
- SmartScreen 판정 결과 수신과 기록(WebView2에 해당 API 없음).
- Windows 정책(“Microsoft Edge용 SmartScreen”, 그룹 정책)으로 꺼진 상태 감지.
- Windows Defender나 OS 전체 SmartScreen 변경(하지 않음).
- WebView2 엔진의 다른 Microsoft 연결 차단. 예를 들어 `config.edge.skype.com`은 SmartScreen을 꺼도 남을 수 있다.

## 7. 1.1 계획

- 로컬 엔진 1차: 탐색 시작 시 호출하고, Malicious면 경고 페이지와 사용자 선택을 보여 준다. Unknown과 Error는 통과시키고 표시만 한다(정책 결정 필요).
- 서명된 로컬 차단 목록과 갱신(갱신 채널의 개인정보 설계 포함, 주소 원문 전송 금지).
- 다운로드 금고 단계에 파일 검사 연결(봉인 전 해시 기반).
- Windows SmartScreen 정책 상태 감지(가능한 공식 경로가 있을 때만).
- 엔진별 시간 제한, 병렬 실행, 성능 예산.

## 8. 시험 결과와 미검증 항목

2026-11-23, 호스트 Windows 11 Home, WebView2 런타임 157.0.4341.60, SDK 1.0.2903.40, Debug 빌드.

| 항목 | 결과 |
|---|---|
| 기본값(새 프로필, 값 없는 기존 프로필) 켜짐, 다른 설정 보존 | PASS |
| 변경(꺼짐) 후 모든 WebView(탭 3개, 두 번째 창 포함) 반영 및 상태 파일 저장 | PASS |
| 재시작 복원(꺼짐 → 켜짐 → 켜짐) | PASS |
| 프로필별 값, 두 인스턴스 동시 실행(꺼짐/켜짐) | PASS |
| 손상 값 3종 → 켜짐으로 복원·표시, 모르는 필드 무시, 상태 파일 전체 손상 시 시작 거부·파일 보존 | PASS |
| 저장 실패 시 저장된 설정·적용 상태 불변 | PASS (실패를 주입한 단위 확인) |
| API 지원: 설정 후 다시 읽기 일치 | PASS |
| 시험 엔진: 등록, 4가지 결과, 미등록 호스트 Unknown, 예외 Error, 시간 초과 Error, 결합 규칙, 결과에 주소 없음, 판정 후 SmartScreen 값 불변 | PASS |
| 프로덕션: 기본 등록 엔진 0개, `ProductionEnginesEnabled=false`, Release DLL에 시험 엔진 없음 | PASS |
| 엔진 동작 관찰: 로컬 DoH 응답기로 이름 조회 관찰. 켜짐 실행은 `nav-edge.smartscreen.microsoft.com`을 조회, 꺼짐 실행은 조회 0건 | PASS (응답은 NXDOMAIN이라 실제 Microsoft 연결 없음) |
| 회귀 | 아래 기록 참고 |

미검증(NOT RUN):

- 실제 SmartScreen 경고 페이지(인터넷 필요, Microsoft 시험 페이지). 인터넷 VM에서 실행한다.
- Windows “Microsoft Edge용 SmartScreen” 설정과 그룹 정책이 끈 경우의 동작(VM 전용, 호스트 설정 변경 금지).
- 위험한 다운로드에 대한 SmartScreen 판정(금고는 엔진 다운로드를 취소하고 다시 받으므로 엔진 SmartScreen 다운로드 검사가 실행되지 않을 수 있다). 인터넷 VM에서 확인한다.
- 실제 UI 클릭으로 하는 토글 조작(자동 시험은 같은 코드 경로를 직접 호출). 화면 수동 확인이 필요하다.

## 9. 외부 통신과 개인정보 한계

- SmartScreen이 켜져 있으면 WebView2 엔진이 방문 주소와 다운로드 정보를 Microsoft SmartScreen 서비스로 보낸다. VEIL은 이 통신 내용을 바꾸거나 보거나 기록하지 않는다.
- SmartScreen을 끄면 이 SmartScreen 조회는 멈춘다(로컬 관찰). 엔진의 다른 Microsoft 연결이 모두 멈추는 것은 아니다.
- Windows 설정이 SmartScreen을 끄면 VEIL에서 켜도 동작하지 않는다. VEIL은 이를 감지하지 못하므로 "켜짐"은 VEIL이 요청을 적용했다는 뜻이다.
- VEIL 위협 보호 계층은 외부로 아무것도 보내지 않는다. 결과와 상태 문장에는 주소, 쿼리, 토큰이 들어가지 않는다. 적용 실패 진단은 예외 형식 이름만 남긴다.
