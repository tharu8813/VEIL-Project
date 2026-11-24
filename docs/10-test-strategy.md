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
| 0. 번들 | 호스트(일반 사용자) | `node tests/vm/make-bundle.mjs`. 커밋된 트리에서 Debug 데스크톱과 `veil-vault.exe`, 소스, 게스트 스크립트, SHA-256 매니페스트, `VEILBAT.viso`를 만든다. 패키지 시험용 서명 전 Release 결과물(`PAYLOAD-1.0.0-alpha.1~3.ZIP`, 고지·SBOM 포함)과 루프백 시험 인증서(`TESTCERT.ZIP`, 7일 유효, 게스트에 openssl이 없어서 호스트에서 생성)도 넣는다. 호스트 시스템 설정과 신뢰 저장소는 바꾸지 않는다. |
| 1. 연결 | 호스트 | VM을 `01-veil-tools-offline` 스냅샷으로 되돌리고 종료한 상태에서, 출력된 `VBoxManage storageattach` 명령으로 DVD를 연결한다. 네트워크 어댑터는 Not attached로 둔다. |
| 2. 준비 | 게스트 관리자 셸 | `D:\BATCH.PS1 -Stage Prepare -StandardUser <일반 계정>`. 매니페스트 해시 검증 → `C:\VEIL-Batch`에 풀기 → 폐기용 BitLocker VHDX 두 개(V: 1 GiB, W: 256 MiB, XTS-AES-256, 임의 암호는 메모리에만 보관) → 사전 점검 증거 → Procmon 추적 시작(V:와 W:를 보호 볼륨으로 지정). |
| 3. 실행 | 게스트 일반 사용자 셸 | 2단계가 출력한 `phase34-vm.mjs --run ... --cert-dir C:\VEIL-Batch\testcert` 명령. |
| 4. 마무리 | 게스트 관리자 셸 | `C:\VEIL-Batch\BATCH.PS1 -Stage Finish`. 추적 종료·분석 → `RESULTS.ZIP`과 결과표 출력. 모두 PASS면 시험 볼륨을 잠그고 분리한 뒤 삭제한다. FAIL이 하나라도 있으면 볼륨과 증거를 그대로 둔다. |
| 5. 패키지 | 게스트 관리자 셸 | 추적이 끝난 뒤 `C:\VEIL-Batch\src`에서 `tests\vm\guest-package.ps1 -Stage Sign` → `Install`(운영자 단계) → 운영자 점검 3개를 `-Stage Record`로 기록 → `Update` → `Remove`. 결과는 `C:\VEIL-Batch\PACKAGE-RESULTS.ZIP`. 아래 [패키지 시험](#패키지-시험-adr-0010-e1e2b3) 참고. |
| 6. 전원 차단 | 게스트 일반 사용자 셸 + 호스트 | 별도 프로필로 `node tests\phase3\power-loss.mjs --arm ...` → 호스트에서 `VBoxManage controlvm <vm> poweroff` → 재부팅·잠금 해제 후 `--check`. |

자동 실행 항목(3단계):

- 보호 볼륨 V:의 프로필: `profile-routing`, `engine-kill`, `vault-capture`, `vault-persistence`, `jump-list`, `desktop-crash-cycle`
- V:의 작업 폴더에서 실행하는 루프백 시험: `network-guard`, `webrtc-guard`, `dns-protection`, `smartscreen`, `phase6`(Phase 6 관리 페이지와 합성 비밀번호 비밀의 디스크 검색). TLS가 필요한 뒤의 세 시험은 번들의 시험 인증서(`--cert-dir`)를 쓴다. 인증서도 openssl도 없으면 사유와 함께 NOT RUN으로 남는다.
- 작은 볼륨 W:: `profile-disk-full`

4단계 추적 결과는 `physical-writes`로 기록된다.

계속 수동이거나 NOT RUN으로 남는 항목:

- 설치형 Release 서비스(`installed-release`)와 복구 드릴(`recovery-backup`): 5단계 패키지 시험에서 기록한다.
- 실제 전원 차단(`power-loss`): 기존 arm/check 러너와 호스트의 `VBoxManage controlvm <vm> poweroff`
- 금고 디스크 부족(`vault-disk-full`)
- 인터넷 VM 항목(`internet-*`): DNS 실제 경로, IPv6·WebRTC, 절전·네트워크 전환, 실제 SmartScreen 경고·다운로드 판정·Windows 설정 재정의, 실제 사이트 호환성. 오프라인 VM에서는 실행하지 않는다.

VirtualBox `keyboardputstring`으로 명령을 입력할 때 주의할 점이 있다. 큰따옴표는 Windows 인수 전달 과정에서 빠지므로 작은따옴표만 쓴다. 인수가 `\`로 끝나면 다음 따옴표가 이스케이프되므로 이런 인수는 피한다. Procmon은 기본 위치(`C:\Tools\Procmon`)에 없으면 바탕 화면의 `ProcessMonitor` 폴더에서 찾는다. 게스트에 openssl이 없으면 DoH를 쓰는 두 시험은 NOT RUN으로 남는다.

결과는 게스트 `C:\VEIL-Batch\RESULTS.ZIP`에 남는다. 그 안의 `summary.json`, 항목별 로그, `outside-writes.json`, `batch.json`이 SECURITY_STATUS 갱신 근거가 된다.

### 패키지 시험 (ADR-0010 E1·E2·B3)

`tests/vm/guest-package.ps1`은 오프라인 VM 안에서만 일회용 코드 서명 인증서를 만들어 VM의 신뢰 저장소에 넣는다. 그 인증서로 서명 전 결과물 3개 버전에 서명하고, 매니페스트는 운영 코드(`Write-VeilPackageManifest`)로 만든다. 오프라인이라 타임스탬프는 넣지 않는다. 시험 후에는 VM을 스냅샷으로 되돌린다.

| 단계 | 판정 |
|---|---|
| `Sign` | `package-sign`과 변조 거부 7종 `package-tamper-*`: 정상 통과, 파일 변조, 추가 파일, 누락 파일, 매니페스트 수정, 다른 서명자 지문, 서명 없는 실행 파일(매니페스트를 다시 서명해도 Authenticode에서 거부). 설치용 일회용 BitLocker 볼륨 X:을 만든다. 암호는 운영자가 입력하고 저장하지 않는다. |
| `Install` (운영자) | `package-install`: 운영 설치 스크립트가 X: 암호 입력, 새 복구 암호 표시, 복구 암호 재입력(B3 드릴)을 요구한다. 그다음 운영자가 `installed-unlock`(일반 사용자로 잠금 해제·탐색), `installed-relock`(정상 종료 후 VHDX 분리), `installed-refuses`(`client.json` 없이 시작 거부, A1)를 확인하고 `-Stage Record`로 남긴다. |
| `Update` | `package-update`(alpha.1 → alpha.2), `package-same-version`·`package-lower-version` 거부, `package-registration-kept`(클라이언트 고정값 외 등록·`client.json`·VHDX 불변). alpha.3 업데이트를 트리 교체 직후와 커밋 표시 직후에 강제 종료하고 `-RecoverInterruptedUpdate`로 복구한다(`package-interrupt-pre-commit`은 이전 버전으로, `post-commit`은 새 버전으로 복구). 종료 시점을 못 맞추면 NOT RUN으로 남긴다. |
| `Remove` | `package-remove`: 서비스와 실행 파일 삭제, 등록(복구 보호기 ID 포함)과 VHDX 보존. |

호스트에서는 같은 매니페스트·변조 판정을 `tests/install/package-manifest.test.mjs`가 메모리 인증서로 확인한다. 이때 Authenticode, ACL, 인증서 체인 신뢰 검사는 완화한다. 완화 없는 전체 검사는 이 VM 시험에서만 한다.

## 인터넷 VM 시험 (Gate D)

ADR-0004 정책 4에 따라 인터넷에 연결된 **별도의 격리 시험 VM**에서만 실행한다. 오프라인 실험 VM과 그 스냅샷은 그대로 둔다. 시험 계정과 합성 프로필만 쓰고, 공유 폴더·클립보드·드래그 앤 드롭은 끈다. 소스와 Debug 빌드는 오프라인 일괄 시험과 같은 번들(`make-bundle.mjs`)로 옮긴다.

| 단계 | 위치 | 명령 |
|---|---|---|
| 1. 캡처 시작 | 게스트 관리자 셸 | `<src>\tests\vm\guest-internet.ps1 -Stage Start -Src <src>`: 기본 경로(인터넷)가 있는지 확인한 뒤 pktmon으로 포트 53만 캡처한다. |
| 2. 실행 | 게스트 일반 사용자 셸 | `node tests\vm\internet-batch.mjs --run --internet-vm --desktop-dll <Veil.Desktop.dll>` |
| 3. 판정 | 게스트 관리자 셸 | `<src>\tests\vm\guest-internet.ps1 -Stage Finish -Src <src>`: 캡처를 멈추고 `dns-path`를 판정한다. |

| ID | 판정 |
|---|---|
| `dns-path` | PASS 조건: 시스템 DNS 대조 이름이 평문 DNS(포트 53)에 보이고, 보안 DNS(Cloudflare)로 찾은 이름은 보이지 않는다. 대조 이름이 없으면 캡처를 믿을 수 없으므로 FAIL. DoH 제공자 이름의 부트스트랩 조회는 `public-doh-bootstrap`에 기록한다. |
| `webrtc-standard` | 기록만(OBSERVED). STUN을 쓴 ICE 후보 종류(host/srflx, IPv4/IPv6/mDNS). |
| `webrtc-proxy` | PASS 조건: 프록시 보호에서 UDP 후보가 하나도 없고, 페이지가 프록시를 거친다. |
| `smartscreen-demo` | Microsoft SmartScreen 데모 피싱 페이지를 켜기·끄기로 비교한다. 켰을 때 차단되고 껐을 때 열리면 PASS, 그 밖에는 OBSERVED. |
| `site-compat` | 실제 사이트 8개가 HTTPS 우선·추적 방지 엄격 상태에서 열리고, HTTP 전용 사이트는 경고 페이지를 보여야 PASS. |
| `ipv6-reachability` | 기록만. IPv6 전용 사이트에 도달하는지. |
| `proxy-doh` | 프록시 보호 + Cloudflare DoH. 실제 사이트와 DoH 서버가 모두 로컬 프록시의 CONNECT로 나가야 하고, 합성 이름이 같은 캡처의 평문 DNS에 없어야 PASS(대조 이름은 `dns-path`와 공유). |
| `quic-proxy` | 같은 프로필로 두 번 연 페이지의 `nextHopProtocol`. 표준 모드 대조군이 `h3`에 도달하고 프록시 보호가 `h3`가 아니면 PASS. 프록시에서 `h3`면 FAIL. 대조군이 `h3`에 못 미치면 OBSERVED. |
| `https-downgrade-redirect` | HTTPS에서 HTTP로 리다이렉트하는 페이지(httpbin)가 HTTPS 경고 페이지로 끝나면 PASS, HTTP 페이지가 그대로 열리면 FAIL, 리다이렉트 서비스 문제면 OBSERVED. |
| 수동(NOT RUN) | 절전·네트워크 전환, Windows "Microsoft Edge용 SmartScreen" 끄기에 따른 재정의(게스트 설정 변경 필요). |

인터넷 VM 준비 전에 호스트에서 확인할 수 있는 부분은 루프백 시험으로 대신한다. `node tests/phase5/webrtc-guard.mjs`는 STUN 없이 프록시 모드의 UDP 후보 0개를 확인한다.

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
