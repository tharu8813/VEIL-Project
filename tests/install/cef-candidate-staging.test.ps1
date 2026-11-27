# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = 'Stop'
# Test-only: relaxed certificate chain, Authenticode and admin ACL.
# Signature mathematics and all byte copies/checks are real.
$dir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\scripts'))
$source = [IO.File]::ReadAllText((Join-Path $dir 'cef-candidate-evidence.ps1'))
$source = $source.Replace('$PSScriptRoot',("'" + $dir.Replace("'","''") + "'"))
if (-not $source.Contains('$cms.CheckSignature($false)')) { throw 'CMS contract changed.' }
. ([scriptblock]::Create($source.Replace('$cms.CheckSignature($false)','$cms.CheckSignature($true)')))
$stageSource = [IO.File]::ReadAllText((Join-Path $dir 'cef-candidate-staging.ps1'))
$import = ". (Join-Path " + '$PSScriptRoot' + " 'cef-candidate-evidence.ps1')"
if (-not $stageSource.Contains($import)) { throw 'Staging import changed.' }
. ([scriptblock]::Create($stageSource.Replace($import,'')))
function Assert-VeilAdminPath([string]$Path) { }
function Assert-VeilSignedImage([string]$Path,[string]$Thumbprint) { }
function New-VeilRestrictedDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) { throw 'Already exists.' }
    [void][IO.Directory]::CreateDirectory($Path)
}
$rsa = [Security.Cryptography.RSA]::Create(2048)
$req = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL candidate staging test',$rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
$cert = $req.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5),[DateTimeOffset]::Now.AddHours(1))
$work = Join-Path ([IO.Path]::GetTempPath()) ('veil-cef-stage-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'source'
$evidence = Join-Path $work 'proof'
$stage = Join-Path $work 'staged'
$commit = 'a'*40
[void][IO.Directory]::CreateDirectory($root)
[void][IO.Directory]::CreateDirectory($evidence)
function Reject([scriptblock]$Action) {
    $rejected=$false
    try { [void](& $Action) } catch { $rejected=$true }
    if (-not $rejected) { throw 'Invalid staging accepted.' }
}
try {
    foreach ($file in @('veil-cef-product.exe','veil-cef-product.dll','libcef.dll','icudtl.dat','locales/en-US.pak')) {
        $path = Join-Path $root $file
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
        [IO.File]::WriteAllText($path,'synthetic')
    }
    Write-VeilCefCandidateEvidence $root $evidence $commit $cert
    $manifest = Copy-VeilCefVerifiedCandidate $root $evidence $stage $cert.Thumbprint $commit
    if ($manifest.installable -or $manifest.startupEnabled -or @($manifest.files).Count -ne 5) { throw 'Staging contract changed.' }
    [void](Read-VeilCefCandidateEvidence (Join-Path $stage 'candidate') (Join-Path $stage 'evidence') $cert.Thumbprint $commit)
    $marker = Join-Path $stage 'preserve.txt'
    [IO.File]::WriteAllText($marker,'preserve')
    Reject { Copy-VeilCefVerifiedCandidate $root $evidence $stage $cert.Thumbprint $commit }
    if ([IO.File]::ReadAllText($marker) -cne 'preserve') { throw 'Existing staging modified.' }
    Reject { Copy-VeilCefVerifiedCandidate $root $evidence (Join-Path $root 'bad') $cert.Thumbprint $commit }
    $bad = Join-Path $work 'bad'
    Reject { Copy-VeilCefVerifiedCandidate $root $evidence $bad ('0'*40) $commit }
    if (Test-Path -LiteralPath $bad) { throw 'Bad signer created staging.' }
    [IO.File]::AppendAllText((Join-Path $root 'libcef.dll'),'modified')
    Reject { Copy-VeilCefVerifiedCandidate $root $evidence $bad $cert.Thumbprint $commit }
    if (Test-Path -LiteralPath $bad) { throw 'Modified source created staging.' }
    Write-Host 'PASS: signed CEF candidate copy, revalidation and existing-stage preservation'
}
finally {
    $cert.Dispose()
    $rsa.Dispose()
    Remove-Item -LiteralPath $work -Recurse -Force
}
