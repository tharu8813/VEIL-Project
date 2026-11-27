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
    if (-not $rejected) { throw 'Invalid preflight package accepted.' }
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
