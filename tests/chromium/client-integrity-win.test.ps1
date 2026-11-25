# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-client-integrity-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$file = Join-Path $root 'client-fixture.exe'
try {
    Copy-Item -LiteralPath $Executable -Destination $file
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    & $Executable $root unsigned $hash
    if ($LASTEXITCODE -ne 0) { throw "Unsigned integrity fixture failed: $LASTEXITCODE" }
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
        if ($signature.Status -eq 'Valid' -and $signature.SignatureType -eq 'Authenticode') {
            $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            & $Executable $root signed $hash
            if ($LASTEXITCODE -ne 0) { throw "Signed integrity fixture failed: $LASTEXITCODE" }
            $positive = $true
            break
        }
        [IO.File]::Delete($file)
    }
    if (-not $positive) { Write-Host 'SKIP: no independently valid embedded signed fixture; native positive combined observation NOT RUN' }
} finally {
    foreach ($name in @('alias.exe','client-fixture.exe')) {
        $item = Join-Path $root $name
        if (Test-Path -LiteralPath $item) { [IO.File]::Delete($item) }
    }
    [IO.Directory]::Delete($root)
}
