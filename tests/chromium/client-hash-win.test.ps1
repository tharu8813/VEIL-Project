# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-client-hash-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$file = Join-Path $root 'client-fixture.exe'
try {
    Copy-Item -LiteralPath $Executable -Destination $file
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    & $Executable $root $hash
    if ($LASTEXITCODE -ne 0) { throw "Client hash fixture failed: $LASTEXITCODE" }
} finally {
    foreach ($name in @('alias.exe','client-fixture.exe')) {
        $item = Join-Path $root $name
        if (Test-Path -LiteralPath $item) { [IO.File]::Delete($item) }
    }
    [IO.Directory]::Delete($root)
}
