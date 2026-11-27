#requires -RunAsAdministrator
# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$SignerThumbprint,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Use elevated 64-bit Windows PowerShell 5.1.'
}
. (Join-Path $PSScriptRoot 'cef-candidate-evidence.ps1')
$candidate = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'VEIL.cef-candidate-staging\candidate'
# No file writes. Never redirect directly into the live VEIL registration.
Get-VeilCefVerifiedRuntimePinPlan $candidate $EvidenceDirectory $SignerThumbprint $SourceCommit
