#requires -RunAsAdministrator
# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$CefEvidenceDirectory,
    [Parameter(Mandatory)][string]$PackageEvidenceDirectory,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$SignerThumbprint,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit,
    [switch]$Diagnostic,
    [switch]$DisposableVm
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use elevated 64-bit Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot 'cef-preflight-staging.ps1')
$bin = [Environment]::GetFolderPath('ProgramFiles')
$data = [Environment]::GetFolderPath('CommonApplicationData')
# Restrict even preparation to a clean isolated validation installation.
if ((Test-Path -LiteralPath (Join-Path $bin 'VEIL')) -or
    (Test-Path -LiteralPath (Join-Path $data 'VEIL')) -or
    (Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'")) {
    throw 'Existing VEIL installation or registration refused. Use a clean disposable VM.'
}
$ordinaryStage = Join-Path $bin 'VEIL.cef-preflight-staging'
$diagnosticStage = Join-Path $bin 'VEIL.cef-diagnostic-staging'
if ($Diagnostic -and -not $DisposableVm) { throw 'Explicit disposable VM confirmation required for diagnostic staging.' }
if ($Diagnostic) {
    if (Test-Path -LiteralPath $ordinaryStage) { throw 'Existing ordinary staging refused for diagnostic preparation.' }
    $stage = $diagnosticStage
} else {
    if (Test-Path -LiteralPath $diagnosticStage) { throw 'Existing diagnostic staging refused for ordinary preparation.' }
    $stage = $ordinaryStage
}
$plan = Copy-VeilVerifiedPreflightPackage $PayloadRoot $CefEvidenceDirectory $PackageEvidenceDirectory $stage $SignerThumbprint $SourceCommit -Diagnostic:$Diagnostic
$plan | ConvertTo-Json -Depth 8
# No installed files, configuration or service changed. Do not execute payload.
