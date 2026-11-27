#requires -RunAsAdministrator
# SPDX-License-Identifier: MPL-2.0
[CmdletBinding(DefaultParameterSetName='Verify')]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$SignerThumbprint,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit,
    [Parameter(Mandatory,ParameterSetName='Sign')][switch]$Sign
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use elevated 64-bit Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot 'cef-candidate-evidence.ps1')
if ($Sign) {
    # Both product images must already carry the separately trusted
    # Authenticode signature. This command does not modify the candidate.
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SignerThumbprint"
    Write-VeilCefCandidateEvidence $CandidateRoot $EvidenceDirectory $SourceCommit $certificate
}
[void](Read-VeilCefCandidateEvidence $CandidateRoot $EvidenceDirectory $SignerThumbprint $SourceCommit)
Write-Host 'PASS: signed candidate inventory verified. NOT installable; startup disabled.'
