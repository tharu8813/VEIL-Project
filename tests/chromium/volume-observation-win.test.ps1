# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-volume-fixture-' + [Guid]::NewGuid().ToString('N'))
$junction = Join-Path $root 'junction'
$file = Join-Path $root 'file'
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
try {
    $null = New-Item -ItemType Directory -Path $root
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'nested')
    [IO.File]::WriteAllText($file, 'synthetic fixture')
    $null = New-Item -ItemType Junction -Path $junction -Target $root
    & $Executable $root
    if ($LASTEXITCODE -ne 0) { throw "Volume observation failed: $LASTEXITCODE" }
    if (Test-Path -LiteralPath (Join-Path $root 'missing')) { throw 'Unexpected creation.' }
    if ([IO.File]::ReadAllText($file) -cne 'synthetic fixture') { throw 'Unexpected mutation.' }
} finally {
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
