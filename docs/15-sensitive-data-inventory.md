# 15. 민감 데이터 저장 위치와 OS 기록 정책

Gate A의 "민감 영구 데이터 목록과 저장 경로 확인", "임시 경로·로그·캐시·크래시 기록의 위험 범위"를 정리한다. 2026-11-23 WebView2 157.0.4341.60 개발 빌드에서 시험 프로필의 실제 파일 목록과 격리 VM Procmon 추적([SEC-004](05-encrypted-profile.md#볼륨-밖-쓰기-추적-sec-004))을 근거로 했다. 엔진 업데이트 때 다시 확인한다.

## 1. 프로필 안 (보호 볼륨에 저장)

제품 모드에서는 아래 전체가 BitLocker 보호 볼륨의 등록된 프로필 폴더(`ProfilePath`) 아래에 있다. 볼륨이 잠기면 함께 보호된다.

| 경로 (프로필 기준) | 내용 | 민감도 |
|---|---|---|
| `browser-state.json` | VEIL 북마크, 방문 기록, 열린 탭·창, 선택 탭 | 높음 |
| `temp\` | 앱·엔진 임시 파일(TEMP/TMP), `temp\downloads` 취소된 다운로드 조각. 시작·종료 시 비움 | 높음(일시) |
| `webview2\EBWebView\Default\Network\` | 쿠키, 인증 상태 | 매우 높음 |
| `…\Default\Local Storage`, `Session Storage`, `IndexedDB`, `WebStorage`, `Service Worker`, `blob_storage`, `Shared Dictionary` | 사이트 데이터 | 높음 |
| `…\Default\History`, `Top Sites`, `Favicons`, `Visited Links`, `Network Action Predictor`, `DIPS`, `Sessions`, `Sessions_Encrypted`, `EdgeSessions` | 엔진 방문 기록·자주 가는 사이트·세션 복원 | 높음 |
| `…\Default\Cache`, `Code Cache`, `GPUCache`, `DawnGraphiteCache`, `DawnWebGPUCache` | 페이지·스크립트·그래픽 캐시(페이지 내용 조각) | 높음 |
| `…\Default\Login Data`, `Login Data For Account`, `Web Data` | 비밀번호·자동 완성 저장소. VEIL은 자동 저장을 끄지만 파일은 생성된다 | 높음 |
| `…\Default\Preferences`, `Secure Preferences`, `JumpListIconsRecentWorkspacesV2` 등 | 엔진 설정, 점프 목록 아이콘 캐시 | 중간 |
| `webview2\EBWebView\Crashpad` | 엔진 충돌 덤프(메모리 내용 포함 가능) | 높음 |
| `webview2\EBWebView\Local State`, `Variations*`, `ShaderCache` 등 | 엔진 전역 상태·구성 요소 | 낮음 |

## 2. 프로필 밖 (시스템 디스크, 앱이 막지 못한 것)

| 위치 | 내용 | 상태 |
|---|---|---|
| `%APPDATA%\Microsoft\Windows\Recent\CustomDestinations\*.customDestinations-ms` | 엔진이 쓰는 작업 표시줄 점프 목록. 엔진 프로필 경로 포함. 루프백 장시간 탐색에서 URL·제목은 없었음 | **미해결** — 앱 수준 차단 실패([기록](05-encrypted-profile.md#볼륨-밖-쓰기-추적-sec-004)) |
| `%USERPROFILE%\Downloads\<GUID>.tmp` | 취소 전 다운로드 본문 | 수정됨 — 기본 다운로드 폴더를 프로필로 이동, VM 재추적으로 확인 |
| NTFS `$LogFile`·`$Mft`, 폴더 인덱스 | 위 파일 생성의 부수 효과. 파일 내용 없음 | 정보 |
| 브로커 서비스 로그·이벤트 | 볼륨 작업 결과. 암호 미기록(ADR-0003) | 설계상 허용 |

## 3. OS가 기록할 수 있는 것 (앱 범위 밖, 배포 정책)

| 기록 | 위험 | 정책 |
|---|---|---|
| 페이지 파일(`pagefile.sys`), 스왑 | 잠금 해제 중 엔진·앱 메모리(페이지 내용, 쿠키) | **시스템 드라이브 BitLocker를 설치 조건으로 요구**한다. 미충족 시 설치 프로그램이 경고하고 정식 보호 모드를 활성화하지 않는다 |
| 최대 절전 파일(`hiberfil.sys`) | 메모리 전체 | 위와 같음. 절전 진입 알림 시 보호 브라우저는 이미 종료·재잠금한다(SEC-003) |
| Windows 오류 보고(WER) 덤프 | VEIL 호스트 프로세스 충돌 시 메모리 덤프가 `%LOCALAPPDATA%\CrashDumps`·`%ProgramData%\Microsoft\Windows\WER`에 남을 수 있음 | 설치 프로그램에서 `WerAddExcludedApplication`으로 VEIL 실행 파일의 덤프 수집을 제외하는 것을 다음 배포 단계에서 구현·검증한다. 엔진 덤프는 프로필 안 `Crashpad`에 남는다 |
| Windows 검색 색인 | 보호 볼륨이 색인 대상이면 파일 내용 | 시험 VHDX는 기본 색인 대상이 아니다. 설치 시 볼륨을 색인에서 제외하는지 배포 단계에서 확인한다 |
| 점프 목록·최근 항목 | 위 2절 | 배포 정책 후보: 사용자 정책으로 최근 항목 추적 끄기. 효과는 VM에서 검증 필요 |

## 4. 한계

- 목록은 개발 빌드의 시험 프로필과 합성 사이트로 만든 것이다. 실제 사이트 로그인·미디어·확장 기능 사용 시 새 저장소가 생길 수 있다.
- Procmon 기본 필터로 수집했다. 커널 드라이버 수준 기록, 메모리 매핑 파일의 지연 쓰기 일부는 분석기가 System 프로세스 페이징 쓰기로만 추정한다.
- 시스템 드라이브 암호화·WER 제외·색인 제외는 배포(Phase 7) 항목이며 아직 구현·검증하지 않았다.
