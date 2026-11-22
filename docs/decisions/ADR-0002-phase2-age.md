# ADR-0002 Phase 2 암호화 실험에 age v1 사용

- 상태 — 독립 개발 실험에 채택. 출시용 포맷 결정은 보류.
- 작성일 — 2026-11-23
- 관련 요구 — SR-01/02/03, SEC-005/006, C-01/02

## 선택

Rust `age` 0.12.1을 기본 기능 없이 사용한다. 키 봉투에는 표준 age v1 scrypt 수신자를, 파일과 백업 객체에는 X25519 수신자와 age v1 ChaCha20-Poly1305 스트리밍을 사용한다. 라이브러리가 키 래핑, 난수, 청크 순서와 마지막 청크 인증을 담당한다. VEIL은 인증된 키 봉투 안에 스키마 버전과 세 용도의 키를 담는다. [age v1 명세](https://c2sp.org/age) · [Rust 구현](https://github.com/str4d/rage)

프로필 데이터, 다운로드 객체, 백업용 키를 독립 생성한다. 합성 테스트에는 scrypt `log2(N)=17`, `r=8`, `p=1`을 적용하고 그 이상의 작업 계수를 거부한다. 출시용 작업 비용은 대상 기기와 오프라인 위협 기준에 맞춰 다시 결정한다.

## 대안

libsodium의 Argon2id와 secretstream은 유력한 대안이다. 바인딩 및 키 봉투 포맷을 추가로 검증해야 한다. Windows CNG는 운영체제 암호 원시 기능을 제공하며 청크 경계와 최종 무결성 검증은 상위 계층에서 설계해야 한다. 이 실험의 목표는 기존 표준 파일 포맷으로 독립 검증을 빠르게 시작하는 것이다. [libsodium secretstream](https://doc.libsodium.org/secret-key_cryptography/secretstream) · [Windows CNG](https://learn.microsoft.com/windows/win32/seccng/cng-portal)

## 경계와 재평가

같은 파일시스템에서 임시 암호문을 동기화한 뒤 기존 파일을 덮어쓰지 않는 방식으로 발행한다. 중단 후 남는 미완료 임시 객체는 최종 파일로 취급할 수 없다. 디렉터리 메타데이터의 전원 차단 내구성과 키 회전·백업 복구 정책은 별도 검증이 필요하다. 파일명·크기·시간, 객체 교체 및 유효한 과거 상태의 재생 방지는 상위 저장소 설계 대상이다. 브라우저의 기존 평문 프로필에는 이 코어를 연결하지 않았다. 시험 결과는 [Phase 2 기록](../phase2-evidence.md)에 남겼다.
