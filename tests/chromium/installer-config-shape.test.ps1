# SPDX-License-Identifier: MPL-2.0
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
# Verify the actual writer shape without executing the privileged installer.
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../scripts/broker-service.ps1') -Raw
$anchor = 'volumeId=$volumeId; pipeName=$pipe; brokerImage=$brokerImage; profilePath=$ProfilePath; requireWindowsService=$true'
if (-not $source.Contains($anchor) -or
    -not $source.Contains('} | ConvertTo-Json | Set-Content -LiteralPath $clientConfig -Encoding UTF8') -or
    -not $source.Contains('$pipe = ''VEIL.VolumeBroker''') -or
    -not $source.Contains('$binRoot = Join-Path $env:ProgramFiles ''VEIL''') -or
    -not $source.Contains('$brokerImage = Join-Path $binRoot ''Broker\Veil.VolumeBroker.exe''')) {
    throw 'Installer writer changed; review fixture contract.'
}
$volumeId = '0123456789abcdef0123456789abcdef'
$pipe = 'VEIL.VolumeBroker'
$brokerImage = 'C:\Program Files\VEIL\Broker\Veil.VolumeBroker.exe'
$ProfilePath = 'D:\VEIL Profile'
$root = Join-Path $env:RUNNER_TEMP ('veil-installer-shape-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $root) { throw 'Fixture already exists.' }
$null = New-Item -ItemType Directory -Path $root
$file = Join-Path $root 'client-fixture.json'
try {
    foreach ($encoding in @('UTF8','utf8BOM')) {
        @{
            volumeId=$volumeId; pipeName=$pipe; brokerImage=$brokerImage; profilePath=$ProfilePath; requireWindowsService=$true
        } | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding $encoding
        & $Executable $file
        if ($LASTEXITCODE -ne 0) { throw "Installer shape test failed: $LASTEXITCODE" }
    }
} finally {
    if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($root)
}
