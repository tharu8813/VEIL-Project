# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-cutover-journal-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
try {
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
    foreach ($name in @('prepared.json','registration-replaced.json','activation-committed.json','record.pending','alias.json','maintenance.lock','alias-lock')) {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    }
    [IO.Directory]::Delete($root)
}
