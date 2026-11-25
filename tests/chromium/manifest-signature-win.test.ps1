# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$root = Join-Path $env:RUNNER_TEMP ('veil-manifest-signature-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$rsa = [Security.Cryptography.RSA]::Create(2048)
$rsa2 = [Security.Cryptography.RSA]::Create(2048)
$cert = $null
$cert2 = $null
try {
    # Ephemeral keys and certificates: no certificate store installation.
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL synthetic manifest fixture',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request2 = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL synthetic second signer',$rsa2,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(1))
    $cert2 = $request2.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(1))
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes('{"format":1,"channel":"internal","architecture":"win-x64","fixture":true}')
    $content = [Security.Cryptography.Pkcs.ContentInfo]::new($bytes)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $signer2 = [Security.Cryptography.Pkcs.CmsSigner]::new($cert2)
    $signer2.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $signer2.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new($content,$true)
    $cms.ComputeSignature($signer,$true)
    $cms.CheckSignature($true)
    [IO.File]::WriteAllBytes((Join-Path $root 'manifest-fixture.json'),$bytes)
    [IO.File]::WriteAllBytes((Join-Path $root 'manifest-fixture.p7s'),$cms.Encode())
    $cms.ComputeSignature($signer2,$true)
    [IO.File]::WriteAllBytes((Join-Path $root 'multi-fixture.p7s'),$cms.Encode())
    $attached = [Security.Cryptography.Pkcs.SignedCms]::new($content,$false)
    $attached.ComputeSignature($signer,$true)
    [IO.File]::WriteAllBytes((Join-Path $root 'attached-fixture.p7s'),$attached.Encode())
    $pin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($cert.RawData))
    & $Executable $root $pin
    if ($LASTEXITCODE -ne 0) { throw "Manifest signature fixture failed: $LASTEXITCODE" }
} finally {
    if ($cert) { $cert.Dispose() }
    if ($cert2) { $cert2.Dispose() }
    $rsa.Dispose()
    $rsa2.Dispose()
    foreach ($name in @('manifest-fixture.json','manifest-fixture.p7s','multi-fixture.p7s','attached-fixture.p7s')) {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    }
    [IO.Directory]::Delete($root)
}
