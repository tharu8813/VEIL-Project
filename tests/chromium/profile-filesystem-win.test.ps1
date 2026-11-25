# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-profile-fixture-' + [Guid]::NewGuid().ToString('N'))
$target = Join-Path $root 'ordinary'
$nested = Join-Path $target 'nested'
$junction = Join-Path $root 'junction'
$file = Join-Path $root 'file.txt'
function Assert-Observation([string]$Path, [string]$Expected) {
    & $Executable $Path $Expected
    if ($LASTEXITCODE -ne 0) { throw "Unexpected observation for $Expected" }
}
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
try {
    $null = New-Item -ItemType Directory -Path $root
    $null = New-Item -ItemType Directory -Path $target
    $null = New-Item -ItemType Directory -Path $nested
    [IO.File]::WriteAllText($file, 'synthetic fixture')
    Assert-Observation $target 'ordinary'
    Assert-Observation $nested 'ordinary'
    Assert-Observation $nested 'retention'
    Assert-Observation (Join-Path $root 'missing') 'missing'
    Assert-Observation $file 'file'
    Assert-Observation ($target + '\..') 'syntax'
    $null = New-Item -ItemType Junction -Path $junction -Target $target
    Assert-Observation $junction 'junction'
    Assert-Observation (Join-Path $junction 'nested') 'junction'
    if (Test-Path -LiteralPath (Join-Path $root 'missing')) { throw 'Unexpected creation.' }
    if ([IO.File]::ReadAllText($file) -cne 'synthetic fixture') { throw 'Unexpected file mutation.' }
    Write-Output 'PASS: ordinary, missing, file, syntax, junction and ancestor junction observations; no runtime approval.'
} finally {
    # Remove only the fixture junction itself before recursive fixture cleanup.
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
