# Phase 2 독립 암호화 코어 실측

2026-11-23 · 기준 커밋 `5868126` 및 작업 트리 · Windows 11 x64 · Rust 1.99.1 MSVC · age 0.12.1 · tempfile 3.27.0 · zeroize 1.9.0

[veil-security](../crates/veil-security/src/lib.rs)는 독립 Rust 라이브러리다. 패스프레이즈를 암호화 키 봉투를 여는 데 사용하고, 용도별로 독립 생성한 키로 객체를 암호화한다. 스트리밍 시험은 합성 입력을 메모리에 생성해 암호문으로 기록한다. 소스 읽기 실패, 중단, 이름 충돌 시 완성 경로에 객체를 발행하지 않는다. 파싱 전에 키 봉투와 객체 헤더를 제한한다. 작은 객체는 끝까지 인증한 후 메모리로 반환하며, 큰 객체는 평문을 내보내지 않고 순차 검증할 수 있다.

| 시험 | 결과 |
|---|---|
| 키 생성, 종료 후 재개방, 키 봉투 사본을 통한 백업 객체 복원 | PASS |
| 잘못된 비밀번호, 키 봉투 변조, 용도 불일치 | PASS |
| 빈 객체, 작은 객체 내용 왕복, 64 MiB + 37바이트 스트리밍 | PASS |
| 암호문 바이트 변조, 잘림, 덧붙인 바이트, 순서 변경, 과도한 헤더 | PASS |
| 알 수 없는 키 스키마, 허용 한도를 넘는 scrypt 작업 계수 | PASS |
| 입력 중간 오류와 동일 경로 동시 쓰기 | PASS. 읽기 오류의 최종 파일은 없었고 이름 충돌에서는 한 객체만 발행됐다. |
| 기록 도중 실제 테스트 자식 강제 종료 | PASS. 완성 경로가 생성되지 않았고 기존 객체는 재검증됐다. 남은 임시 객체는 age 헤더를 담고 있었고 합성 평문 마커가 검출되지 않았으며 끝까지 인증하는 데 실패했다. |
| 전원 차단, 키 회전, 계정 간 ACL, OS 캐시·덤프, 전체 의존성 보안 감사 | NOT RUN |

```powershell
cargo test --release --locked --manifest-path crates/veil-security/Cargo.toml
cargo clippy --release --locked --manifest-path crates/veil-security/Cargo.toml --all-targets -- -D warnings
```

작업 환경에서는 공개 crates.io에서 잠금 파일의 의존성을 `.veil-dev/cargo`로 내려받은 뒤 `--offline --locked`로 테스트했다. [Cargo.lock](../crates/veil-security/Cargo.lock) · [테스트 코드](../crates/veil-security/tests/core.rs)

이 범위의 사본 복원은 키 봉투와 객체 사본으로 실시했다. 브라우저 잠금, 백업 UX, 휴지통과 임시 파일 정리, 비밀번호 분실 처리, 암호화 파일시스템 연동은 후속 작업이다. 평문 청크가 메모리에 존재하는 동안의 페이지 파일·덤프와 현재 프로필 전체의 잠금 상태 보안도 별도 시험이 필요하다.
