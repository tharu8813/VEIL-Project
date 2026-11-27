# CEF 보호 프로필 사전 검사 VM 준비 런북

상태: 준비 도구 및 검토 절차. 실제 서명, 설치, 런타임 실행 NOT RUN. 이 문서는 배포 승인 또는 자동 설치 지시가 아니다.

## 1. 별도 검증 환경

- 폐기 가능한 Windows x64 VM과 복원 가능한 스냅샷, BitLocker 지원 에디션, 관리자 계정 및 별도 일반 사용자 계정.
- 실사용 VEIL 설치, 실사용 브라우저 데이터, 실제 비밀번호/복구 코드 재사용 금지. 시험 VHDX만 사용한다.
- 신뢰된 관리자 관리 소스의 정확한 커밋을 확인한다. .NET 10 SDK, Windows Desktop Runtime, CEF 빌드 도구와 신뢰된 코드 서명 인증서를 준비한다. 인증서 지문은 후보 파일과 별개의 신뢰 경로에서 확인한다.
- 제품의 기존 Release 패키지 스크립트는 변경하지 않는다.

## 2. 후보 빌드 및 서명 준비

신뢰된 깨끗한 checkout에서 다음을 별도로 빌드한다. 잠금 의존성 복원이 실패하면 중단한다.

~~~powershell
dotnet restore apps/desktop/Veil.Desktop.csproj --locked-mode
dotnet build apps/desktop/Veil.Desktop.csproj -c CefPreflight --no-restore
dotnet restore apps/broker/Veil.VolumeBroker/Veil.VolumeBroker.csproj --locked-mode
dotnet build apps/broker/Veil.VolumeBroker/Veil.VolumeBroker.csproj -c Release --no-restore
.\scripts\build-cef-smoke.ps1 -BuildProductCandidate
~~~

각 명령의 종료 코드를 확인한다. CefPreflight 출력은 일반 Debug/Release와 구분한다. 관리자 제한 ACL의 별도 후보 디렉터리에서 공식 서명 절차를 사용하여 데스크톱, 브로커 및 CEF 후보 EXE/DLL을 서명한다. 실제 신뢰 인증서 및 타임스탬프 정책이 필요하다. 서명 후 해시를 계산하며 서명 전에 계산한 해시는 등록에 쓰지 않는다. 서명 여부만으로 전체 관리 DLL 또는 의존성의 신뢰를 주장하지 않는다.

## 3. 비활성 CEF 런타임 등록 계획 생성

관리자 Windows PowerShell 5.1 x64에서 서명된 CEF 후보와 별도의 빈 제한 ACL 증거 디렉터리를 준비한다. 기존 도구는 서명된 후보를 검증하고 CMS 증거를 발행한 뒤 고정 별도 staging에 복사한다.

~~~powershell
# CandidateRoot, EvidenceDirectory, SignerThumbprint, SourceCommit은
# 운영자가 별도로 검증한 실제 값을 사용한다.
.\scripts\sign-cef-candidate.ps1 -CandidateRoot $candidate -EvidenceDirectory $evidence -SignerThumbprint $pin -SourceCommit $commit -Sign
.\scripts\stage-cef-candidate.ps1 -CandidateRoot $candidate -EvidenceDirectory $evidence -SignerThumbprint $pin -SourceCommit $commit
$runtimePlan = .\scripts\plan-cef-runtime-pins.ps1 -EvidenceDirectory $evidence -SignerThumbprint $pin -SourceCommit $commit
$runtimePlan | ConvertFrom-Json | Select-Object kind,format,architecture,sourceCommit,startupEnabled
~~~

계획은 원본 서명 증거와 staging 파일을 다시 대조하며 startupEnabled=false인 JSON만 출력한다. 설치/등록 파일로 직접 리다이렉트하지 않는다. 계획 JSON은 자체 서명 증거가 아니며 원본 CMS 및 정확한 후보 파일을 계속 보존한다.

## 4. 전체 검증용 패키지 서명 증거

별도 관리자 제한 ACL payload에 Desktop(CefPreflight 구성), Broker(Release 구성), Cef(서명된 후보)만 둔다. 각각의 EXE 및 VEIL DLL을 서명하고 CEF 후보의 원본 CMS 증거를 별도로 보존한다. 관리 DLL의 ProductName은 CefPreflight 전용 마커와 일치해야 한다. 이 마커는 빌드 혼입을 탐지하기 위한 것으로 바이너리 동작이나 빌드 출처의 독립 증명이 아니다.

~~~powershell
.\scripts\check-cef-preflight-package.ps1 -PayloadRoot $payload -CefEvidenceDirectory $evidence -PackageEvidenceDirectory $packageEvidence -SignerThumbprint $pin -SourceCommit $commit -Sign
~~~

새 별도 CMS 증거에는 전체 파일 크기 및 SHA-256이 포함된다. 기존 제품 manifest와 형식이 다르며 installable=false 및 startupEnabled=false만 허용한다. 패키지 검증은 파일을 실행하거나 설치하지 않는다. 전체 payload의 안전한 게시 및 복구 절차는 여전히 미구현이다.

## 5. 전체 패키지 사전 스테이징

깨끗한 폐기용 VM에서 기존 VEIL 설치 디렉터리·등록·브로커 서비스가 없는 경우에만 다음 준비를 수행할 수 있다.

~~~powershell
$stagePlan = .\scripts\stage-cef-preflight-package.ps1 -PayloadRoot $payload -CefEvidenceDirectory $evidence -PackageEvidenceDirectory $packageEvidence -SignerThumbprint $pin -SourceCommit $commit
~~~

고정 Program Files\VEIL.cef-preflight-staging 에 payload 및 양쪽 CMS 증거를 별도로 복사하고 원본·복사본을 다시 검증한다. 등록 예정 데스크톱/브로커/CEF 이미지 해시와 런타임 핀 계획을 출력한다. 오류 또는 기존 staging 발견 시 중단하고 증거를 보존한다. 이 도구는 설치 또는 등록을 수행하지 않으며 출력만으로 등록 권한을 부여하지 않는다.

## 6. 등록 초안 준비

반드시 폐기용 시험 VHDX와 별도 일반 사용자 SID를 사용한다. 기존에 만든 서로 다른 primary/recovery 보호기 GUID를 명시한다. 관리자 전용 시험 이미지 디렉터리는 Program Files 바로 아래에 둔다. 기존 복구 확인 루틴으로 현재 암호와 외부 복구 백업을 대화형 확인하고 재잠금·분리한다. 복구 비밀번호는 인자나 JSON에 넣지 않는다.

~~~powershell
.\scripts\prepare-cef-preflight-registration.ps1 -SignerThumbprint $pin -SourceCommit $commit -ImagePath $testImage -ProfilePath $testProfile -VolumeGuid $volumeGuid -ProtectorId $primaryId -RecoveryProtectorId $recoveryId -UserSid $testUserSid
~~~

고정 Program Files\VEIL.cef-preflight-registration 에 broker.json, client.json, cef-runtime-pins.json을 새 파일로 기록하고 flush 및 재조회한 뒤 preparation.json(각 등록 파일 해시, 소스 커밋, 세 이미지 핀, prepared-only 상태)을 마지막에 작성한다. 기존/부분 초안은 덮어쓰지 않는다. 기록은 설치 성공 또는 복구 완료를 뜻하지 않는다. 시험 프로필 경로와 사용자 권한의 실측 및 초안 재검증/게시 트랜잭션은 다음 단계이며 전원 차단 내구성은 NOT RUN이다.

## 7. 등록 초안 읽기 전용 재검증

동일한 시험 환경과 별도로 확인한 볼륨·보호기·사용자 입력을 사용한다. 등록 초안의 내용을 그대로 입력으로 신뢰하지 않는다.

~~~powershell
.\scripts\check-cef-preflight-registration.ps1 -SignerThumbprint $pin -SourceCommit $commit -ImagePath $testImage -ProfilePath $testProfile -VolumeGuid $volumeGuid -ProtectorId $primaryId -RecoveryProtectorId $recoveryId -UserSid $testUserSid
~~~

검증된 staging과 독립 입력으로 세 등록 JSON의 원래 바이트를 재구성하고 preparation.json의 정확한 파일 목록·해시·상태를 대조한다. 추가·누락·변조된 파일, 다른 독립 입력, 새 해시를 붙인 잘못된 등록, 설치 흔적, 연결된 시험 볼륨은 모두 거부한다. 부분 초안은 자동 수정·삭제하지 않는다. 이 결과는 읽기 전용 일관성 검사이며, 저널은 서명된 증거 또는 복구 자격 증명 검증의 독립 증명이 아니다. 실제 게시 직전에는 시험 볼륨과 복구 자격 증명을 다시 검증해야 한다.

## 8. 검증 전용 파일 게시

기존 설치와 서비스가 전혀 없는 폐기용 VM에서만 명시적으로 실행한다. 실제 복구 자격 증명 및 보호기 확인, 재잠금·분리 이후 별도 pending 디렉터리에 전체 파일을 복사하고 재검증한다. 기존 스테이징과 등록 초안을 보존한다.

~~~powershell
.\scripts\publish-cef-preflight-package.ps1 -DisposableVm -SignerThumbprint $pin -SourceCommit $commit -ImagePath $testImage -ProfilePath $testProfile -VolumeGuid $volumeGuid -ProtectorId $primaryId -RecoveryProtectorId $recoveryId -UserSid $testUserSid
~~~

Program Files와 ProgramData의 각각 동일 부모 안에서 pending 디렉터리를 최종 VEIL 경로로 이동한다. 두 이동은 하나의 원자적 작업이 아니다. 별도 Program Files\VEIL.cef-preflight-publication 에 01-intent, 02-copies-verified, 03-binaries-published, 04-files-published 저널을 순서대로 기록한다. 마지막 상태는 files-published-only이며 서비스 등록/시작을 뜻하지 않는다. 실패 또는 기존 경로 발견 시 자동 삭제·덮어쓰기·재시도를 금지하고 증거를 보존한다. 실제 Windows ACL, 전원 차단, 경합 및 이동 내구성은 NOT RUN이다.

## 9. 게시 중단 상태 확인 및 명시적 재개

동일한 독립 입력과 폐기용 VM 확인 옵션으로 먼저 상태만 조회한다. 아래 명령은 서비스가 없고 시험 볼륨이 분리되어 있을 때만 동작한다.

~~~powershell
.\scripts\recover-cef-preflight-publication.ps1 -DisposableVm -SignerThumbprint $pin -SourceCommit $commit -ImagePath $testImage -ProfilePath $testProfile -VolumeGuid $volumeGuid -ProtectorId $primaryId -RecoveryProtectorId $recoveryId -UserSid $testUserSid
~~~

- incomplete-copies: 복사 검증 기록이 없다. 재개 금지, 원본 및 부분 상태를 보존하고 깨끗한 VM 스냅샷으로 복귀한다.
- publish-binaries / record-binaries / publish-registration / record-completion: 정확한 저널 prefix, 서명된 인벤토리, 등록 초안, 실제 디렉터리 배치가 모두 일치할 때만 재개 가능.
- files-published-only: 모든 게시 기록 및 실제 파일 일치. 서비스 등록이나 시작은 아직 하지 않는다.

재개하려면 위 명령에 -Resume을 추가한다. 기존 복구 자격 증명과 재잠금/분리를 다시 확인하고 매 이동 또는 저널 기록 전에 전체 증거를 재검증한다. 알 수 없는 상태·변조·추가 기록·중복 디렉터리·서비스 존재·볼륨 연결은 거부한다. 파일이나 기록을 삭제/덮어쓰지 않는다. 한 명의 관리자가 다른 설치 도구와 동시에 실행하지 않는 폐기용 VM 절차이며, 전원 차단 및 동시성의 실제 내구성은 미검증이다.

## 10. 비활성 브로커 서비스 등록

files-published-only 상태에서만 폐기용 VM 확인 옵션과 별도 확인한 입력으로 실행한다. 전체 파일 게시와 시험 볼륨 복구 자격 증명을 재확인한다.

~~~powershell
.\scripts\register-cef-preflight-service.ps1 -DisposableVm -SignerThumbprint $pin -SourceCommit $commit -ImagePath $testImage -ProfilePath $testProfile -VolumeGuid $volumeGuid -ProtectorId $primaryId -RecoveryProtectorId $recoveryId -UserSid $testUserSid
~~~

SCM에 고정 경로의 Veil.VolumeBroker를 LocalSystem / Disabled / Stopped 상태로 생성하고 SYSTEM·Administrators 관리 및 인증 사용자 조회 전용 DACL을 설정·재조회한다. 별도 Program Files\VEIL.cef-preflight-service 에 등록 intent와 registered-disabled 기록을 남긴다. 등록 중 실패 시 서비스나 파일을 삭제하지 않으며 기존/불명확한 SCM 등록을 재사용하지 않는다. 실제 SCM DACL roundtrip, Windows 서비스 등록 및 권한 시험은 NOT RUN. 등록 완료 기록은 서비스를 시작할 권한이나 CEF 실행 성공을 의미하지 않는다.

## 11. 비활성 등록 확인 및 일회성 서비스 시작/정지 시험

동일 독립 입력으로 등록 저널의 정확한 바이트, 게시 증거, 서비스 경로·계정·상태·DACL을 읽기 전용 확인한다.

~~~powershell
.\scripts\test-cef-preflight-service.ps1 -DisposableVm -SignerThumbprint $pin -SourceCommit $commit -ImagePath $testImage -ProfilePath $testProfile -VolumeGuid $volumeGuid -ProtectorId $primaryId -RecoveryProtectorId $recoveryId -UserSid $testUserSid
~~~

명시적 -RunProbe를 추가하면 복구 자격 증명을 다시 확인한 뒤 서비스 probe intent를 만들고 Manual 전환, SCM Running 및 분리 상태를 확인한다. 정상·오류 모두 finally에서 정확한 서비스의 Disabled 전환 및 정상 Stop, 분리 상태를 확인한다. 모든 검증이 통과해야 running-then-stopped 기록을 남긴다. SCM Running 관측은 실제 일반 사용자 브라우저 연결 검증이 아니다. 프로세스 강제 종료 및 강제 볼륨 분리 금지. 전원 차단으로 finally를 실행하지 못하거나 서비스 정지/분리 확인 실패 시 기존 상태를 보존하고 관리자가 VM을 검사해야 한다. 자동 재개 없음. 실제 SCM/BitLocker 시험은 NOT RUN.

## 12. 일반 사용자 보호 프로필 사전 검사 세션

위 서비스 시작/정지 시험을 통과하고 비활성 상태로 복귀한 폐기용 VM에서만 진행한다. 관리자 대화형 데스크톱과 별도 활성화된 로컬 일반 사용자 계정이 필요하다. 표준 계정의 Windows 로그인 암호를 Get-Credential로 받으며 등록된 SID와 비교한다. 관리자 그룹 직접 구성원과 현재 관리자 계정은 거부한다. 운영자는 간접 관리자 권한도 없는 계정을 준비해야 한다. Windows Secondary Logon 및 대화형 자격 증명 실행 호환성은 실제 VM에서 확인해야 한다.

~~~powershell
.\scripts\test-cef-protected-session.ps1 -DisposableVm -RunProtectedSession -SignerThumbprint $pin -SourceCommit $commit -ImagePath $testImage -ProfilePath $testProfile -VolumeGuid $volumeGuid -ProtectorId $primaryId -RecoveryProtectorId $recoveryId -UserSid $testUserSid
~~~

등록 및 이전 서비스 시험 저널을 다시 검증하고 복구 자격 증명을 확인한 뒤 수동 시작한 서비스에 연결한다. 관리자가 서명된 고정 Desktop EXE를 지정된 표준 계정 자격 증명으로 --cef-protected-preflight 인자만 사용하여 실행한다. 브라우저 자체가 상승 권한을 거부하고 브로커가 등록 SID를 확인한다. 앱의 시험용 볼륨 암호 입력창에만 볼륨 암호를 입력한다. Windows 로그인 암호와 볼륨 암호는 별개이며 파일, 명령행 또는 로그에 기록하지 않는다.

관리자 측 Process.Start가 반환한 프로세스 핸들에서 Win32 이미지 경로, 등록 SID, 동일한 비영(0이 아닌) 세션, medium integrity, elevation/default 및 관리자 그룹 부재를 검사한다. 검사에 성공한 동일 프로세스 객체에서 최대 5분 동안 종료를 관측한다. PID·세션·tokenVerified 관측 결과를 별도 저널에 남긴다. 시작 후 토큰 스냅샷이며 suspended 실행 전 검증이나 전체 실행 동안의 토큰 고정 증명은 아니다. 종료 코드 29, 동일 브로커 PID 및 볼륨 분리를 확인한 뒤 서비스 Disabled/Stopped, 재분리 및 등록 재검증을 요구한다. 별도 보호 세션 저널에 시험 결과를 기록한다. 1/2, 다른 코드, timeout, 브로커 교체 또는 분리 실패는 성공이 아니다. Timeout 시 브라우저 강제 종료 없이 서비스 정상 정리를 시도하므로 시험 창을 닫고 VM을 확인해야 한다. 이 절차는 독립 OS write trace 또는 런타임 전체 권한 불변성의 증명이 아니며 실제 VM에서 관측해야 한다. 실제 Windows/BitLocker/CEF 시험은 NOT RUN이다.

## 13. 보호 세션 결과 읽기 전용 재검증

~~~powershell
.\scripts\check-cef-protected-session.ps1 -DisposableVm -SignerThumbprint $pin -SourceCommit $commit -ImagePath $testImage -ProfilePath $testProfile -VolumeGuid $volumeGuid -ProtectorId $primaryId -RecoveryProtectorId $recoveryId -UserSid $testUserSid
~~~

전체 서명 패키지, 등록 및 게시 저널, 이전 서비스 전용 시험, 현재 Disabled/Stopped SCM 설정 및 DACL과 분리된 시험 VHDX를 다시 확인한다. 보호 세션의 네 기록을 정확한 바이트로 재구성하고 실제 관측 PID/세션/브로커 PID와 종료 코드 29, tokenVerified=true, 최종 정리 기록에 포함된 관측 SHA-256을 대조한다. 누락/추가/변조 또는 다른 등록 기록은 거부한다. 서비스 시작, 볼륨 연결, 비밀번호 요청 및 기록 수정은 하지 않는다. 이전 관측 해시가 없는 보호 세션 기록은 인정하지 않는다. 이 결과는 관리자 수집 기록의 일관성 확인이며 독립적인 과거 실행 증명, OS 기록 추적 또는 CEF 초기화/제품 활성화 허가는 아니다.

## 14. 남은 차단점

관리자와 일반 사용자가 분리된 실제 폐기용 VM에서 서명, 전체 설치, 서비스 등록, 일반 사용자 사전 검사를 수행하고 재잠금·분리를 실측해야 한다. 기존 제품 설치 또는 실사용 데이터를 변경하지 않는다. 종료 코드 29는 보호된 비활성 자식 사전 검사와 정리를 뜻할 뿐 실제 CEF 초기화나 브라우징 성공이 아니다. 실제 CEF 런타임 활성화, OS 기록 추적, 잠금/절전 및 출시 게이트는 별도 검증한다.

### 서명 증거 결합 (등록 준비 형식 2)

등록 준비 및 게시 intent는 이제 세 실행 이미지뿐 아니라 네 원본 서명 증거의 SHA-256과 서명자 지문에 결합한다. 동일 커밋/서명자라도 DLL 또는 증거가 다른 패키지는 기존 준비 기록으로 게시할 수 없다. 형식 1의 준비/게시 기록은 새 검증기로 인정하지 않는다. 기존 시험 자료를 수정하지 말고 깨끗한 폐기용 VM 스냅샷에서 staging 및 등록 준비부터 다시 진행한다. 실제 VM과 동시 관리자 실행은 미검증이다.
