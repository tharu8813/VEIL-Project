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
