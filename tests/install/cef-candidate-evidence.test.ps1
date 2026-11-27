# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = 'Stop'
# Test-only relaxation: in-memory self-signed certificates, no trust store
# changes, no actual Authenticode binaries or installed admin ACLs.
$scriptPath = Join-Path $PSScriptRoot '..\..\scripts\cef-candidate-evidence.ps1'
$source = [IO.File]::ReadAllText($scriptPath)
$dir = Split-Path -Parent $scriptPath
$source = $source.Replace('$PSScriptRoot',("'" + $dir.Replace("'","''") + "'"))
if (-not $source.Contains('$cms.CheckSignature($false)')) { throw 'CMS verification contract changed.' }
. ([scriptblock]::Create($source.Replace('$cms.CheckSignature($false)','$cms.CheckSignature($true)')))
function Assert-VeilAdminPath([string]$Path) { }
function Assert-VeilSignedImage([string]$Path,[string]$Thumbprint) { }
$rsa = [Security.Cryptography.RSA]::Create(2048)
$req = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL candidate evidence test',$rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
$cert = $req.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5),[DateTimeOffset]::Now.AddHours(1))
$work = Join-Path ([IO.Path]::GetTempPath()) ('veil-cef-evidence-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'candidate'
$evidence = Join-Path $work 'evidence'
$commit = 'a'*40
[void][IO.Directory]::CreateDirectory($root)
[void][IO.Directory]::CreateDirectory($evidence)
function Verify { Read-VeilCefCandidateEvidence $root $evidence $cert.Thumbprint $commit }
function Reject([scriptblock]$Action) {
    $rejected=$false
    try { [void](& $Action) } catch { $rejected=$true }
    if (-not $rejected) { throw 'Invalid candidate evidence accepted.' }
}
try {
    foreach ($file in @('veil-cef-product.exe','veil-cef-product.dll','libcef.dll','icudtl.dat')) {
        [IO.File]::WriteAllText((Join-Path $root $file),'synthetic')
    }
    Write-VeilCefCandidateEvidence $root $evidence $commit $cert
    $manifest = Verify
    function Plan { Get-VeilCefVerifiedRuntimePinPlan $root $evidence $cert.Thumbprint $commit }
    $plan = (Plan) | ConvertFrom-Json
    if ($plan.kind -cne 'cef-admin-runtime-pins' -or $plan.format -ne 1 -or
        $plan.architecture -cne 'win-x64' -or $plan.startupEnabled -isnot [bool] -or $plan.startupEnabled -or
        $plan.sourceCommit -cne $commit -or $plan.cefArchiveSha256 -cne $manifest.cefArchiveSha256 -or
        @($plan.PSObject.Properties).Count -ne 7 -or @($plan.files).Count -ne 4) { throw 'Invalid runtime pin plan.' }
    foreach ($file in $plan.files) {
        if ($file.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
            $file.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $root $file.path)).Hash -or
            $file.bytes -ne (Get-Item -LiteralPath (Join-Path $root $file.path)).Length) { throw 'Wrong runtime pin.' }
    }
    Reject { Get-VeilCefVerifiedRuntimePinPlan $root $evidence ('0'*40) $commit }
    Reject { Get-VeilCefVerifiedRuntimePinPlan $root $evidence $cert.Thumbprint ('b'*40) }
    if ($manifest.installable -or $manifest.startupEnabled -or @($manifest.files).Count -ne 4) { throw 'Invalid manifest.' }
    Reject { Read-VeilCefCandidateEvidence $root $evidence ('0'*40) $commit }
    Reject { Read-VeilCefCandidateEvidence $root $evidence $cert.Thumbprint ('b'*40) }
    Reject { Write-VeilCefCandidateEvidence $root $evidence $commit $cert }
    $dll = Join-Path $root 'libcef.dll'
    [IO.File]::WriteAllText($dll,'modified!')
    Reject { Verify }
    Reject { Plan }
    [IO.File]::WriteAllText($dll,'synthetic')
    $extra = Join-Path $root 'extra.dll'
    [IO.File]::WriteAllText($extra,'synthetic')
    Reject { Verify }
    Remove-Item -LiteralPath $extra
    Remove-Item -LiteralPath $dll
    Reject { Verify }
    [IO.File]::WriteAllText($dll,'synthetic')
    $json = Join-Path $evidence 'cef-candidate.json'
    $original = [IO.File]::ReadAllBytes($json)
    [IO.File]::AppendAllText($json,' ')
    Reject { Verify }
    Reject { Plan }
    [IO.File]::WriteAllBytes($json,$original)
    [void](Verify)
    Write-Host 'PASS: detached CMS signature and exact candidate inventory synthetic tests'
}
finally {
    $cert.Dispose()
    $rsa.Dispose()
    Remove-Item -LiteralPath $work -Recurse -Force
}
