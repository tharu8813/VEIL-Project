# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-client-signer-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$file = Join-Path $root 'client-fixture.exe'
try {
    Copy-Item -LiteralPath $Executable -Destination $file
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    & $Executable $root unsigned $hash ('a' * 64)
    if ($LASTEXITCODE -ne 0) { throw "Unsigned signer fixture failed: $LASTEXITCODE" }
    [IO.File]::Delete($file)
    $positive = $false
    foreach ($candidate in @(
        (Join-Path $PSHOME 'pwsh.exe'),
        (Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'),
        (Join-Path $env:windir 'System32\msiexec.exe'),
        (Join-Path $env:windir 'System32\notepad.exe')
    )) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        Copy-Item -LiteralPath $candidate -Destination $file
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -eq 'Valid' -and $signature.SignatureType -eq 'Authenticode' -and $signature.SignerCertificate) {
            $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            $pin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData))
            & $Executable $root signed $hash $pin
            if ($LASTEXITCODE -ne 0) { throw "Signed signer fixture failed: $LASTEXITCODE" }
            $positive = $true
            break
        }
        [IO.File]::Delete($file)
    }
    if (-not $positive) { Write-Host 'SKIP: no independently valid embedded signed fixture; native signer pin positive NOT RUN' }
} finally {
    if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($root)
}
