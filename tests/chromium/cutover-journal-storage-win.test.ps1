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
    # Expected nonzero native exit codes are checked explicitly.
    $PSNativeCommandUseErrorActionPreference = $false
    foreach ($stage in @('marker','copy','displaced','restored')) {
        $crashRoot = Join-Path $env:RUNNER_TEMP ('veil-cutover-journal-fixture-' + [Guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $crashRoot) { throw 'Crash fixture already exists.' }
        $null = New-Item -ItemType Directory -Path $crashRoot
        try {
            [IO.File]::WriteAllBytes((Join-Path $crashRoot 'maintenance.lock'),[byte[]]@())
            & $Executable $crashRoot "crash-$stage"
            if ($LASTEXITCODE -ne 77) { throw "Crash boundary $stage not reached: $LASTEXITCODE" }
            # Reopen in a separate process: verify lock release but refuse any
            # attempt to resume, activate, back up or restore automatically.
            & $Executable $crashRoot inspect-crash
            if ($LASTEXITCODE -ne 0) { throw "Crash inspection failed at $stage : $LASTEXITCODE" }
            $marker = Join-Path $crashRoot 'restore-started.json'
            $registration = Join-Path $crashRoot 'registration-fixture.json'
            $backup = Join-Path $crashRoot 'registration-backup.json'
            $copy = Join-Path $crashRoot 'restore-copy.pending'
            $displaced = Join-Path $crashRoot 'restore-displaced.json'
            $prepared = Join-Path $crashRoot 'prepared.json'
            if ([IO.File]::ReadAllText($backup) -cne 'abc') { throw 'Backup changed.' }
            if (-not (Test-Path -LiteralPath $marker)) { throw 'Missing restore marker.' }
            if (Test-Path -LiteralPath (Join-Path $crashRoot 'activation-committed.json')) { throw 'Unexpected activation record.' }
            if (Test-Path -LiteralPath (Join-Path $crashRoot 'registration-replaced.json')) { throw 'Unexpected replacement record.' }
            if ($stage -eq 'marker') {
                if ((Get-Item -LiteralPath $marker).Length -ne 0 -or
                    [IO.File]::ReadAllText($registration) -cne 'hello' -or
                    (Test-Path -LiteralPath $copy) -or (Test-Path -LiteralPath $displaced)) {
                    throw 'Unexpected marker-boundary layout.'
                }
            } else {
                if ([IO.File]::ReadAllText($marker) -cne [IO.File]::ReadAllText($prepared)) {
                    throw 'Restore marker changed.'
                }
                if ($stage -eq 'copy') {
                    if ([IO.File]::ReadAllText($copy) -cne 'abc' -or
                        [IO.File]::ReadAllText($registration) -cne 'hello' -or
                        (Test-Path -LiteralPath $displaced)) { throw 'Unexpected copy-boundary layout.' }
                } elseif ($stage -eq 'displaced') {
                    if ([IO.File]::ReadAllText($copy) -cne 'abc' -or
                        [IO.File]::ReadAllText($displaced) -cne 'hello' -or
                        (Test-Path -LiteralPath $registration)) { throw 'Unexpected displaced-boundary layout.' }
                } else {
                    if ([IO.File]::ReadAllText($registration) -cne 'abc' -or
                        [IO.File]::ReadAllText($displaced) -cne 'hello' -or
                        (Test-Path -LiteralPath $copy)) { throw 'Unexpected restored-boundary layout.' }
                }
            }
            Write-Output "PASS: abrupt process termination at $stage; exact fixture layout and restart refusal verified"
        } finally {
            foreach ($name in @('maintenance.lock','prepared.json','registration-fixture.json','registration-backup.json','restore-started.json','restore-copy.pending','restore-displaced.json','backup.pending','record.pending','registration-replaced.json','activation-committed.json')) {
                $file = Join-Path $crashRoot $name
                if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
            }
            [IO.Directory]::Delete($crashRoot)
        }
    }
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
