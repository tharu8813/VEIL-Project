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
