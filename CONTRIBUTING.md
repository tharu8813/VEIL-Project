# VEIL 기여 가이드

VEIL은 익숙한 브라우징과 개인정보 보호를 함께 추구한다. 버그 수정, 문서 개선, 접근성, 호환성 및 검증 가능한 보안 개선을 환영한다. 현재 저장소는 비공개이므로 참여 및 브랜치 생성 권한은 유지관리자와 조율한다.

## 시작하기

1. [개발 문서 시작점](docs/00-START-HERE.md), [위협 모델](docs/02-threat-model.md), [보안 릴리즈 게이트](docs/11-security-release-gates.md)를 읽는다.
2. [개발 및 테스트 가이드](docs/DEVELOPMENT.md)에 따라 Windows 11 x64, .NET 10 SDK, WebView2 Runtime, Rust 및 Node.js 22 이상 등 작업에 필요한 도구를 준비한다.
3. 최신 `main`을 기준으로 목적이 명확한 브랜치(`feat/...`, `fix/...`, `docs/...`, `chore/...`)를 만든다.
4. 한 PR에 한 가지 목적을 담고 관련 코드·문서·시험을 함께 수정한다.

## 개발 원칙

- 기존 기능을 유지하고, 암호화·잠금·권한·네트워크 경계의 변경은 기존 요구사항을 검토한다.
- 보안 기능을 약화하거나 비보호 경로로 폴백하여 시험을 통과시키지 않는다. 보안 경계 변경은 유지관리자 승인과 결정 기록(ADR)이 필요하다.
- 실제 로그인 정보나 개인 파일이 아닌 합성 데이터와 격리 VM을 사용한다. 서비스 설치, 관리자 권한, OS 보안 설정 변경 및 파괴적 시험은 사전 승인 없이는 수행하지 않는다.
- 신규 자체 소스 파일에 `SPDX-License-Identifier: MPL-2.0`을 표시한다. 외부 코드의 고지와 라이선스를 보존한다. [라이선스 안내](docs/LICENSING.md)를 참고한다.

## 검증

변경 범위에 맞는 시험을 선택한다. 예를 들어 저장소 루트에서 다음 명령을 사용한다.

```powershell
dotnet restore apps/desktop/Veil.Desktop.csproj --locked-mode
dotnet build apps/desktop/Veil.Desktop.csproj --no-restore
cargo test --locked --manifest-path crates/veil-security/Cargo.toml -- --test-threads=4
```

Rust 보안 변경은 병합 전 기존 전체 보안 시험(`bash scripts/rust-timed-tests.sh`)도 수행한다. 브로커·설치·네트워크 변경은 관련 시험과 [CI 구성](.github/workflows/veil-checks.yml), [시험 전략](docs/10-test-strategy.md)을 따른다. GUI·실제 BitLocker·전원 차단 검증은 적절한 Windows 환경에서 별도 수행한다.

결과는 PASS / FAIL / NOT RUN으로 구분하고 실행 명령과 미실행 이유를 적는다. 문서만 바꾸는 경우 문서 링크 및 형식을 검사하고 런타임 시험 생략 사유를 남긴다.

## 이슈와 PR

- 일반 버그, 호환성 문제, 기능 제안은 [Issues](https://github.com/tharu8813/VEIL-Project/issues)의 양식을 사용한다. 취약점은 [보안 정책](SECURITY.md)을 따른다.
- PR에는 목적, 변경 범위, 관련 이슈, 시험 결과, 보안·개인정보 영향, 남은 위험을 기록한다.
- 관련 [변경 기록](CHANGELOG.md)과 [보안 검증 상태](docs/SECURITY_STATUS.md)를 필요한 경우 갱신한다.
- 검토와 해당 검증을 마친 뒤 유지관리자가 병합한다. 의존성 업데이트도 동일한 절차를 적용한다.

서로를 존중하고 구체적인 근거와 재현 가능한 피드백을 중심으로 협업한다.
