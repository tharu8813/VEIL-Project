# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-config-path-fixture-' + [Guid]::NewGuid().ToString('N'))
$junction = Join-Path $root 'junction'
$file = Join-Path $root 'client-fixture.json'
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
try {
    $null = New-Item -ItemType Directory -Path $root
    [IO.File]::WriteAllText($file, 'synthetic-only')
    $null = New-Item -ItemType Junction -Path $junction -Target $root
    & $Executable $root
    if ($LASTEXITCODE -ne 0) { throw "Config path observation failed: $LASTEXITCODE" }
    if (Test-Path -LiteralPath (Join-Path $root 'missing')) { throw 'Unexpected creation.' }
} finally {
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
