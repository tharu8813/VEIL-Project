# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = 'Stop'
# Synthetic bytes and ephemeral CMS key; no trust-store changes or real installation.
$dir = (Resolve-Path (Join-Path $PSScriptRoot '..\..\scripts')).Path
foreach ($name in @('cef-candidate-evidence.ps1','cef-preflight-package.ps1')) {
    $source = [IO.File]::ReadAllText((Join-Path $dir $name))
    $source = $source.Replace('$PSScriptRoot',("'" + $dir.Replace("'","''") + "'"))
    if ($name -eq 'cef-preflight-package.ps1') {
        $source = $source.Replace(". (Join-Path '$dir' 'cef-candidate-evidence.ps1')",'')
    }
    if (-not $source.Contains('$cms.CheckSignature($false)')) { throw 'CMS contract changed.' }
    . ([scriptblock]::Create($source.Replace('$cms.CheckSignature($false)','$cms.CheckSignature($true)')))
}
$stageSource = [IO.File]::ReadAllText((Join-Path $dir 'cef-preflight-staging.ps1'))
$import = ". (Join-Path " + '$PSScriptRoot' + " 'cef-preflight-package.ps1')"
if (-not $stageSource.Contains($import)) { throw 'Staging import changed.' }
. ([scriptblock]::Create($stageSource.Replace($import,'')))
$registration = [IO.File]::ReadAllText((Join-Path $dir 'cef-preflight-registration.ps1'))
foreach ($name in @('cef-preflight-staging.ps1','cef-registration-plan.ps1')) {
    $import = ". (Join-Path " + '$PSScriptRoot' + " '$name')"
    if (-not $registration.Contains($import)) { throw 'Registration import changed.' }
    $registration = $registration.Replace($import,'')
}
. (Join-Path $dir 'cef-registration-plan.ps1')
. ([scriptblock]::Create($registration))
$publication = [IO.File]::ReadAllText((Join-Path $dir 'cef-preflight-publication.ps1'))
$import = ". (Join-Path " + '$PSScriptRoot' + " 'cef-preflight-registration.ps1')"
if (-not $publication.Contains($import)) { throw 'Publication import changed.' }
. ([scriptblock]::Create($publication.Replace($import,'')))
$serviceSource = [IO.File]::ReadAllText((Join-Path $dir 'cef-preflight-service.ps1'))
$import = ". (Join-Path " + '$PSScriptRoot' + " 'cef-preflight-publication.ps1')"
if (-not $serviceSource.Contains($import)) { throw 'Service import changed.' }
. ([scriptblock]::Create($serviceSource.Replace($import,'')))
function New-VeilRestrictedDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) { throw 'Already exists.' }
    [void][IO.Directory]::CreateDirectory($Path)
}
function Assert-VeilAdminPath([string]$Path) { }
function Assert-VeilSignedImage([string]$Path,[string]$Thumbprint) { }
$script:markerAccepted = $true
function Assert-VeilPreflightDesktopMarker([string]$Root) {
    if (-not $script:markerAccepted) { throw 'Synthetic wrong build marker.' }
}
$script:diagnosticMarker = $false
function Assert-VeilPreflightBrokerMarker([string]$Root,[switch]$Diagnostic) {
    if ([bool]$Diagnostic -ne $script:diagnosticMarker) { throw 'Synthetic broker marker mismatch.' }
}
$rsa = [Security.Cryptography.RSA]::Create(2048)
$req = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL preflight test',$rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
$cert = $req.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5),[DateTimeOffset]::Now.AddHours(1))
$work = Join-Path ([IO.Path]::GetTempPath()) ('veil-preflight-package-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'payload'
$cefEvidence = Join-Path $work 'cef-evidence'
$packageEvidence = Join-Path $work 'package-evidence'
$commit = 'a'*40
foreach ($folder in @((Join-Path $root 'Desktop'),(Join-Path $root 'Broker'),(Join-Path $root 'Cef'),$cefEvidence,$packageEvidence)) {
    [void][IO.Directory]::CreateDirectory($folder)
}
function Verify { Read-VeilPreflightPackageEvidence $root $cefEvidence $packageEvidence $cert.Thumbprint $commit }
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { [void](& $Action) } catch { $rejected = $true }
    if (-not $rejected) { throw ('Invalid preflight package accepted at line {0}: {1}' -f $Action.Ast.Extent.StartLineNumber,$Action.ToString()) }
}
try {
    foreach ($file in @('Desktop/Veil.Desktop.exe','Desktop/Veil.Desktop.dll','Desktop/Veil.Desktop.deps.json','Desktop/Veil.Desktop.runtimeconfig.json',
        'Broker/Veil.VolumeBroker.exe','Broker/Veil.VolumeBroker.dll','Broker/Veil.VolumeBroker.deps.json','Broker/Veil.VolumeBroker.runtimeconfig.json',
        'Cef/veil-cef-product.exe','Cef/veil-cef-product.dll','Cef/libcef.dll','Cef/icudtl.dat')) {
        [IO.File]::WriteAllText((Join-Path $root $file),'synthetic')
    }
    Write-VeilCefCandidateEvidence (Join-Path $root 'Cef') $cefEvidence $commit $cert
    Write-VeilPreflightPackageEvidence $root $cefEvidence $packageEvidence $commit $cert
    $manifest = Verify
    if ($manifest.kind -cne 'cef-preflight-vm-package' -or $manifest.installable -or $manifest.startupEnabled -or
        $manifest.files.Count -ne 12) { throw 'Unexpected package manifest.' }
    # Distinct signed diagnostic inventory: ordinary paths must refuse it.
    $diagEvidence = Join-Path $work 'diagnostic-evidence'
    [void][IO.Directory]::CreateDirectory($diagEvidence)
    $baseline = Join-Path $root 'Broker/cef-system-baseline.json'
    $detached = Join-Path $root 'Broker/cef-system-baseline.sig'
    Reject { Read-VeilPreflightPackageEvidence $root $cefEvidence $packageEvidence $cert.Thumbprint $commit -Diagnostic }
    $script:diagnosticMarker = $true
    Reject { Verify }
    Reject { Write-VeilPreflightPackageEvidence $root $cefEvidence $diagEvidence $commit $cert -Diagnostic }
    [IO.File]::WriteAllText($baseline,'synthetic baseline bytes; not trusted')
    [IO.File]::WriteAllBytes($detached,(New-Object byte[] 383))
    Reject { Write-VeilPreflightPackageEvidence $root $cefEvidence $diagEvidence $commit $cert -Diagnostic }
    [IO.File]::WriteAllBytes($detached,(New-Object byte[] 384))
    Reject { Verify }
    $script:diagnosticMarker = $false
    Reject { Write-VeilPreflightPackageEvidence $root $cefEvidence $diagEvidence $commit $cert -Diagnostic }
    $script:diagnosticMarker = $true
    Write-VeilPreflightPackageEvidence $root $cefEvidence $diagEvidence $commit $cert -Diagnostic
    $diag = Read-VeilPreflightPackageEvidence $root $cefEvidence $diagEvidence $cert.Thumbprint $commit -Diagnostic
    if ($diag.kind -cne 'cef-diagnostic-vm-package' -or $diag.configuration -cne 'CefDiagnostic' -or
        $diag.installable -or $diag.startupEnabled -or $diag.files.Count -ne 14) { throw 'Unexpected diagnostic package.' }
    Reject { Read-VeilPreflightPackageEvidence $root $cefEvidence $diagEvidence $cert.Thumbprint $commit }
    $diagStage = Join-Path $work 'diagnostic-stage-refused'
    Reject { Copy-VeilVerifiedPreflightPackage $root $cefEvidence $diagEvidence $diagStage $cert.Thumbprint $commit }
    if (Test-Path -LiteralPath $diagStage) { throw 'Ordinary staging created diagnostic output.' }
    [IO.File]::WriteAllBytes($detached,(New-Object byte[] 512))
    Reject { Read-VeilPreflightPackageEvidence $root $cefEvidence $diagEvidence $cert.Thumbprint $commit -Diagnostic }
    Remove-Item -LiteralPath $baseline,$detached
    $script:diagnosticMarker = $false
    [void](Verify)
    Reject { Read-VeilPreflightPackageEvidence $root $cefEvidence $packageEvidence ('0'*40) $commit }
    Reject { Read-VeilPreflightPackageEvidence $root $cefEvidence $packageEvidence $cert.Thumbprint ('b'*40) }
    Reject { Write-VeilPreflightPackageEvidence $root $cefEvidence $packageEvidence $commit $cert }
    $script:markerAccepted = $false
    Reject { Verify }
    $script:markerAccepted = $true
    foreach ($file in @('Desktop/Veil.Desktop.dll','Broker/Veil.VolumeBroker.dll','Cef/libcef.dll')) {
        $path = Join-Path $root $file
        [IO.File]::WriteAllText($path,'modified!')
        Reject { Verify }
        [IO.File]::WriteAllText($path,'synthetic')
        Remove-Item -LiteralPath $path
        Reject { Verify }
        [IO.File]::WriteAllText($path,'synthetic')
    }
    $extra = Join-Path $root 'Desktop/extra.dll'
    [IO.File]::WriteAllText($extra,'synthetic')
    Reject { Verify }
    Remove-Item -LiteralPath $extra
    $json = Join-Path $packageEvidence 'preflight-package.json'
    $original = [IO.File]::ReadAllBytes($json)
    [IO.File]::AppendAllText($json,' ')
    Reject { Verify }
    [IO.File]::WriteAllBytes($json,$original)
    [void](Verify)
    $stage = Join-Path $work 'stage'
    $bad = Join-Path $work 'bad-stage'
    Reject { Copy-VeilVerifiedPreflightPackage $root $cefEvidence $packageEvidence $bad ('0'*40) $commit }
    if (Test-Path -LiteralPath $bad) { throw 'Bad trust input created staging.' }
    Reject { Copy-VeilVerifiedPreflightPackage $root $cefEvidence $packageEvidence (Join-Path $root 'bad') $cert.Thumbprint $commit }
    $plan = Copy-VeilVerifiedPreflightPackage $root $cefEvidence $packageEvidence $stage $cert.Thumbprint $commit
    if ($plan.kind -cne 'cef-preflight-stage-plan' -or $plan.installable -or $plan.startupEnabled -or
        $plan.runtimePins.startupEnabled -or $plan.runtimePins.files.Count -ne 4 -or $plan.sourceCommit -cne $commit) {
        throw 'Unexpected stage plan.'
    }
    foreach ($pair in @(@{name='desktopSha256';path='Desktop/Veil.Desktop.exe'},@{name='brokerSha256';path='Broker/Veil.VolumeBroker.exe'},@{name='cefChildSha256';path='Cef/veil-cef-product.exe'})) {
        if ($plan.($pair.name) -ne (Get-FileHash -LiteralPath (Join-Path $root $pair.path)).Hash) { throw 'Staged image pin mismatch.' }
    }
    Reject { Copy-VeilVerifiedPreflightPackage $root $cefEvidence $packageEvidence $stage $cert.Thumbprint $commit }
    [void](Get-VeilPreflightStagePlan $stage $cert.Thumbprint $commit)
    $volumeGuid = [guid]'11111111-1111-1111-1111-111111111111'
    $primary = [guid]'22222222-2222-2222-2222-222222222222'
    $recovery = [guid]'33333333-3333-3333-3333-333333333333'
    $sid = 'S-1-5-21-100-200-300-1001'
    $bin = 'C:\Program Files\VEIL'
    $image = 'C:\Program Files\VEIL-test-volume\profile.vhdx'
    $profile = 'V:\VEIL\profile'
    $draft = New-VeilPreflightRegistrationDraft $plan $bin $image $profile $volumeGuid $primary $recovery $sid 'synthetic'
    if ($draft['broker.json'].clientSha256 -cne $plan.desktopSha256 -or
        $draft['broker.json'].cefChildSha256 -cne $plan.cefChildSha256 -or
        $draft['broker.json'].clientImages.Count -ne 1 -or
        $draft['broker.json'].clientImages[0] -ne (Join-Path $bin 'Desktop\Veil.Desktop.exe') -or
        $draft['broker.json'].allowUnsignedClients -or $draft['broker.json'].development -or
        -not $draft['client.json'].requireWindowsService -or $draft['cef-runtime-pins.json'].startupEnabled) {
        throw 'Invalid registration draft.'
    }
    Reject { New-VeilPreflightRegistrationDraft $plan $bin $image $profile $volumeGuid $primary $primary $sid 'synthetic' }
    Reject { New-VeilPreflightRegistrationDraft $plan $bin $image $profile ([guid]::Empty) $primary $recovery $sid 'synthetic' }
    Reject { New-VeilPreflightRegistrationDraft $plan $bin $image $profile $volumeGuid $primary $recovery 'S-1-5-18' 'synthetic' }
    Reject { New-VeilPreflightRegistrationDraft $plan $bin $image 'V:\' $volumeGuid $primary $recovery $sid 'synthetic' }
    Reject { New-VeilPreflightRegistrationDraft $plan $bin $image $profile $volumeGuid $primary $recovery $sid 'bad/id' }
    $originalHash = $plan.cefChildSha256
    $plan.cefChildSha256 = '0'*64
    Reject { New-VeilPreflightRegistrationDraft $plan $bin $image $profile $volumeGuid $primary $recovery $sid 'synthetic' }
    $plan.cefChildSha256 = $originalHash
    $destination = Join-Path $work 'registration'
    $journal = Write-VeilPreflightRegistrationDraft $destination $draft $plan
    if ($journal.state -cne 'prepared-only' -or $journal.installable -or $journal.startupEnabled -or
        $journal.files.Count -ne 3 -or @(Get-ChildItem -LiteralPath $destination -Force).Count -ne 4) {
        throw 'Invalid preparation marker.'
    }
    foreach ($file in $journal.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $destination $file.name)).Hash -ne $file.sha256) { throw 'Registration hash mismatch.' }
    }
    $journalPath = Join-Path $destination 'preparation.json'
    $journalHash = (Get-FileHash -LiteralPath $journalPath).Hash
    Reject { Write-VeilPreflightRegistrationDraft $destination $draft $plan }
    if ((Get-FileHash -LiteralPath $journalPath).Hash -ne $journalHash) { throw 'Existing preparation changed.' }
    function Inspect-Draft([string]$Directory) {
        Read-VeilPreflightRegistrationDraft $Directory $plan $bin $image $profile $volumeGuid $primary $recovery $sid
    }
    $verifiedJournal = Inspect-Draft $destination
    if ($verifiedJournal.state -cne 'prepared-only' -or $verifiedJournal.files.Count -ne 3) { throw 'Invalid read-only inspection.' }
    # A separately signed package from the same commit and signer, with
    # identical EXEs but a changed managed DLL, must not reuse preparation.
    $alternateRoot = Join-Path $work 'alternate-payload'
    $alternateProof = Join-Path $work 'alternate-package-proof'
    $alternateStage = Join-Path $work 'alternate-stage'
    Copy-Item -LiteralPath $root -Destination $alternateRoot -Recurse
    [void][IO.Directory]::CreateDirectory($alternateProof)
    [IO.File]::WriteAllText((Join-Path $alternateRoot 'Desktop/Veil.Desktop.dll'),'alternate synthetic DLL')
    Write-VeilPreflightPackageEvidence $alternateRoot $cefEvidence $alternateProof $commit $cert
    $alternatePlan = Copy-VeilVerifiedPreflightPackage $alternateRoot $cefEvidence $alternateProof $alternateStage $cert.Thumbprint $commit
    if ($alternatePlan.desktopSha256 -cne $plan.desktopSha256 -or
        $alternatePlan.brokerSha256 -cne $plan.brokerSha256 -or
        $alternatePlan.cefChildSha256 -cne $plan.cefChildSha256 -or
        $alternatePlan.proofHashes.'preflight-package.json' -ceq $plan.proofHashes.'preflight-package.json') {
        throw 'Alternate signed package fixture is not distinct.'
    }
    Reject { Read-VeilPreflightRegistrationDraft $destination $alternatePlan $bin $image $profile $volumeGuid $primary $recovery $sid }
    # Old format preparations must not acquire authority under the new reader.
    $savedJournal = [IO.File]::ReadAllBytes($journalPath)
    $oldJournal = [ordered]@{
        kind='cef-preflight-registration-journal';format=1;state='prepared-only'
        sourceCommit=$plan.sourceCommit;installable=$false;startupEnabled=$false
        desktopSha256=$plan.desktopSha256;brokerSha256=$plan.brokerSha256;cefChildSha256=$plan.cefChildSha256;files=$journal.files
    }
    [IO.File]::WriteAllBytes($journalPath,[Text.UTF8Encoding]::new($false).GetBytes(($oldJournal | ConvertTo-Json -Depth 10)))
    Reject { Inspect-Draft $destination }
    [IO.File]::WriteAllBytes($journalPath,$savedJournal)
    [void](Inspect-Draft $destination)
    Write-Host 'PASS: full signed package binding rejects alternate payload and legacy preparation'
    Reject { Read-VeilPreflightRegistrationDraft $destination $plan $bin $image 'V:\Other\profile' $volumeGuid $primary $recovery $sid }
    Reject { Read-VeilPreflightRegistrationDraft $destination $plan $bin $image $profile $volumeGuid $primary $recovery 'S-1-5-21-100-200-300-1002' }
    Reject { Read-VeilPreflightRegistrationDraft $destination $plan $bin $image $profile $volumeGuid $recovery $primary $sid }
    foreach ($name in @('broker.json','client.json','cef-runtime-pins.json','preparation.json')) {
        $path = Join-Path $destination $name
        $originalBytes = [IO.File]::ReadAllBytes($path)
        [IO.File]::AppendAllText($path,' ')
        Reject { Inspect-Draft $destination }
        [IO.File]::WriteAllBytes($path,$originalBytes)
        Remove-Item -LiteralPath $path
        Reject { Inspect-Draft $destination }
        [IO.File]::WriteAllBytes($path,$originalBytes)
    }
    $extra = Join-Path $destination 'extra.json'
    [IO.File]::WriteAllText($extra,'{}')
    Reject { Inspect-Draft $destination }
    Remove-Item -LiteralPath $extra
    # A fresh, internally consistent journal with altered security settings
    # must still fail because the independently supplied inputs disagree.
    $forged = Join-Path $work 'forged-registration'
    $wrongDraft = New-VeilPreflightRegistrationDraft $plan $bin $image 'V:\Other\profile' $volumeGuid $primary $recovery $sid 'synthetic'
    [void](Write-VeilPreflightRegistrationDraft $forged $wrongDraft $plan)
    Reject { Inspect-Draft $forged }
    $wrongDraft = New-VeilPreflightRegistrationDraft $plan $bin $image $profile $volumeGuid $primary $recovery $sid 'synthetic'
    $wrongDraft['broker.json'].allowUnsignedClients = $true
    $forgedBroker = Join-Path $work 'forged-broker'
    [void](Write-VeilPreflightRegistrationDraft $forgedBroker $wrongDraft $plan)
    Reject { Inspect-Draft $forgedBroker }
    [void](Inspect-Draft $destination)
    if ((Get-FileHash -LiteralPath $journalPath).Hash -ne $journalHash) { throw 'Inspection changed preparation.' }
    Write-Host 'PASS: exact registration reconstruction, independent inputs, forged journal and partial file refusal'
    # Real temporary-directory copies and same-parent renames; no OS service
    # or BitLocker. The registration binds to the temporary destination.
    $publishedBin = Join-Path $work 'published-bin'
    $publishedData = Join-Path $work 'published-data'
    $transaction = Join-Path $work 'publication'
    $publishPrepared = Join-Path $work 'publication-prepared'
    $publishDraft = New-VeilPreflightRegistrationDraft $plan $publishedBin $image $profile $volumeGuid $primary $recovery $sid 'synthetic'
    [void](Write-VeilPreflightRegistrationDraft $publishPrepared $publishDraft $plan)
    $final = Publish-VeilPreflightFiles $stage $publishPrepared $publishedBin $publishedData $transaction $cert.Thumbprint $commit $image $profile $volumeGuid $primary $recovery $sid {}
    if ($final.state -cne 'files-published-only' -or $final.serviceCreated -or $final.startupEnabled -or
        @(Get-ChildItem -LiteralPath $transaction -Force).Count -ne 4 -or
        (Test-Path -LiteralPath ($publishedBin + '.cef-preflight-pending')) -or
        (Test-Path -LiteralPath ($publishedData + '.cef-preflight-pending'))) { throw 'Publication boundary mismatch.' }
    [void](Read-VeilPreflightPackageEvidence $publishedBin (Join-Path $stage 'cef-evidence') (Join-Path $stage 'package-evidence') $cert.Thumbprint $commit)
    [void](Read-VeilPreflightRegistrationDraft $publishedData $plan $publishedBin $image $profile $volumeGuid $primary $recovery $sid)
    [void](Read-VeilPreflightRegistrationDraft $publishPrepared $plan $publishedBin $image $profile $volumeGuid $primary $recovery $sid)
    Reject { Publish-VeilPreflightFiles $stage $publishPrepared $publishedBin $publishedData $transaction $cert.Thumbprint $commit $image $profile $volumeGuid $primary $recovery $sid {} }
    # Compile the registry helper and exercise the pure plan and security
    # descriptor checks. Hosted CI never creates HKLM keys.
    . (Join-Path $dir 'cef-preflight-bootstrap.ps1')
    Add-Type -Path (Join-Path $dir 'CefPreflightBootstrapRegistry.cs')
    $bootstrapPlan = Get-VeilInstalledBootstrapPlan $publishedData $profile $volumeGuid
    if ($bootstrapPlan.ProductRoot -cne $profile -or $bootstrapPlan.VolumeId -cne 'synthetic' -or
        $bootstrapPlan.VolumeGuid -cne ('\\?\Volume{' + $volumeGuid.ToString() + '}\')) { throw 'Bootstrap plan mismatch.' }
    Reject { Get-VeilInstalledBootstrapPlan $publishedData 'V:\Other\profile' $volumeGuid }
    Reject { Get-VeilInstalledBootstrapPlan $publishedData $profile ([guid]::Empty) }
    function DescriptorBytes([string]$Sddl) {
        $sd = [Security.AccessControl.RawSecurityDescriptor]::new($Sddl)
        $buffer = [byte[]]::new($sd.BinaryLength)
        $sd.GetBinaryForm($buffer,0)
        return ,$buffer
    }
    [Veil.Preflight.BootstrapRegistry]::ValidateDescriptor((DescriptorBytes 'O:BAG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)'))
    foreach ($badDescriptor in @('O:BUG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)',
        'O:BAG:BAD:(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)',
        'O:BAG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KA;;;BU)')) {
        Reject { [Veil.Preflight.BootstrapRegistry]::ValidateDescriptor((DescriptorBytes $badDescriptor)) }
    }
    Write-Host 'PASS: bootstrap plan and registry descriptor checks (no HKLM mutation)'
    # SCM is a fake here; publication, journal bytes and descriptor parsing
    # are real. Never register a privileged service on a hosted runner.
    $command = '"{0}" --service' -f (Join-Path $publishedBin 'Broker\Veil.VolumeBroker.exe')
    $script:fakeService = $null
    $script:fakeDacl = $null
    $script:createCalls = 0
    $verifyPublished = {
        Get-VeilPreflightPublicationState $stage $publishPrepared $publishedBin $publishedData $transaction $cert.Thumbprint $commit $image $profile $volumeGuid $primary $recovery $sid {}
    }
    $readService = { $script:fakeService }
    $createService = {
        param($binary)
        $script:createCalls++
        $script:fakeService = [pscustomobject]@{
            Name='Veil.VolumeBroker';PathName=$binary;StartName='LocalSystem';StartMode='Disabled'
            State='Stopped';ProcessId=0;ServiceType='Own Process'
        }
    }
    $setDacl = { param($sddl) $script:fakeDacl = $sddl }
    $readDacl = { $script:fakeDacl }
    $serviceJournal = Join-Path $work 'service-journal'
    $serviceResult = Register-VeilDisabledPreflightService $serviceJournal $command $verifyPublished $readService $createService $setDacl $readDacl
    if ($serviceResult.state -cne 'registered-disabled' -or -not $serviceResult.serviceCreated -or $serviceResult.startupEnabled -or
        $script:createCalls -ne 1 -or @(Get-ChildItem -LiteralPath $serviceJournal -Force).Count -ne 2) { throw 'Service registration boundary mismatch.' }
    Assert-VeilDisabledPreflightService $script:fakeService $command
    Assert-VeilPreflightServiceDacl $script:fakeDacl
    Reject { Assert-VeilPreflightServiceDacl 'D:(A;;RPWP;;;AU)' }
    Reject { Register-VeilDisabledPreflightService $serviceJournal $command $verifyPublished $readService $createService $setDacl $readDacl }
    $badServiceJournal = Join-Path $work 'bad-service-journal'
    Reject { Register-VeilDisabledPreflightService $badServiceJournal $command $verifyPublished $readService $createService $setDacl $readDacl }
    if ((Test-Path -LiteralPath $badServiceJournal) -or $script:createCalls -ne 1) { throw 'Existing service was adopted.' }
    foreach ($pair in @(@{name='StartMode';value='Auto'},@{name='StartName';value='LocalService'},@{name='State';value='Running'},@{name='ProcessId';value=100},@{name='PathName';value='wrong'})) {
        $old = $script:fakeService.($pair.name)
        $script:fakeService.($pair.name) = $pair.value
        Reject { Assert-VeilDisabledPreflightService $script:fakeService $command }
        $script:fakeService.($pair.name) = $old
    }
    $verifyService = {
        Read-VeilPreflightServiceRegistration $serviceJournal $command $verifyPublished $readService $readDacl
    }
    $observed = & $verifyService
    if ($observed.state -cne 'registered-disabled' -or $observed.startupEnabled) { throw 'Registered service verification failed.' }
    $record = Join-Path $serviceJournal '02-service-registered.json'
    $saved = [IO.File]::ReadAllBytes($record)
    [IO.File]::AppendAllText($record,' ')
    Reject { & $verifyService }
    [IO.File]::WriteAllBytes($record,$saved)
    $script:fakeDacl = 'D:(A;;RPWP;;;AU)'
    Reject { & $verifyService }
    $script:fakeDacl = $script:VeilPreflightServiceDacl
    $manual = { $script:fakeService.StartMode = 'Manual' }
    $disabled = { $script:fakeService.StartMode = 'Disabled' }
    $startProbe = { $script:fakeService.State = 'Running'; $script:fakeService.ProcessId = 100 }
    $stopProbe = { $script:fakeService.State = 'Stopped'; $script:fakeService.ProcessId = 0 }
    $probeRoot = Join-Path $work 'service-probe'
    $probeResult = Invoke-VeilPreflightServiceProbe $probeRoot $command $verifyService $readService $readDacl $manual $startProbe $disabled $stopProbe {}
    if ($probeResult.state -cne 'running-then-stopped' -or $probeResult.startupEnabled -or
        @(Get-ChildItem -LiteralPath $probeRoot -Force).Count -ne 3) { throw 'Service probe did not complete.' }
    Assert-VeilDisabledPreflightService $script:fakeService $command
    Reject { Invoke-VeilPreflightServiceProbe $probeRoot $command $verifyService $readService $readDacl $manual $startProbe $disabled $stopProbe {} }
    $failedProbe = Join-Path $work 'failed-service-probe'
    $failStart = { & $startProbe; throw 'Synthetic start reply failure.' }
    Reject { Invoke-VeilPreflightServiceProbe $failedProbe $command $verifyService $readService $readDacl $manual $failStart $disabled $stopProbe {} }
    Assert-VeilDisabledPreflightService $script:fakeService $command
    if (-not (Test-Path -LiteralPath (Join-Path $failedProbe '01-probe-intent.json')) -or
        (Test-Path -LiteralPath (Join-Path $failedProbe '03-stopped-verified.json'))) { throw 'Failed probe incorrectly completed.' }
    # The service-only probe is a prerequisite, not evidence of the user
    # preflight. A separate synthetic exercise returns the actual exit-code type.
    Assert-VeilCompletedServiceProbe $probeRoot (& $verifyService)
    Reject { Assert-VeilCompletedServiceProbe $failedProbe (& $verifyService) }
    $probeFinalPath = Join-Path $probeRoot '03-stopped-verified.json'
    $probeFinalBytes = [IO.File]::ReadAllBytes($probeFinalPath)
    [IO.File]::AppendAllText($probeFinalPath,' ')
    Reject { Assert-VeilCompletedServiceProbe $probeRoot (& $verifyService) }
    [IO.File]::WriteAllBytes($probeFinalPath,$probeFinalBytes)
    # Real Win32 token inspection of the current runner only.
    Add-Type -Path (Join-Path $dir 'CefPreflightProcessObservation.cs')
    $self = [Diagnostics.Process]::GetCurrentProcess()
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $selfObservation = [Veil.Preflight.ProcessObservation]::Inspect($self)
        if ($selfObservation.ProcessId -ne $PID -or $selfObservation.UserSid -cne $identity.User.Value -or
            $selfObservation.SessionId -ne $self.SessionId -or
            $selfObservation.ImagePath -ne $self.MainModule.FileName -or $selfObservation.IntegritySid -notmatch '\AS-1-16-[0-9]+\z') {
            throw 'Win32 process observation mismatch.'
        }
        # Cross-check enabled administrator membership through the current
        # identity, whose normal token is distinct from the query-only handle.
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $selfObservation.Administrator) {
            throw 'Administrator group missing from raw token observation.'
        }
        Reject { $selfObservation.RequireStandard($selfObservation.ImagePath,'S-1-0-0',$self.SessionId) }
        Reject { $selfObservation.RequireStandard('C:\wrong.exe',$identity.User.Value,$self.SessionId) }
        Reject { $selfObservation.RequireStandard($selfObservation.ImagePath,$identity.User.Value,-1) }
        if ($selfObservation.Administrator -or $selfObservation.Elevated -or $selfObservation.ElevationType -ne 1 -or
            $selfObservation.IntegritySid -ne 'S-1-16-8192' -or $selfObservation.SessionId -le 0) {
            Reject { $selfObservation.RequireStandard($selfObservation.ImagePath,$identity.User.Value,$self.SessionId) }
        } else {
            $selfObservation.RequireStandard($selfObservation.ImagePath,$identity.User.Value,$self.SessionId)
        }
    } finally { $self.Dispose(); $identity.Dispose() }
    function New-SyntheticUserObservation {
        [pscustomobject]@{kind='standard-user-exit-observation';exitCode=[int]29;processId=[int]200;sessionId=[int]1;tokenVerified=$true}
    }
    $sessionRoot = Join-Path $work 'protected-session'
    $exercise = {
        if ($script:fakeService.State -ne 'Running') { throw 'Exercise without broker.' }
        New-SyntheticUserObservation
    }
    $sessionResult = Invoke-VeilPreflightServiceProbe $sessionRoot $command $verifyService $readService $readDacl $manual $startProbe $disabled $stopProbe {} $exercise
    if ($sessionResult.state -cne 'running-then-stopped' -or $sessionResult.startupEnabled -or
        @(Get-ChildItem -LiteralPath $sessionRoot -Force).Count -ne 4) { throw 'Protected session not completed.' }
    $exerciseRecord = Get-Content -LiteralPath (Join-Path $sessionRoot '02a-protected-preflight.json') -Raw | ConvertFrom-Json
    if ($exerciseRecord.exitCode -ne 29 -or $exerciseRecord.startupEnabled -or -not $exerciseRecord.tokenVerified -or
        $exerciseRecord.processId -ne 200 -or $exerciseRecord.sessionId -ne 1) { throw 'Unexpected exercise result.' }
    # Read-only reconstruction must bind the exact observation to cleanup.
    $verifiedSession = Read-VeilCompletedProtectedSession $sessionRoot (& $verifyService)
    if ($verifiedSession.state -cne 'verified-protected-preflight-records' -or $verifiedSession.startupEnabled -or
        $verifiedSession.sessionSha256 -ne (Get-FileHash -LiteralPath (Join-Path $sessionRoot '03-stopped-verified.json')).Hash) {
        throw 'Protected session verification mismatch.'
    }
    Reject { Read-VeilCompletedProtectedSession $probeRoot (& $verifyService) }
    $differentRegistration = & $verifyService
    $differentRegistration.registrationSha256 = '0'*64
    Reject { Read-VeilCompletedProtectedSession $sessionRoot $differentRegistration }
    foreach ($name in @('01-probe-intent.json','02-running-observed.json','02a-protected-preflight.json','03-stopped-verified.json')) {
        $recordPath = Join-Path $sessionRoot $name
        $savedRecord = [IO.File]::ReadAllBytes($recordPath)
        [IO.File]::AppendAllText($recordPath,' ')
        Reject { Read-VeilCompletedProtectedSession $sessionRoot (& $verifyService) }
        [IO.File]::WriteAllBytes($recordPath,$savedRecord)
        Remove-Item -LiteralPath $recordPath
        Reject { Read-VeilCompletedProtectedSession $sessionRoot (& $verifyService) }
        [IO.File]::WriteAllBytes($recordPath,$savedRecord)
    }
    $observationPath = Join-Path $sessionRoot '02a-protected-preflight.json'
    $savedObservation = [IO.File]::ReadAllBytes($observationPath)
    $parsedObservation = Get-Content -LiteralPath $observationPath -Raw | ConvertFrom-Json
    $parsedObservation.processId = 201
    [IO.File]::WriteAllBytes($observationPath,[Text.UTF8Encoding]::new($false).GetBytes(($parsedObservation | ConvertTo-Json -Depth 10)))
    Reject { Read-VeilCompletedProtectedSession $sessionRoot (& $verifyService) }
    [IO.File]::WriteAllBytes($observationPath,$savedObservation)
    $unexpectedRecord = Join-Path $sessionRoot 'unexpected.json'
    [IO.File]::WriteAllText($unexpectedRecord,'{}')
    Reject { Read-VeilCompletedProtectedSession $sessionRoot (& $verifyService) }
    Remove-Item -LiteralPath $unexpectedRecord
    Assert-VeilPreflightPublicationEqual (Read-VeilCompletedProtectedSession $sessionRoot (& $verifyService)) $verifiedSession
    Write-Host 'PASS: protected session record reconstruction and observation digest binding'
    Reject { Assert-VeilCompletedServiceProbe $sessionRoot (& $verifyService) }
    Assert-VeilDisabledPreflightService $script:fakeService $command
    $index = 0
    foreach ($badExercise in @({ return [int]29 },{ return '29' },{ throw 'Synthetic user timeout.' },
        { $o = New-SyntheticUserObservation; $o.exitCode=1; $o },
        { $o = New-SyntheticUserObservation; $o.exitCode='29'; $o },
        { $o = New-SyntheticUserObservation; $o.tokenVerified=$false; $o },
        { $o = New-SyntheticUserObservation; $o.sessionId=0; $o },
        { $o = New-SyntheticUserObservation; $o.processId=0; $o },
        { $script:fakeService.ProcessId = 101; New-SyntheticUserObservation })) {
        $index++
        $badSession = Join-Path $work ('bad-protected-session-' + $index)
        Reject { Invoke-VeilPreflightServiceProbe $badSession $command $verifyService $readService $readDacl $manual $startProbe $disabled $stopProbe {} $badExercise }
        Assert-VeilDisabledPreflightService $script:fakeService $command
        if ((Test-Path -LiteralPath (Join-Path $badSession '02a-protected-preflight.json')) -or
            (Test-Path -LiteralPath (Join-Path $badSession '03-stopped-verified.json'))) {
            throw 'Failed protected session recorded success.'
        }
    }
    Write-Host 'PASS: protected session callback, exact exit code, broker continuity and cleanup'
    Write-Host 'PASS: service journal revalidation and synthetic start/stop/failure cleanup'
    $script:fakeService = $null
    $badPublication = { [pscustomobject]@{state='publish-registration';startupEnabled=$false;serviceCreated=$false} }
    Reject { Register-VeilDisabledPreflightService $badServiceJournal $command $badPublication $readService $createService $setDacl $readDacl }
    if ((Test-Path -LiteralPath $badServiceJournal) -or $script:createCalls -ne 1) { throw 'Incomplete publication created a service.' }
    $failedServiceJournal = Join-Path $work 'failed-service-journal'
    Reject { Register-VeilDisabledPreflightService $failedServiceJournal $command $verifyPublished $readService $createService { throw 'Synthetic SCM DACL failure.' } $readDacl }
    if ($script:createCalls -ne 2 -or $null -eq $script:fakeService -or
        -not (Test-Path -LiteralPath (Join-Path $failedServiceJournal '01-service-intent.json')) -or
        (Test-Path -LiteralPath (Join-Path $failedServiceJournal '02-service-registered.json'))) { throw 'Failed SCM registration evidence not preserved.' }
    Write-Host 'PASS: synthetic disabled service registration, identity/DACL refusal and failure preservation'
    # Inject refusal at the boundary after binaries are published but before
    # registration is moved. Both sources and partial transaction must survive.
    $interruptedBin = Join-Path $work 'interrupted-bin'
    $interruptedData = Join-Path $work 'interrupted-data'
    $interruptedTransaction = Join-Path $work 'interrupted-publication'
    $interruptedPrepared = Join-Path $work 'interrupted-prepared'
    $interruptedDraft = New-VeilPreflightRegistrationDraft $plan $interruptedBin $image $profile $volumeGuid $primary $recovery $sid 'synthetic'
    [void](Write-VeilPreflightRegistrationDraft $interruptedPrepared $interruptedDraft $plan)
    $script:publicationChecks = 0
    $interrupt = { $script:publicationChecks++; if ($script:publicationChecks -eq 3) { throw 'Synthetic publication interruption.' } }
    Reject { Publish-VeilPreflightFiles $stage $interruptedPrepared $interruptedBin $interruptedData $interruptedTransaction $cert.Thumbprint $commit $image $profile $volumeGuid $primary $recovery $sid $interrupt }
    if (-not (Test-Path -LiteralPath $interruptedBin) -or (Test-Path -LiteralPath $interruptedData) -or
        -not (Test-Path -LiteralPath ($interruptedData + '.cef-preflight-pending')) -or
        -not (Test-Path -LiteralPath (Join-Path $interruptedTransaction '03-binaries-published.json')) -or
        (Test-Path -LiteralPath (Join-Path $interruptedTransaction '04-files-published.json'))) {
        throw 'Interrupted publication state not preserved.'
    }
    Reject { Publish-VeilPreflightFiles $stage $interruptedPrepared $interruptedBin $interruptedData $interruptedTransaction $cert.Thumbprint $commit $image $profile $volumeGuid $primary $recovery $sid {} }
    function Inspect-Publication {
        Get-VeilPreflightPublicationState $stage $interruptedPrepared $interruptedBin $interruptedData $interruptedTransaction $cert.Thumbprint $commit $image $profile $volumeGuid $primary $recovery $sid {}
    }
    function Resume-Publication {
        Resume-VeilPreflightPublication $stage $interruptedPrepared $interruptedBin $interruptedData $interruptedTransaction $cert.Thumbprint $commit $image $profile $volumeGuid $primary $recovery $sid {}
    }
    function Require-PublicationState([string]$Expected) {
        if ((Inspect-Publication).state -cne $Expected) { throw "Expected publication state $Expected." }
    }
    Require-PublicationState 'publish-registration'
    $record3 = Join-Path $interruptedTransaction '03-binaries-published.json'
    $record4 = Join-Path $interruptedTransaction '04-files-published.json'
    $record2 = Join-Path $interruptedTransaction '02-copies-verified.json'
    $record3Bytes = [IO.File]::ReadAllBytes($record3)
    [IO.File]::AppendAllText($record3,' ')
    Reject { Inspect-Publication }
    Reject { Resume-Publication }
    [IO.File]::WriteAllBytes($record3,$record3Bytes)
    $pendingData = $interruptedData + '.cef-preflight-pending'
    $pendingBin = $interruptedBin + '.cef-preflight-pending'
    $pendingConfig = Join-Path $pendingData 'client.json'
    $pendingBytes = [IO.File]::ReadAllBytes($pendingConfig)
    [IO.File]::AppendAllText($pendingConfig,' ')
    Reject { Resume-Publication }
    if (Test-Path -LiteralPath $interruptedData) { throw 'Invalid registration was published.' }
    [IO.File]::WriteAllBytes($pendingConfig,$pendingBytes)
    [void][IO.Directory]::CreateDirectory($pendingBin)
    Reject { Inspect-Publication }
    Remove-Item -LiteralPath $pendingBin
    # Synthesize a crash immediately after the first rename but before
    # its journal record, then one before the rename. Test-only rewind.
    Remove-Item -LiteralPath $record3
    Require-PublicationState 'record-binaries'
    [IO.Directory]::Move($interruptedBin,$pendingBin)
    Require-PublicationState 'publish-binaries'
    $extraRecord = Join-Path $interruptedTransaction '04-files-published.json'
    [IO.File]::WriteAllText($extraRecord,'{}')
    Reject { Inspect-Publication }
    Remove-Item -LiteralPath $extraRecord
    $resumed = Resume-Publication
    if ($resumed.state -cne 'files-published-only' -or $resumed.startupEnabled -or $resumed.serviceCreated) {
        throw 'Recovery did not finish verified file publication.'
    }
    Require-PublicationState 'files-published-only'
    $completedHash = (Get-FileHash -LiteralPath $record4).Hash
    [void](Resume-Publication)
    if ((Get-FileHash -LiteralPath $record4).Hash -ne $completedHash) { throw 'Completed recovery was not idempotent.' }
    Remove-Item -LiteralPath $record4
    Require-PublicationState 'record-completion'
    [void](Resume-Publication)
    if ((Get-FileHash -LiteralPath $record4).Hash -ne $completedHash) { throw 'Completion record differs.' }
    # No verified-copy record is never a resumable state, even if both
    # temporary trees happen to be complete.
    Remove-Item -LiteralPath $record4,$record3,$record2
    [IO.Directory]::Move($interruptedData,$pendingData)
    [IO.Directory]::Move($interruptedBin,$pendingBin)
    Require-PublicationState 'incomplete-copies'
    Reject { Resume-Publication }
    if ((Test-Path -LiteralPath $interruptedBin) -or (Test-Path -LiteralPath $interruptedData) -or
        -not (Test-Path -LiteralPath $pendingBin) -or -not (Test-Path -LiteralPath $pendingData)) { throw 'Unverified copies were modified.' }
    Write-Host 'PASS: publication recovery at known boundaries, tamper refusal and incomplete-copy preservation'
    Write-Host 'PASS: verified publication copies, journal boundaries and interrupted publication preservation'
    # Inject a deterministic file creation conflict after creating a fresh
    # preparation directory. No complete marker may follow the failed write.
    $partial = Join-Path $work 'partial-registration'
    function New-VeilRestrictedDirectory([string]$Path) {
        [void][IO.Directory]::CreateDirectory($Path)
        [IO.File]::WriteAllText((Join-Path $Path 'client.json'),'preserve')
    }
    Reject { Write-VeilPreflightRegistrationDraft $partial $draft $plan }
    if ((Test-Path -LiteralPath (Join-Path $partial 'preparation.json')) -or
        [IO.File]::ReadAllText((Join-Path $partial 'client.json')) -cne 'preserve') { throw 'Partial registration not preserved.' }
    Reject { Inspect-Draft $partial }
    Write-Host 'PASS: preflight registration draft, hash marker and interrupted preparation refusal'
    $stageDll = Join-Path $stage 'payload/Desktop/Veil.Desktop.dll'
    [IO.File]::AppendAllText($stageDll,'changed')
    Reject { Get-VeilPreflightStagePlan $stage $cert.Thumbprint $commit }
    if ([IO.File]::ReadAllText($stageDll) -cne 'syntheticchanged') { throw 'Invalid staging was modified.' }
    [IO.File]::WriteAllText($stageDll,'synthetic')
    $stageProof = Join-Path $stage 'package-evidence/preflight-package.p7s'
    [IO.File]::AppendAllText($stageProof,'changed')
    # Mutate the signed JSON as well; CMS decoders may ignore trailing bytes.
    [IO.File]::AppendAllText((Join-Path $stage 'package-evidence/preflight-package.json'),' ')
    Reject { Get-VeilPreflightStagePlan $stage $cert.Thumbprint $commit }
    [IO.File]::AppendAllText((Join-Path $root 'Desktop/Veil.Desktop.dll'),'changed')
    Reject { Copy-VeilVerifiedPreflightPackage $root $cefEvidence $packageEvidence $bad $cert.Thumbprint $commit }
    if (Test-Path -LiteralPath $bad) { throw 'Changed source created staging.' }
    Write-Host 'PASS: verified preflight staging, exact image pins, refusal and preservation'
    Write-Host 'PASS: separate preflight package signed inventory synthetic checks'
}
finally {
    $cert.Dispose()
    $rsa.Dispose()
    Remove-Item -LiteralPath $work -Recurse -Force
}
