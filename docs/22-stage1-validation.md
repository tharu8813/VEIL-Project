# 22. Stage 1: 기능 안정화 검증

상태: PR #31–33 코드 반영 및 CI 통과. 실제 Windows 통합 시험과 VM 재시험은 NOT RUN. 이 문서는 기존 Phase 로드맵을 대체하지 않는다.

| 범위 | 실행 방법 | 완료 조건 | 상태 |
|---|---|---|---|
| 기본 탐색, 세션 및 관리 탭 | stage1-batch desktop-regression | 기존 탐색/스토리지 복원 및 내부 경로 저장, 닫기 검증 통과 | NOT RUN |
| 북마크 다중 창 | desktop-regression + 수동 | 표시 토글·추가·이름 변경·삭제가 모든 창과 열린 관리 목록에 즉시 반영; 실패 시 롤백 | NOT RUN |
| 다운로드 팝업 및 진행률 | desktop-regression + vault-capture | 중복 탭 없음, 팝업 탐색 없음, 진행률·취소·저장 결과 정상 | NOT RUN |
| 금고 최초 설정 | desktop-regression wizard + 수동 | 신규 합성 프로필에서 암호/복구 코드 확인 후 실제 다운로드 완료; 취소·재시도·종료가 정상 | NOT RUN |
| 금고 지속성/복구 | --include-vault-persistence | 재시작, 복구, 회전, 변조 검출, 인증된 내보내기 | NOT RUN |
| 창·탭 수명 및 메모리 | 수동 반복 계측 | 동일 조건의 반복 창/탭 열기·닫기에서 퇴역 UI/엔진 잔존 여부 확인. 워밍업 이후 메모리 추세 및 프로세스 수 기록 | NOT RUN |
| 보호 볼륨 및 설치 Release | docs/10-test-strategy.md | 관련 기존 보안 게이트의 증거와 판정 갱신 | NOT RUN |

## 실행 순서

1. 일반 사용자 Windows 환경에서 `node tests/integration/stage1-batch.mjs --plan`으로 범위를 확인한다.
2. `--run`으로 신규 합성 프로필 시험을 진행한다. 필요 시 `--include-vault-persistence`를 추가한다. 결과와 원본 하네스가 출력한 증거 폴더를 함께 보존한다.
3. 수동 확인은 새 임시 합성 프로필에서 수행한다. 기본 테스트 암호 환경변수로 자동 해제하지 않은 별도 Debug 실행으로 최초 다운로드 설정을 확인한다. 기존 제품 프로필/금고나 실제 사용자 자료를 사용하지 않는다.
4. 메모리 계측은 VEIL 및 WebView2 하위 프로세스 수, private bytes, working set을 시작/워밍업/반복/유휴 구간별로 기록한다. 단일 스냅샷으로 누수를 단정하지 않는다.
5. 실패는 재현 절차와 로그를 첨부해 수정하고, 관련 시험을 재실행한다. 별도 격리 VM 검증 전에는 Gate A–E 판정을 바꾸지 않는다.

Stage 2의 독립적인 코드 작업은 진행할 수 있으나, 1단계 실제 실행 검증을 완료로 표시하거나 출시 판단 근거로 사용하지 않는다.
