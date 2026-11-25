# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-payload-tree-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$junction = Join-Path $root 'Chromium\junction'
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Chromium')
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Broker')
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Broker\nested')
    [IO.File]::WriteAllBytes((Join-Path $root 'Chromium\client-fixture.exe'),[Text.Encoding]::ASCII.GetBytes('abc'))
    [IO.File]::WriteAllBytes((Join-Path $root 'Broker\nested\resource.bin'),[byte[]]@())
    & $Executable $root normal
    if ($LASTEXITCODE -ne 0) { throw "Payload tree fixture failed: $LASTEXITCODE" }
    $null = New-Item -ItemType Junction -Path $junction -Target (Join-Path $root 'Broker')
    & $Executable $root junction
    if ($LASTEXITCODE -ne 0) { throw "Junction fixture failed: $LASTEXITCODE" }
} finally {
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    foreach ($name in @('Chromium\client-fixture.exe','Chromium\alias.exe','Chromium\extra.bin','Broker\nested\resource.bin','extra.bin')) {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    }
    foreach ($name in @('Broker\nested','Broker','Chromium')) {
        $dir = Join-Path $root $name
        if (Test-Path -LiteralPath $dir) { [IO.Directory]::Delete($dir) }
    }
    [IO.Directory]::Delete($root)
}
