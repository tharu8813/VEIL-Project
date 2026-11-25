# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP ('veil-cutover-journal-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
try {
    & $Executable $root
    if ($LASTEXITCODE -ne 0) { throw "Journal storage fixture failed: $LASTEXITCODE" }
} finally {
    foreach ($name in @('prepared.json','registration-replaced.json','activation-committed.json','record.pending','alias.json')) {
        $file = Join-Path $root $name
        if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    }
    [IO.Directory]::Delete($root)
}
