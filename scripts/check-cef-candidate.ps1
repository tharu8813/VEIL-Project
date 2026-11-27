# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$InventoryPath
)
$ErrorActionPreference = 'Stop'
$root = Get-Item -LiteralPath $CandidateRoot -Force
if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Invalid candidate root.'
}
$inventory = [IO.Path]::GetFullPath($InventoryPath)
$rootPrefix = $root.FullName.TrimEnd('\') + '\'
if ($inventory.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $inventory)) {
    throw 'Inventory must be a new file outside the candidate directory.'
}
. (Join-Path $PSScriptRoot 'cef-candidate-common.ps1')
$files = @(Get-VeilCefCandidateFiles $CandidateRoot)
# A development inventory must not be confused with the signed package manifest.
$result = [ordered]@{ kind='cef-build-candidate-only'; installable=$false; startupEnabled=$false; files=$files }
$json = $result | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($inventory,$json,[Text.UTF8Encoding]::new($false))
Write-Host "PASS: isolated CEF candidate inventory ($($files.Count) files). NOT a signed package."
