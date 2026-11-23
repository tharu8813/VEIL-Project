# 10. 테스트 전략 및 검증 증거

## 테스트 원칙

- 실제로 실행한 테스트만 PASS로 기록한다.
- 구현·테스트·보안 검증을 서로 별도 상태로 관리한다.
- 외부 웹 계정과 실제 개인정보 대신 로컬 서버·가짜 계정·테스트 비밀 문자열 사용.
- 결과에는 코드 커밋, OS/엔진/라이브러리 버전, 실행 명령, 기대/실제 결과를 포함한다.
- 화면이 정상이라는 사실만으로 내부 보안성을 주장하지 않는다.

## 계층별 테스트

1. **단위:** KDF, 인증 암호화, 키 분리, 파일 금고, 저장 포맷, URL/파일명 검증.
2. **통합:** UI↔브라우저↔저장소, 정상/비정상 종료, 데이터 복원, 다운로드.
3. **보안:** 비밀 데이터의 경로 추적, 파일 핸들, 잘못된 키/변조 검출, 잠금 상태 접근.
4. **네트워크:** DNS/IPv6/WebRTC/UDP/QUIC/프록시 장애.
5. **사용성:** 단축키, 일반 탐색, 다운로드 흐름, 기본 설정, 경고 빈도.
6. **배포:** 설치/업데이트/롤백/제거, 앱 서명, 엔진 보안 패치 확인.

## 로컬 테스트 서버

쿠키 세팅/만료, LocalStorage·IndexedDB 저장, Service Worker, CacheStorage, 로그인 시뮬레이션, 파일 업로드, 일반 다운로드, POST 다운로드, 리다이렉트, Content-Disposition, 대용량 다운로드를 재현하는 서버 및 fixture를 구성한다.

## Phase 3·4 통합 VM 실행

로컬에서 `node tests/phase3/phase34-vm.mjs --plan`으로 실행 계획을 확인할 수 있다. 계획에서는 모든 항목이 `NOT RUN`이다. 기존 기준 스냅샷은 보존하고 준비된 오프라인 VirtualBox 시험 VM에서 실행한다. VM에 미리 가져온 Debug 브라우저와 그에 맞는 Rust 금고 도구를 사용한다. 설치형 Release 검증은 별도로 기록한다.

1. **게스트 관리자 셸**에서 시험용 VHDX와 BitLocker 상태를 확인한 뒤 사전 점검 증거를 저장한다. 대상 볼륨은 게스트 시스템 디스크와 별개여야 한다.

   ```powershell
   .\tests\phase3\phase34-vm-preflight.ps1 -IsolatedVm -ImagePath 'C:\VEIL-Lab\working.vhdx' -ProfileParent 'V:\VEIL-Lab' -Out '.\.veil-dev\phase34-preflight.json'
   .\tests\security\trace-writes.ps1 -Action Start -IsolatedVm -Phase34 -Volume 'V:\' -OutDir '.\.veil-dev\phase34-trace'
   ```

2. **게스트 일반 사용자 셸**에서 시험을 실행한다. 같은 게스트 부팅과 볼륨 GUID를 다시 확인한 뒤 프로필 경로, 엔진 종료, 금고 캡처, 금고 지속성, 점프 목록을 순서대로 실행한다. 선택한 항목만 재시험할 때는 `--only <ID>`를 반복해서 지정한다. 항목별 로그, 시험 보고서 사본과 `summary.json`은 게스트 저장소의 `.veil-dev/phase34-vm-*` 아래에 남는다.

   ```powershell
   node tests/phase3/phase34-vm.mjs --run --isolated-vm --preflight .veil-dev/phase34-preflight.json
   ```

3. **게스트 관리자 셸**에서 추적을 종료하고 WebView2의 보호 볼륨 쓰기와 볼륨 밖 쓰기를 확인한다. 이 모드에서는 보호 볼륨 밖의 증거 폴더에 대한 Node 러너 쓰기만 예외로 처리한다.

   ```powershell
   .\tests\security\trace-writes.ps1 -Action Stop -IsolatedVm -Phase34 -Volume 'V:\' -OutDir '.\.veil-dev\phase34-trace'
   ```

4. **같은 세션의 디스크 부족 시험**은 별도 2 GiB 이하 폐기용 BitLocker VHDX에서 실행한다. 사전 점검에 `-DiskFullImage`와 `-DiskFullParent`를 추가해 새 증거를 만들고 `--only profile-disk-full`로 실행한다. 이 러너는 프로필 복구를 검증한다. 금고가 가득 찬 상태의 내보내기·키 교체에는 별도 시험이 필요하다.

스냅샷에서 시험 VM 준비, 서명된 Release 서비스 설치·복구 드릴, 실제 전원 차단은 관리자가 확인하는 단계다. 전원 차단은 지정된 시험 VM에서 기존 arm/check 러너로 준비와 재시작 후 확인을 진행한다. 성공한 합성 데이터까지 정리하려면 실행 시 `--cleanup-on-pass`를 지정한다. 실패한 데이터는 시험 볼륨에 보존한다. [전원 차단 러너](../tests/phase3/power-loss.mjs)

## 단일 VM 일괄 시험

기능이 안정된 뒤 오프라인 VM 세션 한 번으로 Phase 3·4와 Phase 5 로컬 시험, 그리고 볼륨 밖 쓰기 추적을 함께 실행한다. 아래 절차는 위의 개별 단계를 대신한다. 이미 통과한 항목은 관련 코드나 보안 경계가 바뀌었을 때만 다시 실행한다.

| 단계 | 위치 | 명령 |
|---|---|---|
| 0. 번들 | 호스트(일반 사용자) | `node tests/vm/make-bundle.mjs`. 커밋된 트리에서 Debug 데스크톱과 `veil-vault.exe`, 소스, 게스트 스크립트, SHA-256 매니페스트, `VEILBAT.viso`를 만든다. 호스트 시스템 설정은 바꾸지 않는다. |
| 1. 연결 | 호스트 | VM을 `01-veil-tools-offline` 스냅샷으로 되돌리고 종료한 상태에서, 출력된 `VBoxManage storageattach` 명령으로 DVD를 연결한다. 네트워크 어댑터는 Not attached로 둔다. |
| 2. 준비 | 게스트 관리자 셸 | `D:\BATCH.PS1 -Stage Prepare -StandardUser <일반 계정>`. 매니페스트 해시 검증 → `C:\VEIL-Batch`에 풀기 → 폐기용 BitLocker VHDX 두 개(V: 1 GiB, W: 256 MiB, XTS-AES-256, 임의 암호는 메모리에만 보관) → 사전 점검 증거 → Procmon 추적 시작(V:와 W:를 보호 볼륨으로 지정). |
| 3. 실행 | 게스트 일반 사용자 셸 | 2단계가 출력한 `phase34-vm.mjs --run ...` 명령. |
| 4. 마무리 | 게스트 관리자 셸 | `C:\VEIL-Batch\BATCH.PS1 -Stage Finish`. 추적 종료·분석 → `RESULTS.ZIP`과 결과표 출력. 모두 PASS면 시험 볼륨을 잠그고 분리한 뒤 삭제한다. FAIL이 하나라도 있으면 볼륨과 증거를 그대로 둔다. |

자동 실행 항목(3단계):

- 보호 볼륨 V:의 프로필: `profile-routing`, `engine-kill`, `vault-capture`, `vault-persistence`, `jump-list`, `desktop-crash-cycle`
- V:의 작업 폴더에서 실행하는 루프백 시험: `network-guard`, `dns-protection`, `smartscreen`. 게스트에 openssl이 없으면 뒤의 두 시험은 사유와 함께 NOT RUN으로 남는다.
- 작은 볼륨 W:: `profile-disk-full`

4단계 추적 결과는 `physical-writes`로 기록된다.

계속 수동이거나 NOT RUN으로 남는 항목:

- 설치형 Release 서비스(`installed-release`)
- 복구 드릴(`recovery-backup`)
- 실제 전원 차단(`power-loss`): 기존 arm/check 러너와 호스트의 `VBoxManage controlvm <vm> poweroff`
- 금고 디스크 부족(`vault-disk-full`)
- 인터넷 VM 항목(`internet-*`): DNS 실제 경로, IPv6·WebRTC, 절전·네트워크 전환, 실제 SmartScreen 경고·다운로드 판정·Windows 설정 재정의, 실제 사이트 호환성. 오프라인 VM에서는 실행하지 않는다.

VirtualBox `keyboardputstring`으로 명령을 입력할 때 주의할 점이 있다. 큰따옴표는 Windows 인수 전달 과정에서 빠지므로 작은따옴표만 쓴다. 인수가 `\`로 끝나면 다음 따옴표가 이스케이프되므로 이런 인수는 피한다. Procmon은 기본 위치(`C:\Tools\Procmon`)에 없으면 바탕 화면의 `ProcessMonitor` 폴더에서 찾는다. 게스트에 openssl이 없으면 DoH를 쓰는 두 시험은 NOT RUN으로 남는다.

결과는 게스트 `C:\VEIL-Batch\RESULTS.ZIP`에 남는다. 그 안의 `summary.json`, 항목별 로그, `outside-writes.json`, `batch.json`이 SECURITY_STATUS 갱신 근거가 된다.

## 최소 필수 시나리오

| ID | 테스트 | 기대 결과 |
|---|---|---|
| P-01 | 쿠키·LocalStorage·IndexedDB 저장 후 종료/재실행 | 정책에 따라 영구 데이터 복원 |
| P-02 | Service Worker/CacheStorage 재실행 | 지원/불가 범위가 테스트로 기록됨 |
| P-03 | 잠긴 저장소를 오프라인 복사·분석 | 키 없이 민감 데이터 복원 실패 |
| P-04 | 프로필 저장 도중 앱 강제 종료 | 저장소 손상 감지/안전한 복구/평문 폴백 없음 |
| C-01 | 암호화/복호화 왕복 | 내용 동일·인증 검증 |
| C-02 | 키 오류/암호문 변경·잘림 | 확실한 실패·사용자 데이터 보존 |
| D-01 | GET/POST/인증/Blob 등 다운로드 | 지원 유형 보호 저장 또는 안전 차단 |
| D-02 | 대용량/중단/중복 이름/디스크 가득 참 | 명확한 상태 및 복구 |
| D-03 | 파일 외부 내보내기 | 사용자 지정 위치에만 평문 생성 |
| N-01 | 프록시 프로세스 강제 종료 | 보호 모드에서 직접 연결 없음 |
| N-02 | WebRTC·IPv6·DNS·QUIC | 경로 증거와 제한 범위 보고 |
| U-01 | Ctrl+T/H/J/D/L·탭 복구 | 익숙한 브라우저 동작 |
| R-01 | 오래된 빌드 업데이트 | 데이터 유지·검증된 업그레이드 경로 |

## 디스크/메모리 조사 지침

- 앱 프로필·시스템 임시 폴더·크래시 로그·하위 프로세스 경로를 포함한다.
- Procmon/ETW 등으로 쓰기 이벤트를 추적하고 명시된 위협 모델과 연결한다.
- 테스트용 마커 문자열 파일 검색은 **보조 증거**일 뿐 완전한 비노출 증명은 아니다.
- 잠금 해제 중 프로세스 메모리나 OS 페이지 파일 등에 대한 한계는 별도 기록한다.

## 성능 비교

보안 구성 사용 전후 임의로 기능을 끄는 대신, 기본 엔진과 실제 VEIL 보호 구현을 동일 하드웨어·시나리오에서 비교한다. 시작·잠금 해제·새 탭·페이지 반응·스크롤·CPU/GPU/메모리·다운로드·암호 처리량을 반복 측정한다. 목표 수치는 실측 후 정한다.

## 테스트 보고 템플릿

```markdown
### TEST-ID / 명칭
- 상태: PASS / FAIL / NOT RUN / BLOCKED
- 실행 시점 및 커밋:
- 환경: OS, 브라우저 엔진, 보안 모듈 버전
- 실행 명령 및 시험 데이터:
- 기대 결과:
- 실제 결과:
- 증거 경로:
- 발견된 위험:
- 후속 조치:
```

실패와 미실행을 통과로 둔갑시키지 않는다.
