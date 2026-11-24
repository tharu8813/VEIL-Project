# 18. Windows 설치 및 업데이트 1차 구현

상태: 내부용 설치/업데이트 스크립트 구현, **NOT RUN**. 안정 버전 출시 승인 아님.

## 현재 지원 범위

- Windows PowerShell 5.1 x64, 관리자 유지보수 작업. 데스크톱 앱은 일반 사용자 실행.
- 기존 BitLocker VHDX 및 확인된 복구 보호기 등록을 재사용한다. 설치 전 볼륨 준비는 별도 기존 절차를 따른다.
- 신뢰하는 관리자 관리 코드 서명 인증서가 필요하다. 인증서 지문은 패키지가 아닌 별도 신뢰 경로에서 제공한다. 공식 인증서, 서명 서비스, 업데이트 서버는 아직 마련되지 않았다.
- `build-package.ps1`: 잠금된 의존성으로 Release 빌드, Rust vault 포함, 세 실행 파일 Authenticode 서명, 모든 파일의 SHA-256/길이 목록과 분리 CMS 서명 생성. 내부 채널의 0.x 개발 버전 및 허용된 사전 릴리스만 지원한다(아래 버전 정책 참조).
- `package-maintenance.ps1`: 신뢰된 로컬 관리자 스크립트에서 설치/업데이트/제거/중단 복구. 패키지의 스크립트는 실행하지 않는다. 입력 폴더의 모든 파일을 제한된 staging에 복사한 뒤 정확한 파일 목록 및 서명/해시를 검증한다.
- .NET 10 Desktop Runtime x64 및 WebView2 Evergreen은 별도로 검증된 공식 설치 경로로 준비한다. 자체 포함 런타임과 EXE/MSI GUI 설치 프로그램, 시작 메뉴 등록, 자동 업데이트 다운로드는 후속 단계다.
- MPL 소스 커밋 및 LICENSE/NOTICE 포함. `scripts/third-party.mjs`로 `THIRD-PARTY-NOTICES.md`와 CycloneDX `sbom.cdx.json`을 생성해 Desktop 폴더에 넣는다. 허용 목록 밖 라이선스가 있으면 빌드가 실패한다. 재배포 법률 검토는 공개 출시 전 별도로 필요하다.
- 버전: `0.x` 개발 패키지 또는 `1.0.0-alpha.N`·`beta.N`·`rc.N` 사전 릴리스만 허용하고 안정 1.x+는 거부한다. Windows 파일 버전과 업데이트 순서는 [ADR-0010](decisions/ADR-0010-internal-alpha.md#2-버전)의 매핑(`alpha.N → x.y.z.N` 등)을 쓴다.

## 업데이트 동작

1. 기존 및 새 패키지의 인증된 manifest와 등록 클라이언트 SHA-256을 확인한다. 기존 manifest 없는 레거시 설치는 자동 갱신하지 않는다.
2. 모든 VEIL 창 및 금고 작업을 정상 종료하고 VHDX가 분리된 상태여야 한다. 강제 프로세스 종료하지 않는다.
3. 같은 profileFormat, 더 높은 숫자 버전만 허용. 기존 등록 파일 백업 후 서비스 시작 유형 Disabled 및 정지.
4. 같은 Program Files 볼륨에서 기존 바이너리 트리를 보존하고 새 트리를 게시한다. 전체 패키지를 재검증하고 broker.json의 clientSha256만 원자적으로 변경한다.
5. 활성화 전 commit 표시를 기록한다. 커밋 전 실패는 기존 트리/등록 복원을 시도한다. 커밋 후에는 새 버전이 프로필을 열었을 가능성이 있으므로 자동 다운그레이드하지 않는다.
6. 서비스 시작 및 정리. 장애 후 `-RecoverInterruptedUpdate`는 저널에 따라 커밋 전 복원 또는 커밋 후 검증/재활성화를 수행한다. 복구 자체가 실패하면 중단하고 관리자 점검을 요구한다. 전원 차단 내구성 및 Windows rename/ACL은 VM에서 검증해야 한다.

## 명령 예시 (아직 실행하지 않음)

```powershell
# 관리자 소유의 신뢰된 소스/유지보수 도구에서 실행. 실제 인증서 및 타임스탬프 URL 필요.
.\scripts\build-package.ps1 -Version 1.0.0-alpha.1 -OutputDirectory C:\VEIL-package -SignerThumbprint <PIN> -SignTool <SIGNTOOL> -TimestampUrl <HTTPS_TIMESTAMP>
.\scripts\package-maintenance.ps1 -Update -Package C:\VEIL-package -SignerThumbprint <PIN>
.\scripts\package-maintenance.ps1 -RecoverInterruptedUpdate -SignerThumbprint <PIN>
.\scripts\package-maintenance.ps1 -Remove -SignerThumbprint <PIN>
```

최초 설치는 -Install과 Package, ImagePath, ProfilePath, VolumeGuid, ProtectorId, UserSid, 필요 시 RecoveryProtectorId를 지정한다. 기존 broker-service.ps1 복구 키 확인 절차를 그대로 사용한다. 유지보수 스크립트 자체의 배포/서명 및 안전한 관리자 실행은 별도 신뢰 경로가 필요하다. 레거시 스크립트와 동시 실행 금지.

제거는 브로커 서비스 및 실행 파일만 제거하고 VHDX, 프로필, ProgramData 등록(복구 보호기 ID 포함)을 보존한다. 재설치/재등록 자동화는 아직 없으며 [Alpha 관리자 런북](37-alpha-admin-runbook.md)의 검증 대상이다. 기존 broker-service.ps1 -Remove는 레거시 정리 경로로 유지되며 등록까지 제거하므로 신규 패키지 유지보수에는 사용하지 않는다.

## 검증 및 남은 작업

- [ ] 잠금 restore/publish, 실제 서명 및 CMS 체인, 모든 파일 검증, 추가/누락/변조 파일, 잘못된 인증서, 경로/ACL/링크/동시 실행 검증
- [ ] 실제 VM 신규 설치, 복구 보호기 확인, 서비스 자동 시작, 일반 사용자 브라우저, 설치된 런타임/파일 버전 일치
- [ ] 정상 업데이트, 동일/하위 버전 거부, 프로필 포맷 변경 거부, 기존 데이터/등록/키 유지
- [ ] 단계별 장애 주입 및 전원 차단, 서비스 실패, 디스크 부족, 파일 사용 중, journal/rollback 복구 및 재실행, ACL 및 디렉터리 rename 내구성
- [ ] 제거 및 보존 데이터의 실제 재등록/복원, 서명된 설치 UI·업데이트 서버/키 회전/보안 패치 정책
- [ ] Gate A~E, 라이선스/공급망/서명 검증 이후에만 공개 배포

이 문서 및 코드 추가는 공개 출시, 실제 서명, 실제 설치, 업데이트 성공 또는 보안 게이트 통과를 의미하지 않는다.

복구 중 다시 중단된 경우 기존 트리와 등록 백업의 SHA-256 일치를 확인한 뒤 등록 복원을 재시도한다. 최초 설치 중단 또는 손상된 저널은 자동 복구를 보장하지 않으며 관리자 점검이 필요하다.
