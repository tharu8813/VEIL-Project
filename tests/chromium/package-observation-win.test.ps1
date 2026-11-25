# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$root = Join-Path $env:RUNNER_TEMP ('veil-package-observation-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$rsa = [Security.Cryptography.RSA]::Create(2048)
$cert = $null
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Payload')
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Payload\Chromium')
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Payload\Broker')
    $client = Join-Path $root 'Payload\Chromium\client-fixture.exe'
    $resource = Join-Path $root 'Payload\Broker\resource.bin'
    Copy-Item -LiteralPath $Executable -Destination $client
    [IO.File]::WriteAllBytes($resource,[Text.Encoding]::ASCII.GetBytes('abc'))
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL synthetic package fixture',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(1))
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $data = @{format=1; channel='internal'; architecture='win-x64'; version='1.1.0-alpha.1'; sourceCommit=('b'*40); profileFormat=1; files=@(
        @{path='Chromium/client-fixture.exe'; bytes=(Get-Item -LiteralPath $client).Length; sha256=(Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash},
        @{path='Broker/resource.bin'; bytes=(Get-Item -LiteralPath $resource).Length; sha256=(Get-FileHash -LiteralPath $resource -Algorithm SHA256).Hash}
    )}
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($data | ConvertTo-Json -Depth 6))
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
    $cms.ComputeSignature($signer,$true)
    $cms.CheckSignature($true)
    [IO.File]::WriteAllBytes((Join-Path $root 'manifest-fixture.json'),$bytes)
    [IO.File]::WriteAllBytes((Join-Path $root 'manifest-fixture.p7s'),$cms.Encode())
    $pin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cert.RawData))
    & $Executable $root $pin
    if ($LASTEXITCODE -ne 0) { throw "Package observation fixture failed: $LASTEXITCODE" }
} finally {
    if ($cert) { $cert.Dispose() }
    $rsa.Dispose()
    foreach ($name in @('manifest-fixture.json','manifest-fixture.p7s','Payload\Chromium\client-fixture.exe','Payload\Broker\resource.bin','Payload\Broker\extra.bin')) {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    }
    foreach ($name in @('Payload\Broker','Payload\Chromium','Payload')) {
        $dir = Join-Path $root $name
        if (Test-Path -LiteralPath $dir) { [IO.Directory]::Delete($dir) }
    }
    [IO.Directory]::Delete($root)
}
