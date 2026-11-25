# SPDX-License-Identifier: MPL-2.0
# Test-only bridge to the actual package byte-copy function; no installation or trust changes.
param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\package-common.ps1')
try {
    Copy-VeilPackageFiles $Source $Destination
    [pscustomobject]@{ status = 'COPIED'; ioCodes = @() } | ConvertTo-Json -Compress
} catch {
    $codes = @()
    for ($ex = $_.Exception; $null -ne $ex; $ex = $ex.InnerException) {
        if ($ex -is [IO.IOException]) { $codes += ($ex.HResult -band 65535) }
    }
    [pscustomobject]@{ status = 'FAILED'; ioCodes = @($codes); type = $_.Exception.GetType().FullName } |
        ConvertTo-Json -Compress
}
