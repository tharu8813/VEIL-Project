# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$root = Join-Path $env:RUNNER_TEMP ('veil-manifest-client-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$rsa = [Security.Cryptography.RSA]::Create(2048)
$cert = $null
try {
    $dir = Join-Path $root 'Chromium'
    $null = New-Item -ItemType Directory -Path $dir
    $client = Join-Path $dir 'client-fixture.exe'
    Copy-Item -LiteralPath $Executable -Destination $client
    $hash = (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash
    $length = (Get-Item -LiteralPath $client).Length
    $wrong = if ($hash[0] -eq 'A') { 'B' + $hash.Substring(1) } else { 'A' + $hash.Substring(1) }
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL synthetic client fixture',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(1))
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    function Write-Fixture([string]$Name,[long]$Length,[string]$Hash) {
        $data = @{format=1; channel='internal'; architecture='win-x64'; version='1.1.0-alpha.1'; sourceCommit=('b'*40); profileFormat=1; files=@(@{path='Chromium/client-fixture.exe'; bytes=$Length; sha256=$Hash})}
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($data | ConvertTo-Json -Depth 6))
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
        $cms.ComputeSignature($signer,$true)
        $cms.CheckSignature($true)
        [IO.File]::WriteAllBytes((Join-Path $root ($Name + '-fixture.json')),$bytes)
        [IO.File]::WriteAllBytes((Join-Path $root ($Name + '-fixture.p7s')),$cms.Encode())
    }
    Write-Fixture 'manifest' $length $hash
    Write-Fixture 'size' ($length + 1) $hash
    Write-Fixture 'hash' $length $wrong
    $pin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cert.RawData))
    & $Executable $root $pin
    if ($LASTEXITCODE -ne 0) { throw "Manifest client fixture failed: $LASTEXITCODE" }
} finally {
    if ($cert) { $cert.Dispose() }
    $rsa.Dispose()
    foreach ($name in @('manifest-fixture.json','manifest-fixture.p7s','size-fixture.json','size-fixture.p7s','hash-fixture.json','hash-fixture.p7s','Chromium\client-fixture.exe','Chromium\alias.exe')) {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    }
    $dir = Join-Path $root 'Chromium'
    if (Test-Path -LiteralPath $dir) { [IO.Directory]::Delete($dir) }
    [IO.Directory]::Delete($root)
}
