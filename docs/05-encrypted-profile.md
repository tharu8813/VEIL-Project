# 05. 암호화 프로필 및 브라우저 데이터 영속화

## 사용자 요구 [확정]

VEIL은 매 실행마다 로그아웃되는 시크릿 브라우저가 아니다. 쿠키, 로그인 상태, 방문 기록, 사이트 데이터, 북마크를 유지하면서, 저장된 민감정보를 잠긴 상태에서 암호화로 보호한다.

## 보호 대상 데이터 목록

- 영구/세션 쿠키 및 인증 토큰 (보관 정책 분리)
- LocalStorage, SessionStorage의 복원 관련 데이터
- IndexedDB, WebSQL 등 엔진이 제공하는 저장소(지원 여부 별도 기록)
- Service Worker, CacheStorage, 웹 앱 상태
- 방문 기록 URL/제목/검색어/시간, favicon, 썸네일
- 북마크, 세션 복원, 양식 자동완성, 저장된 비밀번호
- 프로필 설정과 사이트 권한, 오류 로그 내 민감 데이터
- HTTP 캐시, GPU/미디어 캐시, 다운스트림 하위 프로세스의 임시 기록

모든 항목을 '지원'으로 추정하지 말고 엔진별 실제 저장 위치와 지원 여부를 **inventory 표**로 관리한다.

## 검토할 저장 구조

제품 브로커는 잠금 여부와 사용 가능한 보호 상태를 별도로 판정한다. Windows는 잠긴 볼륨의 보호 상태를 `Unknown`으로 보고하거나 암호화 상태 조회를 거부할 수 있다. 등록한 볼륨의 잠금 해제 요청을 허용하는 판단은 보호 인증에 사용하지 않는다. 잠금 해제 직후 완전 암호화, 보호 켜짐, 등록 키 보호기가 확인된 경우에만 프로필 시작을 허용한다. 조건을 충족하지 못하면 브로커가 비강제 재잠금을 시도한다. [BitLocker 보호 상태](https://learn.microsoft.com/windows/win32/secprov/getprotectionstatus-win32-encryptablevolume) · [암호화 상태 조회](https://learn.microsoft.com/windows/win32/secprov/getconversionstatus-win32-encryptablevolume)

1. **전용 암호화 파일 시스템/볼륨:** 기존 브라우저 엔진과 호환성이 좋을 수 있으나 볼륨 연결 중 평문 접근, 장치 잠금, OS 캐시, 파일 핸들 등 조사 필수.
2. **엔진 저장 계층 수정:** 강한 통합이 가능할 수 있으나 유지보수/패치 비용 높음.
3. **개별 DB 암호화만:** 모든 사이트 저장소를 보호하지 못할 수 있어 단독으로는 필수 요구 충족으로 인정 불가.
4. **기본 평문 프로필에 종료 시 재암호화:** 정상 종료 전 평문 노출 때문에 최종 설계로 금지.

## 핵심 작동 요구

- 첫 실행: 마스터 키 계층 설정 → 안전한 프로필 생성.
- 시작: 사용자 인증 → 보호 저장소 이용 가능 여부 확인 → 브라우저 인스턴스 시작.
- 이용: 데이터는 자동 저장되지만 영구 기록은 보호된 저장 계층에만 기록.
- 종료: 쓰기 플러시 → 하위 브라우저 프로세스 정리 → 저장소 잠금 → 키 수명 종료.
- 크래시: 미완료 쓰기 탐지, 트랜잭션 복구, 저장소 무결성 검사, 민감 데이터 누출 여부 확인.

## 유의할 보안 한계

'암호화된 VHD/가상 볼륨'을 사용한다고 해서 실행 중 같은 권한 사용자 프로세스로부터 데이터가 격리된다는 뜻은 아니다. `02-threat-model.md`에서 **잠금 상태의 오프라인 공격**과 **잠금 해제 상태의 실행 중 공격**을 분리한다. 임의 시스템 로그와 페이징의 완전 제어는 보장하지 않는다.

## 구현 우선순위

1. 엔진별 데이터 저장 경로를 로컬 테스트로 열거한다.
2. 디스크 쓰기 경로·파일 핸들·캐시·크래시 덤프를 추적한다.
3. 안전한 저장 아키텍처를 2개 이상 PoC로 비교한다.
4. 정상 종료/강제 종료/저장소 손상/동시 실행/업데이트 후 재실행 시험.
5. 프로필 분리, 사이트별 데이터 삭제, 백업/복원 기능 구현.

## 필수 수용 테스트

- 쿠키·LocalStorage·IndexedDB·Service Worker·CacheStorage 값을 만들고 재실행 후 값이 유지되는지 확인.
- 테스트용 로그인 쿠키가 서버에서 유효한 범위 내에서 복원되는지 확인.
- 잠긴 저장소만 복사한 실험에서 원문을 해독할 수 없는지 검증.
- `TEMP`, 프로필 경로, 브라우저 캐시, 크래시/로그 파일의 쓰기 경로를 확인.
- 갑작스러운 종료 시 안전한 복구가 가능한지 확인.
- 저장 실패 시 새 평문 프로필 생성이나 민감 데이터의 비보호 폴백이 발생하지 않는지 확인.

검증 결과를 `SECURITY_STATUS.md`에 실제 증거와 함께 기록한다.

## Phase 3 실험을 위한 환경 확인

현재 Windows에 Virtual Disk API와 BitLocker 명령이 존재한다. VeraCrypt 및 WinFsp는 확인한 기본 설치 경로와 레지스트리 키에서 찾지 못했다. VHD 연결 시 볼륨 관리 권한이 필요하므로, 실제 대상이 확인된 격리 VM에서 전용 빈 디스크 이미지의 생성·연결·암호화와 파일 쓰기 추적을 승인받아야 한다. Windows 호스트 설정과 데이터 볼륨은 변경하지 않았다. [Microsoft Virtual Disk API](https://learn.microsoft.com/windows/win32/api/virtdisk/nf-virtdisk-attachvirtualdisk)

### VirtualBox 실험 VM 구성

- 게스트 — x64 호스트 기준 Windows 11 Pro x64, vCPU 4개, RAM 8 GB, 동적 VDI 100 GB, UEFI·Secure Boot·가상 TPM 2.0. 호스트 자원이 빠듯하면 2 vCPU와 6 GB부터 시작한다. Pro는 VM 내부에서 BitLocker 데이터 볼륨을 관리하기 위해 선택한다. [Windows 11 시스템 요구 사항](https://learn.microsoft.com/windows/whats-new/windows-11-requirements) · [BitLocker 에디션](https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/) · [VirtualBox 설정](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/working-with-vms.html)
- 네트워크 — 설치·업데이트 및 도구 준비 시만 NAT, 포트 포워딩 없이 사용한다. 저장소 실험은 어댑터를 **Not attached**로 두고 같은 게스트의 루프백 서버를 쓴다. 두 VM 사이 DNS/프록시 실험 시 **Internal Network**의 같은 네트워크 이름을 선택하고 기본 게이트웨이를 설정하지 않는다. 공유 폴더·클립보드·드래그 앤 드롭·USB 전달은 비활성화한다. [VirtualBox 네트워크 모드](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/networkingdetails.html) · [VirtualBox 보안 가이드](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/Security.html)
- 스냅샷 — 완전 종료 상태에서 `00-windows-clean`(설치·업데이트 직후)과 `01-veil-tools-offline`(.NET·Node·WebView2·Rust·Procmon 및 소스 준비 직후)을 만든다. 첫 시험용 데이터는 두 기준점 이후에 생성한다. 이후 기준점이 필요하면 VHD를 잠그고 분리한 뒤 VM을 정상 종료한다. 실행 중 스냅샷의 메모리 상태에는 열린 키가 들어갈 수 있다. [VirtualBox 스냅샷](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/working-with-vms.html#snapshots)

VM이 준비되면 VM 내부에 VEIL 전용 VHDX를 만들고 게스트 시스템 디스크와 구분한다. 잠금 해제한 실험용 볼륨에 기존의 빈 테스트 폴더를 지정하고, 저장소 루트에서 시험한다.

```powershell
node tests/phase3/profile-routing.mjs --profile-parent 'V:\VEIL-Lab'
```

`--profile-parent`는 지정된 기존 폴더 아래 무작위 테스트 디렉터리를 만든다. 경로 시험은 WebView2가 컨트롤러 초기화 전에 보고한 UDF 문자열과 정상 재사용, 경로 오류 및 환경 변수 재정의를 검사한다. 실제 볼륨 식별과 접합점·재분석 지점, BitLocker 보호 상태, 볼륨 밖의 브라우저 호스트·하위 프로세스 파일 쓰기는 게스트에서 별도로 계측한다. 분석은 TEMP와 엔진 캐시·덤프 경로를 포함한다. [Microsoft Procmon](https://learn.microsoft.com/sysinternals/downloads/procmon)

### 2026-11-23 BitLocker VHDX 실측

- 격리된 `Veil-VM-TESTER`에서 전용 1 GiB VHDX의 NTFS 볼륨을 `V:`에 연결했다. BitLocker XTS-AES-256 전체 암호화 100% 및 보호 상태 `On` 확인 후 쓰기를 시작했다.
- 일반 사용자 WebView2 157.0.4341.60이 전용 UDF `V:\VEIL-Lab\restart`에 영구 쿠키, LocalStorage, IndexedDB, Service Worker, CacheStorage를 기록했다. 원본 잠금 후 프로필 열람이 차단됐다. 분리한 이미지의 사본은 원본과 SHA-256이 일치했고 합성 ASCII 마커는 발견되지 않았다.
- 원본을 연결하지 않은 상태로 사본을 연결했다. 잘못된 암호가 거부되고 올바른 임시 암호로 열린 사본에서, 같은 루프백 원점과 일반 사용자 계정으로 WebView2를 새로 실행해 다섯 종류의 사이트 데이터가 복원됐다. 사본은 다시 잠그고 분리했으며 일회성 암호는 폐기했다.
- Procmon 쓰기 추적에서 전용 프로필을 연 시험 PID 및 자식 PID의 성공한 드라이브 경로 `WriteFile`은 `V:` 1,824건, `C:` 1건이었다. `C:` 한 건은 의도적으로 저장한 테스트 결과 JSON이다. 동일한 실행 파일 이름으로 동작한 Windows 구성 요소의 별도 UDF 쓰기는 시험 PID 집계에서 제외했다.
- 게스트 `C:\VEIL-Project\.veil-dev`에 `bitlocker-before.json`, `bitlocker-after.json`, `bitlocker-write.pml`, `bitlocker-write.csv`, `bitlocker-scoped-outside-writes.json`을 보관한다. 단일 정상 프로필 쓰기 및 오프라인 사본 복원 시험이며 앱의 자동 잠금, 북마크·기록 저장, 강제 종료, 시스템 페이지 파일·덤프, 전체 파일 I/O 경로는 후속 검증 대상이다.

### 전체 데스크톱 상태의 잠금 시험

BitLocker 보호가 완료된 실험 볼륨에 빈 테스트 폴더를 만든 후 일반 사용자 창에서 실행한다.

```powershell
node tests/integration/desktop.mjs --profile-parent 'V:\VEIL-Lab' --lock-cycle
```

시험은 무작위 테스트 프로필에 WebView2 데이터와 `browser-state.json`을 생성하고 정상 종료한다. 출력한 증거 디렉터리의 `ready.json`을 확인한 뒤 관리자 창에서 검증 대상 VHDX를 잠그고, 실제 잠금을 확인한 뒤 그 증거 디렉터리에 빈 `locked` 파일을 만든다. 시험은 잠긴 경로에서 앱 시작이 거부되고 합성 원점으로 새 요청이 가지 않는지 확인한다. `locked-checked`가 생성되면 같은 볼륨을 잠금 해제한 후 `unlocked`를 만들어 재실행과 북마크·기록·탭·사이트 데이터 복원을 확인한다. 신호 파일은 실험 진행에만 쓰며 볼륨의 보호 상태는 BitLocker에서 따로 확인한다.

2026-11-23 실제 BitLocker VHDX 시험에서는 최초 실행의 탭 복원 단언이 실패했다. `browser-state.json`은 첫 저장본에 머물렀고 `.new` 파일이 남아 있었다. 같은 디렉터리에서 새 파일로 상태를 교체하도록 수정하고, 정상 종료 후 디스크 상태가 열린 세션과 일치하는지 시험에 추가했다. 재실행에서 `FullyEncrypted` / `ProtectionStatus=On`을 확인했으며, 잠금 중 시작은 `IOException`으로 거부됐다. 잠금 해제 후 탭과 선택 인덱스, 북마크, 방문 기록, 사이트 데이터 5종 복원 및 손상된 상태 파일 차단이 통과했다. 증거는 게스트 `.veil-dev/desktop-test-tmRNbj`에 있다. 앱 실행 자체의 보호 볼륨 강제와 자동 잠금은 별도 제품 통합 대상이다.

### BitLocker 볼륨의 강제 종료·잠금 조합 시험 (준비)

실행기는 두 옵션을 함께 받는다. 잠금 해제한 실험 볼륨의 빈 폴더를 지정해 일반 사용자 창에서 실행한다.

```powershell
node tests/integration/desktop.mjs --profile-parent 'V:\VEIL-Lab' --crash-cycle --lock-cycle
```

첫 기록 후 합성 임시 상태를 쓰는 브라우저 호스트를 강제로 종료하고, 마지막 정상 상태 보존과 임시 파일만 남은 프로필의 시작 차단을 확인한 다음 위의 잠금·해제 신호 절차를 진행한다. 2026-11-23 호스트에서 일반 디스크의 별도 상위 폴더로 `--profile-parent … --crash-cycle` 조합이 통과했다(`.veil-dev/desktop-test-RlmZGw`). BitLocker 볼륨에서의 실행은 아직 하지 않았다.

### 브라우저 시작 차단 검사 (ADR-0003 1단계)

[ADR-0003](decisions/ADR-0003-volume-broker.md)의 브라우저 쪽 검사를 [ProfileGate.cs](../apps/desktop/ProfileGate.cs)에 구현했다. 일반 실행, `--smoke`, `--probe` 모두 WebView2 환경을 만들기 전에 다음을 확인한다. 실패하면 `ProfileLocationException`(`IOException` 하위)으로 시작을 중단한다.

- 관리자 권한 토큰으로 실행되면 거부한다.
- 프로필 경로가 아직 없으면 가장 가까운 기존 상위 폴더를 먼저 검사한 뒤 생성한다. 접합점이 걸린 상위 폴더 아래에는 새 프로필 폴더를 만들지 않는다.
- 프로필과 모든 상위 폴더에서 재분석 지점(접합점·심볼릭 링크)을 거부한다. 네트워크·UNC 경로를 거부한다.
- 디렉터리 핸들의 최종 경로가 요청 경로와 같아야 한다. 이후 볼륨 GUID를 구한다.
- 볼륨 상태는 `IVolumeBroker`로 받는다. 브로커가 아직 없어 현재 모든 실행은 `UnmanagedDevelopmentVolume`을 쓴다. 이 구현은 보호 여부를 주장하지 않는다(`Protected=false`). `requireProtected` 검사는 코드에 있으나 제품 실행 경로에서 아직 켜지 않았다.

2026-11-23 호스트 결과(일반 사용자 권한):

- PASS — `node tests/phase3/profile-routing.mjs`. 프로필 자체가 접합점인 경우와 접합점 상위 폴더 아래 프로필 모두 종료 코드 1, `ProfileLocationException`, 대상 폴더 비어 있음, 합성 원점 요청 없음. 증거 `.veil-dev/profile-routing-8h1Fnq/evidence.json`.
- PASS — `node tests/integration/desktop.mjs`(`.veil-dev/desktop-test-w314hT`), `node tests/integration/desktop.mjs --crash-cycle`(`.veil-dev/desktop-test-bi4rrl`).
- NOT RUN — 관리자 권한 실행 거부(일반 사용자 셸에서는 재현할 수 없다), `subst`·심볼릭 링크·네트워크 경로, 격리 VM과 BitLocker 볼륨에서의 재실행, 브로커를 통한 보호 상태 조회.

### 볼륨 밖 쓰기 추적 (SEC-004)

- **대응** — 앱은 상태 파일을 읽은 뒤, 엔진을 시작하기 전에 프로세스의 `TEMP`/`TMP`를 프로필 아래 `temp` 폴더로 바꾼다. WebView2 하위 프로세스는 이 환경을 상속하는 것으로 예상한다. 폴더가 재분석 지점이면 시작을 거부하고, 시작과 종료 때 내용을 비운다. 거부된 프로필에는 폴더를 만들지 않는다.
- **분석기** — `tests/security/outside-writes.mjs`는 Procmon CSV에서 하네스가 시작한 프로세스와 모든 하위 프로세스를 따라간다. 성공한 `WriteFile`, 파일 크기 변경, 이름 변경 대상, 새 파일 생성과, 이 트리가 연 파일에 대한 System 프로세스의 페이징 쓰기(매핑된 쓰기)를 분류한다. 보호 볼륨과 하네스 증거 폴더 밖의 쓰기가 하나라도 있으면 실패한다.
- **절차** — 격리 VM에서만 실행한다. Procmon은 관리자 창(`tests/security/trace-writes.ps1 -Action Start -IsolatedVm`)에서, VEIL 하네스는 일반 사용자 창(`node tests/integration/desktop.mjs --profile-parent 'V:\VEIL-Lab' --crash-cycle`)에서 실행한 뒤 `-Action Stop -IsolatedVm`으로 내보내고 분석한다. 스크립트는 `-IsolatedVm` 없이, 또는 관리자 권한 없이는 실행을 거부하며 볼륨이 `FullyEncrypted`/보호 `On`인지 먼저 확인한다.
- **1차 VM 추적 (2026-11-23 13:00, 오프라인 VM)** — 무작위 일회성 암호로 만든 전용 1 GiB BitLocker VHDX(`FullyEncrypted`/`On`)에서 일반 사용자 토큰으로 `--crash-cycle` 하네스가 PASS했다. 하네스 아래 40개 프로세스의 쓰기 4,817건이 V:에 있었다. **볼륨 밖 누출 발견** — WebView2 브라우저 프로세스가 취소된 다운로드 8건의 본문을 `DownloadStarting` 전에 `C:\Users\<user>\Downloads\<GUID>.tmp`로 기록했다(시험 후 파일은 삭제됐으나 시스템 디스크의 빈 공간에 평문이 남을 수 있다). `C:\$LogFile`·`$Mft` 쓰기는 이 파일 생성의 부수 효과였다.
- **수정** — 엔진 프로필의 기본 다운로드 폴더(`CoreWebView2Profile.DefaultDownloadFolderPath`)를 프로필의 `temp\downloads`로 지정했다. 통합 시험이 여덟 유형 각각에서 이 경로가 프로필 안인지 확인한다.
- **2차 VM 추적 (13:05)** — 수정본으로 하네스 PASS, V: 쓰기 4,803건, Downloads의 `.tmp` 0건. TEMP·캐시·다운로드 임시 파일은 볼륨 밖에 쓰이지 않았다. **남은 볼륨 밖 쓰기** — WebView2 브라우저 프로세스가 Windows 작업 표시줄 점프 목록(`%APPDATA%\Microsoft\Windows\Recent\CustomDestinations\*.customDestinations-ms`)에 2개 파일, 5회 기록했다. 내용은 Edge의 "새 창"·"InPrivate" 작업이며 각 항목에 엔진 프로필 경로(`--user-data-dir="V:\VEIL-Lab\…\EBWebView"`)가 들어 있다. 방문 URL은 발견되지 않았지만 보호 프로필의 위치가 시스템 디스크에 남는다. 디렉터리 인덱스 갱신(`C:\` 등)과 `$LogFile`은 부수 효과로 분리했다.
- **점프 목록 차단 시도 (호스트, 2026-11-23)** — 호스트에서도 시험 실행마다 같은 파일(`2a2e0412b8ad04a2.customDestinations-ms`)이 다시 쓰이고 시험 프로필 경로가 들어 있음을 재현했다(일반 실행 5/5). WebView2 157.0.4341.60 런타임의 `msedge.dll`에는 Edge 점프 목록 코드(`jumplist_updater`, `jumplist_profiles`, `jumplisttopsites`, `jumplistrecentlyclosed`)가 있어, 더 오래 탐색하면 자주 가는 사이트·최근 닫은 탭 URL이 기록될 가능성이 있다. 다음 방법은 효과가 없어 코드에 남기지 않았다.
  - Chromium `--disable-custom-jumplist` — 브라우저 프로세스 명령줄에 전달됐지만 무시됨(3/3 기록). 이 문자열은 Edge 바이너리에 없다.
  - `--disable-features=msWorkspacesJumpList,TaskbarJumplistNewWindow,TaskbarJumplistRecentlyClosed,TaskbarJumplistTopSites` — 3/3 기록.
  - 엔진 환경의 `APPDATA`/`LOCALAPPDATA`를 프로필로 변경 — 셸이 실제 폴더에 기록(1/1).
  - `USERPROFILE`을 프로필로 변경 — 2회 중 1회 기록, 일관되지 않아 대책으로 쓰지 않음.
- **장시간 탐색 추적 (2026-11-23 13:36, 오프라인 VM)** — `tests/security/browse-session.mjs --runs 3`로 같은 프로필에서 세션 3회(회당 서로 다른 합성 페이지 30개, 3개 페이지 5회 재방문, 탭 8개 열고 닫기, 종료 전 20초 대기)를 일회성 BitLocker VHDX에서 일반 사용자 토큰으로 실행했다. 하네스 PASS, V: 쓰기 8,683건. 볼륨 밖 쓰기는 점프 목록 파일 2개(5회)뿐이었고, 점프 목록에는 프로필 경로만 있고 시험 URL·호스트·페이지 제목은 없었다. Downloads `.tmp`는 0건, 시험 볼륨은 삭제됐다. 호스트에서 같은 세션을 돌린 결과도 같았다. 증거는 게스트 `C:\VEIL-Trace-20261123-133611\.veil-dev`.
- **한계** — 시험 페이지는 루프백(`127.0.0.1`)이다. Edge의 자주 가는 사이트 목록이 루프백 주소를 제외할 수 있으므로, 실제 인터넷 사이트에서 URL이 기록되지 않는다고 결론 내리지 않는다. 오프라인 VM 정책상 외부 사이트 시험은 하지 않았다.
- **상태** — 앱 수준 차단 방법을 찾지 못했다. 관찰된 누출은 프로필 경로(보호 볼륨 위치)로 한정되며 **미해결**이다. 후보는 배포 정책(점프 목록·최근 항목 기록 끄기), Microsoft에 WebView2 호스트용 점프 목록 비활성화 옵션 요청, 더 긴 탐색 후 파일에 URL이 기록되는지 확인하는 추가 추적이다.
- **분석 기준 보정** — 대용량 CSV(최대 145 MB)를 스트리밍으로 처리하고, NTFS 메타데이터와 디렉터리 인덱스에 대한 System 페이징 쓰기를 누출 목록과 분리한다. Procmon 기본 필터로 수집했으며 내보내기는 완료까지 기다린다.
- **증거** — 게스트 `C:\VEIL-Trace\.veil-dev`(1차)와 `C:\VEIL-Trace-20261123-130538\.veil-dev`(2차)의 `write-trace\trace.pml`, `trace.csv`, `outside-writes.json`, `harness.log`. 시험 볼륨은 잠근 뒤 분리하고 이미지를 삭제했다.
- **범위 밖** — 충돌 덤프(WER), 페이지 파일·최대 절전 파일, Windows 검색 색인과 같은 OS 기록은 앱이 막을 수 없다. 시스템 드라이브 BitLocker, WER 로컬 덤프 정책 등 배포 정책으로 다룬다.
