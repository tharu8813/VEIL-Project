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

## 6. 등록 전 차단점

전체 설치 트리의 서명 인벤토리 검증 도구는 마련했지만 CefPreflight 데스크톱의 pin, 브로커 pin 및 CEF 런타임 계획을 안전하게 함께 게시하는 전용 설치/복구 트랜잭션이 없다. 따라서 기존 설치 파일이나 broker.json/client.json을 수동 덮어쓰지 않는다. 기존 broker-service.ps1 및 제품 package-maintenance.ps1은 CEF 검증 빌드의 등록 도구가 아니다. 기존 staging을 설치 트리로 이동하거나 서비스 ACL/서명 검사를 우회하지 않는다.

후속 구현은 깨끗한 폐기용 VM에서만 기존 설치 부재, 전체 서명 증거, 관리자 ACL, 단일 등록 사용자/볼륨, VHDX 분리, 실패 복구 및 정상 분리 확인을 갖춘 전용 설치 트랜잭션을 마련해야 한다. 그 뒤에만 고정 설치 경로의 일반 사용자 검증 명령을 실행한다.

~~~powershell
# 전용 등록 절차 완료 후에만 실행. 현재 실행하지 않는다.
$p = Start-Process -FilePath "$env:ProgramFiles\VEIL\Desktop\Veil.Desktop.exe" -ArgumentList '--cef-protected-preflight' -PassThru -Wait
$p.ExitCode
~~~

29는 등록된 보호 프로필의 비활성 사전 검사 및 재잠금 확인을 뜻하며 실제 CEF 브라우징 성공이 아니다. 1/2 및 기타 종료 코드는 통과가 아니다. 실제 설치 서비스, 보호 볼륨, 잠금/절전, CEF 초기화, 출시 게이트는 별도로 검증한다.
