# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-cutover-journal-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$restoreRoot = Join-Path $env:RUNNER_TEMP ('veil-cutover-journal-fixture-' + [Guid]::NewGuid().ToString('N'))
try {
    if (Test-Path -LiteralPath $restoreRoot) { throw 'Restore fixture already exists.' }
    $null = New-Item -ItemType Directory -Path $restoreRoot
    $ownsRestore = $true
    [IO.File]::WriteAllBytes((Join-Path $restoreRoot 'maintenance.lock'),[byte[]]@())
    & $Executable $restoreRoot restore
    if ($LASTEXITCODE -ne 0) { throw "Journal restore fixture failed: $LASTEXITCODE" }
    $lock = Join-Path $root 'maintenance.lock'
    [IO.File]::WriteAllBytes($lock,[byte[]]@())
    # PowerShell owns the exclusive handle while a separate native process
    # attempts both operations. No sleep, polling or timing-dependent race.
    $held = [IO.File]::Open($lock,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    try {
        & $Executable $root busy
        if ($LASTEXITCODE -ne 0) { throw "Journal lock contention failed: $LASTEXITCODE" }
    } finally { $held.Dispose() }
    & $Executable $root normal
    if ($LASTEXITCODE -ne 0) { throw "Journal storage fixture failed: $LASTEXITCODE" }
} finally {
    foreach ($name in @('prepared.json','registration-replaced.json','activation-committed.json','record.pending','alias.json','maintenance.lock','alias-lock','registration-fixture.json','registration-backup.json','backup.pending')) {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    }
    [IO.Directory]::Delete($root)
    if ($ownsRestore) {
        foreach ($name in @('maintenance.lock','prepared.json','registration-fixture.json','registration-backup.json','restore-started.json','restore-copy.pending','restore-displaced.json','backup.pending','record.pending')) {
            $file = Join-Path $restoreRoot $name
            if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
        }
        [IO.Directory]::Delete($restoreRoot)
    }
}
