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
```

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
