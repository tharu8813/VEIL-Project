# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-diagnostic-fixture-' + [Guid]::NewGuid().ToString('N'))
$crash = Join-Path $root 'Crashpad'
$file = Join-Path $root 'Diagnostics'
$junction = Join-Path $root 'Local_Traces'
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
try {
    $null = New-Item -ItemType Directory -Path $root
    $null = New-Item -ItemType Directory -Path $crash
    [IO.File]::WriteAllText($file, 'synthetic diagnostic fixture')
    $null = New-Item -ItemType Junction -Path $junction -Target $crash
    & $Executable $root
    if ($LASTEXITCODE -ne 0) { throw "Diagnostic observation failed: $LASTEXITCODE" }
    if (Test-Path -LiteralPath (Join-Path $root 'missing')) { throw 'Unexpected creation.' }
    if ([IO.File]::ReadAllText($file) -cne 'synthetic diagnostic fixture') { throw 'Unexpected mutation.' }
    if (@(Get-ChildItem -LiteralPath $crash -Force).Count -ne 0) { throw 'Unexpected diagnostic write.' }
} finally {
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
