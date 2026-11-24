# VEIL 브랜드 디자인 가이드

<p align="center">
  <img src="assets/veil-logo.svg" alt="VEIL 로고" width="112" height="112">
</p>

<p align="center"><strong>Browse freely. Stay private.</strong></p>

## 브랜드 이름과 메시지

- 제품 표기: **VEIL** (항상 대문자). 저장소 이름은 VEIL-Project.
- 슬로건: **Browse freely. Stay private.** 대소문자와 마침표를 유지한다.
- 핵심 가치: 일상적인 브라우저 사용 흐름을 유지하면서 개인정보 보호를 기본 설계에 포함한다.
- 문체: 간결하고 차분한 설명. 공포 마케팅과 절대적인 보안 보장 표현은 피한다.

슬로건은 브랜드 방향이며 현재 보안 검증 완료를 의미하지 않는다. 공개 자료에는 개발 중 상태와 실제 보호 한계를 명시한다. VPN·완전 익명·모든 위협 차단을 보장하지 않는다.

## 로고

**Soft Veil**은 세 겹의 반투명 베일을 보라색 계열로 표현한 공식 앱 아이콘이다. [SVG 원본](assets/veil-logo.svg), [브라우저 XAML 로고](../apps/desktop/BrowserWindow.xaml), [Windows ICO](../apps/desktop/Assets/veil.ico)는 같은 형태와 색상을 사용한다. 로고 전용 팔레트는 배경 `#251C42` → `#141126`, 첫 베일 `#DFC5FF` → `#886DFF`, 중간 베일 `#A695FF` (62%), 마지막 베일 `#EFE8FF` (87%)이다. 아래의 기존 UI 팔레트는 그대로 유지한다.

- 원본 비율 1:1을 유지하고 그림을 늘이거나 회전·반전하지 않는다.
- 배경은 Chrome 색상 또는 충분한 대비가 있는 어두운 표면을 사용한다.
- 주변 여백은 로고 너비의 약 1/8 이상을 권장한다.
- README 및 문서에는 SVG를 우선 사용한다. Windows 실행 파일에는 16, 24, 32, 48, 64, 128, 256px ICO를 내장한다. 소셜 미리보기 이미지는 별도 제작 전에는 존재한다고 표시하지 않는다.

## 색상

다음 값은 현재 [Theme.xaml](../apps/desktop/Theme.xaml)의 실제 팔레트다.

| 색상 | HEX | 용도 |
| --- | --- | --- |
| Violet | `#8B7CFF` | 브랜드 강조, 그라데이션 시작 |
| Cyan | `#4FD1E8` | 그라데이션 끝, 강조 |
| Ink | `#0A0C13` | 기본 어두운 표면 |
| Chrome | `#10121B` | 브라우저 크롬, 로고 배경 |
| Surface | `#171A26` | 일반 패널 |
| Raised | `#1E2231` | 상승 표면 |
| Line | `#262A3B` | 경계선 |
| Text | `#E9ECF5` | 주 텍스트 |
| Muted | `#8B91A7` | 보조 텍스트 |
| Amber | `#F2B84B` | 주의·경고 강조 |

보라색과 청록색은 좌에서 우로 연결한다. 위험 상태는 색만으로 전달하지 않고 텍스트와 아이콘을 함께 사용한다. 실제 UI 변경 시 가독성·명암 대비·키보드 탐색을 별도 확인한다.

## 타이포그래피와 UI

현재 Windows UI는 Segoe UI Variable Text, Segoe UI, Malgun Gothic을 사용하고 아이콘은 Segoe Fluent Icons, Segoe MDL2 Assets를 사용한다. 문서와 웹에서는 시스템 글꼴을 우선 사용한다. UI 구조는 사용자가 이미 알고 있는 브라우저 관례를 존중하고 장식적 애니메이션은 기능을 방해하지 않도록 한다.

## 적용과 관리

- README 로고, 제품 UI, 설치 프로그램, 릴리즈 자료, 소셜 미리보기의 명칭과 색상을 일관되게 유지한다.
- 제품 변경 없이 브랜드 가이드만 수정하는 경우 UI가 이미 바뀌었다고 주장하지 않는다.
- MPL 2.0은 별도의 상표권을 허여하지 않는다. [라이선스 안내](LICENSING.md)를 참조한다.
