# VEIL

Windows용 개발자 브라우저 프로토타입입니다. 테스트용 북마크, 방문 기록, 탭과 사이트 데이터가 재실행 후 유지됩니다. 현재 프로필은 일반 디스크에 기록되므로 실제 계정이나 민감한 자료는 사용하지 마세요. 파일 다운로드 요청은 취소되며 Ctrl+J에서 확인할 수 있습니다.

## 빌드 및 실행

Windows 11 x64, .NET 10 SDK, Microsoft Edge WebView2 Evergreen Runtime이 필요합니다. 저장소 루트의 일반 PowerShell에서 실행하세요.

```powershell
dotnet build apps/desktop
dotnet .\apps\desktop\bin\Debug\net10.0-windows\Veil.Desktop.dll
```

SDK는 `Microsoft.Web.WebView2` 1.0.2903.40으로 고정했습니다. NuGet을 사용할 수 없는 이 개발 환경에서는 이미 설치된 Visual Studio의 동일 버전 SDK로 빌드했습니다. 해당 파일이 설치되어 있을 때 다음 명령을 사용할 수 있습니다.

```powershell
$veilSdk = 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/IDE/CommonExtensions/Microsoft/ModelBuilder'
dotnet build apps/desktop "-p:VeilWebView2Sdk=$veilSdk"
dotnet .\apps\desktop\bin\Debug\net10.0-windows\Veil.Desktop.dll
```

프로토타입은 현재 작업 디렉터리의 `.veil-dev/desktop`을 사용합니다. 엔진 데이터는 그 아래 `webview2/`에, 북마크·기록·열린 탭은 `browser-state.json`에 저장합니다. 테스트용 프로필 하나는 동시에 한 VEIL 프로세스에서만 열 수 있습니다. 잘못된 상태 파일을 읽으면 프로필 열기를 중단합니다.

### VS Code에서 실행

저장소 루트 폴더를 VS Code로 열고 권장 확장(C# Dev Kit)을 설치합니다. **F5**(디버그) 또는 **Ctrl+F5**(디버그 없이)를 누르면 `build: desktop` 작업으로 Debug 빌드한 뒤 `VEIL 브라우저 (개발 프로필)` 구성으로 앱을 실행합니다. 작업 디렉터리는 저장소 루트이므로 위와 같은 `.veil-dev/desktop` 프로필을 사용합니다. **Ctrl+Shift+B**는 빌드만 실행하고, `작업 실행` 메뉴의 `test: …` 작업으로 데스크톱 통합·강제 종료·프로필 경로 시험을 실행할 수 있습니다. VS Code를 관리자 권한으로 실행하지 마세요.

### 단축키와 탭 조작

Chrome·Edge와 같은 단축키를 사용합니다.

| 동작 | 키 |
|---|---|
| 새 탭 / 새 창 / 탭 닫기 / 창 닫기 | Ctrl+T / Ctrl+N / Ctrl+W·Ctrl+F4 / Ctrl+Shift+W |
| 닫은 탭 다시 열기 | Ctrl+Shift+T |
| 다음·이전 탭 | Ctrl+Tab·Ctrl+PgDn / Ctrl+Shift+Tab·Ctrl+PgUp |
| 1~8번째 탭 / 마지막 탭 | Ctrl+1~8 / Ctrl+9 |
| 탭 순서 이동 | Ctrl+Shift+PgUp / Ctrl+Shift+PgDn |
| 주소창 / 검색 | Ctrl+L·Alt+D·F6 / Ctrl+K·Ctrl+E |
| 주소 자동 완성 / 새 탭에서 열기 | Ctrl+Enter(www.…com) / Alt+Enter |
| 뒤로·앞으로 / 홈 | Alt+←·Alt+→·마우스 뒤로/앞으로 버튼 / Alt+Home |
| 새로고침 / 캐시 무시 새로고침 / 중지 | F5·Ctrl+R / Ctrl+F5·Shift+F5·Ctrl+Shift+R / Esc |
| 페이지 내 검색 / 다음·이전 결과 | Ctrl+F / F3·Ctrl+G / Shift+F3·Ctrl+Shift+G |
| 확대·축소·초기화 | Ctrl++ / Ctrl+- / Ctrl+0 |
| 북마크 추가 / 북마크 / 기록 / 다운로드 | Ctrl+D / Ctrl+B·Ctrl+Shift+O / Ctrl+H / Ctrl+J |
| 전체 화면 / 메뉴 | F11 / Alt+F·Alt+E |

- 탭을 가운데 버튼으로 클릭하면 닫힙니다. 오른쪽 클릭 메뉴에서 새로고침, 복제, 음소거, 새 창으로 이동, 다른 탭·오른쪽 탭 닫기를 사용할 수 있습니다.
- 탭을 좌우로 끌면 순서가 바뀝니다. 탭 줄 위아래로 끌어내면 새 창이 되고, 그 창을 다른 VEIL 창의 탭 줄에 놓으면 합쳐집니다. 페이지는 다시 불러오지 않습니다.
- 인쇄·PDF 저장(Ctrl+P), 페이지 저장(Ctrl+S), 소스 보기(Ctrl+U), InPrivate 창(Ctrl+Shift+N)은 다운로드 금고와 보호 경로 검증 전까지 비활성화되어 있으며 상태 표시줄에 이유를 표시합니다.

## 로컬 사이트에서 시험

Node.js 22 이상에서 아래 명령을 실행하고 VEIL 주소창에 `http://127.0.0.1:8765`를 입력합니다.

```powershell
node tests/integration/fixture.mjs 8765
```

페이지의 **사이트 데이터 저장**으로 합성 로그인 및 사이트 저장소를 생성합니다. Ctrl+D로 북마크를 추가한 뒤 앱을 닫았다 다시 열어 Ctrl+B와 Ctrl+H에서 확인할 수 있습니다. 쿠키는 로그인 fixture에서 만료가 명시된 영구 쿠키를 검증했습니다. 세션 쿠키와 충돌 복구는 별도 검증 대상입니다.

웹 주소를 직접 입력하거나 주소창에서 DuckDuckGo로 검색할 수 있습니다. 일반 주소는 HTTPS로 시작하고 로컬 fixture 주소는 HTTP를 사용합니다. 사이트 팝업은 탭으로 열립니다. 프로토타입에서 카메라·마이크 등 사이트 권한 요청은 거부합니다.

## 자동 시험

Debug 빌드 후 저장소 루트에서 실행합니다. Windows 앱 실행을 제한하는 도구 환경에서는 일반 사용자 PowerShell을 사용합니다. 관리자 권한은 필요하지 않습니다.

```powershell
node tests/phase0/run.mjs
node tests/phase0/proxy.mjs
node tests/phase0/proxy.mjs --ipv6
node tests/phase0/browser-crash.mjs
node tests/integration/desktop.mjs
node tests/integration/desktop.mjs --crash-cycle
node tests/phase3/profile-routing.mjs
node tests/phase3/engine-kill.mjs --cycles 5
node tests/phase4/vault-capture.mjs
node tests/phase4/vault-persist.mjs
node tests/phase5/network-guard.mjs
node tests/phase5/dns-protection.mjs
node tests/security/smartscreen.mjs
node tests/vm/make-bundle.mjs   # 단일 오프라인 VM 일괄 시험 번들 (docs/10-test-strategy.md)
node --test tests/security/outside-writes.test.mjs
node tests/security/browse-session.mjs --runs 3
```

보호 볼륨 밖 쓰기 추적(SEC-004)은 격리 VM에서만 실행합니다. 관리자 창에서 `tests/security/trace-writes.ps1 -Action Start -IsolatedVm`으로 Procmon 수집을 시작하고, 일반 사용자 창에서 `node tests/integration/desktop.mjs --profile-parent 'V:\VEIL-Lab' --crash-cycle`을 실행한 뒤 관리자 창에서 `-Action Stop -IsolatedVm`으로 분석합니다. 하네스 증거 폴더(`.veil-dev`) 외의 볼륨 밖 쓰기가 하나라도 있으면 실패합니다.

Qt까지 비교할 때는 PySide6 6.11.1이 설치된 Python 3.14 환경의 실행 파일을 `VEIL_QT_PYTHON`에 지정합니다. 변수 없이 실행하면 Qt는 `NOT RUN`으로 기록합니다.

```powershell
$env:VEIL_QT_PYTHON = 'C:/path/to/qt-environment/Scripts/python.exe'
node tests/phase0/run.mjs
```

각 실행은 `.veil-dev/` 아래에 새 합성 프로필을 만들고 증거 파일 위치를 출력합니다. 범위와 결과는 [Phase 0 증거](docs/phase0-evidence.md)와 [보안 상태](docs/SECURITY_STATUS.md)를 참고하세요.

## 독립 암호화 코어

Rust 1.99.1 MSVC에서 브라우저와 분리된 age 스트리밍, 암호문 변조 감지, 중단 후 파일 발행을 시험합니다. 테스트는 합성 데이터만 사용합니다. [Phase 2 증거](docs/phase2-evidence.md)

```powershell
cargo test --release --locked --manifest-path crates/veil-security/Cargo.toml
```

다운로드 금고(Phase 4-0)는 `veil-vault.exe`가 필요합니다. 데스크톱 앱보다 먼저 `cargo build --release --locked --manifest-path crates/veil-security/Cargo.toml --bin veil-vault`로 빌드하면 앱 출력 폴더에 복사됩니다. 없으면 모든 다운로드가 차단됩니다(평문 저장 없음).

## Phase 3 보호 프로필 시작 (개발 중)

제품용 구성 파일 `%ProgramData%\\VEIL\\client.json`이 있을 때 일반 사용자 VEIL은 등록된
브로커에 연결해 보호 볼륨의 암호화·보호 상태를 확인합니다. 볼륨이 분리되어 있으면 연결을
요청하고, 잠겨 있는 볼륨에서 비밀번호를 입력받아 해제한 후에만 WebView2 프로필을 엽니다.
이미 열린 볼륨은 이 실행에서 비밀번호를 검증한 것으로 간주하지 않고 시작을 거부합니다.
브라우저 종료 시 잠금 요청 후 이번 실행이 연결한 볼륨만 분리합니다. 강제 종료 시에는 브로커의
클라이언트 종료 감시가 잠금을 재시도합니다. 실패하면 비보호 프로필로 전환하지 않습니다.

**아직 완성된 보안 기능이 아닙니다.** 실제 BitLocker 백엔드가 포함된 격리 VM에서 시작·종료,
중단·열린 핸들, 자동 잠금, 로그·메모리, 설치/업데이트를 검증해야 합니다. 브로커는 현재
Windows 서비스 설치까지 통합되지 않았습니다. 비밀번호는 전송 버퍼에서 지우지만 OS/프로세스
메모리의 모든 복제본 제거를 보장하지 않습니다.

Release 빌드는 제품용 보호 설정이 없으면 시작하지 않으며, Debug 빌드만 이전의 비보호
`.veil-dev` 테스트 프로필을 계속 사용합니다.

Windows 일반 사용자 PowerShell에서 가짜 BitLocker 백엔드 회귀 시험:

```powershell
dotnet build apps/desktop
dotnet build apps/broker/Veil.VolumeBroker
node tests/broker/broker.mjs
```

이 시험은 실제 BitLocker를 제어하지 않습니다. 제품 설치·볼륨 설정을 임의로 진행하지 마세요.
