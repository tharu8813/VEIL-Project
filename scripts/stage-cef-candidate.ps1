#requires -RunAsAdministrator
# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$SignerThumbprint,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Use elevated 64-bit Windows PowerShell 5.1.'
}
. (Join-Path $PSScriptRoot 'cef-candidate-staging.ps1')
$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$stage = Join-Path $programFiles 'VEIL.cef-candidate-staging'
# Fixed sibling of VEIL, not the installed tree or existing maintenance stage.
[void](Copy-VeilCefVerifiedCandidate $CandidateRoot $EvidenceDirectory $stage $SignerThumbprint $SourceCommit)
Write-Host 'PASS: signed CEF candidate staged and reverified. NOT installable; startup disabled.'
Write-Host 'Existing installation, registration and service were not changed.'
