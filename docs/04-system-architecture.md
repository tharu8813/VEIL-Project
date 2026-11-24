# 04. 시스템 아키텍처와 모듈 인터페이스

## 설계 원칙

브라우저 UI, 렌더링 엔진, 암호화 코어, 프로필 저장 계층, 다운로드 금고, 네트워크 정책을 분리한다. 기술 선택은 `03-engine-selection.md` PoC 이후 확정한다.

```text
[VEIL Desktop / UI]
      |  일반 브라우저 상호작용
      v
[Browser Core] <--> [Profile Storage Adapter] <--> [Encrypted Profile Backend]
      |                       |                         |
      |                       +----[Key Service]---------+
      +--> [Download Interception/Adapter] --> [Download Vault]
      |
      +--> [Network Policy Adapter] --> [Network Guard / OS enforcement]

[Security Core / Rust 후보]
  Key Service, AEAD, authenticated streaming, manifest integrity, backup
```

그림은 **논리 아키텍처**다. 실제 브라우저 네트워크 스택을 Rust 모듈이 자동으로 통과하는 것은 아니다. 브라우저 버전과 OS 제약에 맞는 정책 강제 경로를 PoC로 확인한다.

## 책임 분리

| 모듈 | 책임 | 금지 사항 |
|---|---|---|
| Desktop | 화면·메뉴·탭·설정·사용자 확인 | UI에서 암호화 키/원시 파일 데이터를 직접 소유 |
| Browser Core | 웹 표준, 렌더러, 쿠키/세션/권한 | 샌드박스 비활성화를 상시 사용 |
| Profile Storage | 암호화 저장 경로·복원·무결성 | 보호 실패 시 평문 폴더로 폴백 |
| Security Core | 키 수명·암복호화·인증·백업 | 키 하드코딩, 자체 암호 알고리즘 |
| Download Vault | 다운로드 파일·메타데이터·상태·내보내기 | 다운로드 후 평문 파일을 소급 암호화 |
| Network Guard | 모드별 연결 정책·경로 검증·차단 | 보호 모드 실패 시 직접 연결로 몰래 변경 |

## 인터페이스 설계 원칙

- 호출 API는 최소 권한만 부여하고 입력의 출처를 검증한다.
- UI에는 `lock()/unlock()`, `list_downloads()`, `export_file()`, `get_security_status()` 같은 좁은 추상 인터페이스를 노출한다.
- 파일 내용과 암호화 키를 UI 이벤트나 로그로 전달하지 않는다.
- 다운로드·프로필 쓰기는 취소/재시작/크래시에도 상태 전이가 일관되도록 설계한다.
- 비동기 I/O와 렌더링 UI 이벤트 루프를 분리한다.
- 보안 상태는 `requested`, `active`, `verified`, `failed` 등으로 구분한다. 토글이 켜졌다고 검증 완료가 아니다.

## 프로세스·권한 경계

- Chromium/Qt/CEF의 다중 프로세스 모델을 유지한다.
- 웹 콘텐츠는 최저 권한 렌더러에서 실행한다.
- Rust 코어를 별도 프로세스로 두는 방안과 네이티브 라이브러리 방안을 위협 모델 기준으로 비교한다.
- OS 방화벽 서비스가 필요하면 설치·권한·제거 실패·재부팅 시 정책 일관성을 정의한다.
- 브라우저 잠금은 세션 일시정지, 프로세스/스토리지 핸들 종료, 키 수명 조절까지 고려한다.

## 상태 변경과 오류

`LOCKED → UNLOCKING → READY → LOCKING → LOCKED`와 별도 `ERROR` 상태를 정의한다. `READY` 전에는 비밀 데이터 접근이 불가능해야 하며, 암호화 프로필 실패 시 민감 데이터가 새 평문 프로필로 생성되지 않는다. 복구 가능한 장애/재시도/복구 불가능 오류를 구분한다.

## 저장소 및 파일 구조 (가안)

```text
VEIL/
├── apps/desktop/
├── crates/veil-security/
├── modules/browser/
├── modules/profile/
├── modules/downloads/
├── modules/network/
├── tests/unit/
├── tests/integration/
├── tests/security/
├── scripts/
└── docs/...
```

개발 과정에서 실제 저장 구조가 결정되면 ADR과 구현 문서를 갱신한다. 모듈이 특정 디렉터리에 있어야 한다는 이유만으로 설계의 무결성을 희생하지 않는다.

## 보호 프로필 볼륨의 권한 분리

관리자가 준비한 BitLocker 볼륨에서 정상 잠금·복원은 실측했다. 제품 통합은 [ADR-0003](decisions/ADR-0003-volume-broker.md)을 따른다. 브라우저는 일반 사용자 권한으로 실행하고, 등록된 전용 볼륨의 상태 확인·연결·잠금 해제·잠금·분리만 별도 로컬 서비스가 처리한다. 브라우저는 볼륨 보호 상태와 프로필 최종 경로를 확인한 뒤에만 엔진을 시작하며, 실패하면 평문 프로필로 대체하지 않는다. 외부에서 수동 관리하는 볼륨은 개발 검증에 계속 사용한다. [보호 볼륨 실측](05-encrypted-profile.md)

## 엔진 전환 1차 경계

`apps/desktop/Engine/IBrowserEngine.cs`와 `WebView2BrowserEngine.cs`는 세션 초기화 및 WPF 뷰/기본 탐색의 좁은 어댑터 경계를 제공한다. WebView2 네이티브 이벤트, 쿠키/저장소, 다운로드, 인증, CDP, 프로세스 실패 통합은 현재 호스트의 기존 경계를 유지한다. 자체 엔진을 활성화할 수 있는 팩토리 설정, 플러그인 로더, 명령줄 선택, 자동 대체 경로는 없다.

상세 전환 순서와 수용 기준은 [17-engine-transition.md](17-engine-transition.md)에 기록한다.
