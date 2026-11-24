# VEIL 라이선스 및 외부 구성 요소

VEIL 프로젝트의 자체 소스코드, 스크립트, 테스트 및 문서는 별도 표시가 없는 한 Mozilla Public License 2.0 (SPDX: `MPL-2.0`)을 적용한다. [LICENSE](../LICENSE)는 영문 라이선스 전문이며, [NOTICE](../NOTICE)는 저장소 단위의 적용 고지다. 이 문서는 이해를 돕기 위한 안내이며 라이선스 원문을 변경하지 않는다.

## 사용 및 배포

- 개인·상업적 사용, 수정 및 재배포가 허용된다.
- 개인적으로 수정하거나 조직 내부에서 사용하는 것만으로는 수정 소스를 외부에 공개할 의무가 생기지 않는다.
- 외부에 MPL 적용 소스 또는 실행 파일을 배포할 때는 해당 MPL 적용 소스(수정된 파일 포함)를 MPL 2.0 조건으로 제공하고, 실행 파일 수령자가 그 소스를 얻는 방법을 안내한다.
- 별도의 파일에 작성된 다른 라이선스의 코드를 결합할 수 있다. 단, MPL 적용 파일과 외부 구성 요소 각각의 조건을 준수해야 한다.
- 기존 저작권·라이선스 고지를 보존한다. MPL은 VEIL의 상표나 로고에 대한 별도 상표권을 허여하지 않으며 보증을 제공하지 않는다.

## 개발 시 준수 사항

신규 자체 소스 파일에는 해당 언어의 주석 문법에 맞춰 `SPDX-License-Identifier: MPL-2.0` 고지를 추가한다. 기존 자체 파일은 저장소 루트의 NOTICE와 LICENSE로 적용 범위를 명시한다. 타인의 코드를 가져올 때는 기존 고지를 제거하거나 임의로 MPL로 변경하지 않는다.

## 외부 구성 요소

아래는 현재 개발 환경에서 사용되는 주요 구성 요소의 확인 대상이다. 이 표는 완전한 제3자 고지 목록이나 배포 권한의 확인 결과가 아니다.

| 구성 요소 | 확인 자료 |
|---|---|
| .NET / WPF | [dotnet/wpf 라이선스](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT), 실제 배포 런타임·패키지 고지 |
| Microsoft Edge WebView2 SDK / Runtime | [Microsoft WebView2 배포 문서](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution), 해당 버전의 Microsoft 라이선스 및 재배포 조건 |
| Rust 의존성 (age, tempfile, zeroize 및 전이 의존성) | [Cargo.toml](../crates/veil-security/Cargo.toml), [Cargo.lock](../crates/veil-security/Cargo.lock), 각 패키지의 라이선스 원문 |
| Qt/PySide6 실험 환경 | 실제 사용·배포하는 패키지의 라이선스와 조건 (현 데스크톱 제품의 필수 엔진으로 간주하지 않음) |

## 첫 공개 릴리즈 전 확인

- 실제 배포물에 들어가는 직접·전이 의존성, 런타임, 자산의 라이선스와 재배포 조건을 확인한다.
- 필요한 제3자 라이선스 전문 및 고지 목록을 배포물에 포함한다.
- 배포 버전에 대응하는 MPL 적용 소스를 제공하고 설치 파일 또는 릴리즈 안내에 소스 취득 경로와 LICENSE를 명시한다.
- 배포 가능한 형태로 소스 접근이 가능한지 확인한다. 현재 저장소 접근 권한이나 미출시 상태만으로 배포 의무 충족을 가정하지 않는다.
- [보안 릴리즈 게이트](11-security-release-gates.md)는 별도로 충족해야 한다.

참고: [MPL 2.0 공식 원문](https://www.mozilla.org/en-US/MPL/2.0/) · [Mozilla 공식 FAQ](https://www.mozilla.org/en-US/MPL/2.0/FAQ/)
