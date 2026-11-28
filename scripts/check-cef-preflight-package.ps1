#requires -RunAsAdministrator
# SPDX-License-Identifier: MPL-2.0
[CmdletBinding(DefaultParameterSetName='Verify')]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$CefEvidenceDirectory,
    [Parameter(Mandatory)][string]$PackageEvidenceDirectory,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$SignerThumbprint,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit,
    [switch]$Diagnostic,
    [Parameter(Mandatory,ParameterSetName='Sign')][switch]$Sign
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use elevated 64-bit Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot 'cef-preflight-package.ps1')
if ($Sign) {
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SignerThumbprint"
    Write-VeilPreflightPackageEvidence $PayloadRoot $CefEvidenceDirectory $PackageEvidenceDirectory $SourceCommit $cert -Diagnostic:$Diagnostic
}
[void](Read-VeilPreflightPackageEvidence $PayloadRoot $CefEvidenceDirectory $PackageEvidenceDirectory $SignerThumbprint $SourceCommit -Diagnostic:$Diagnostic)
if ($Diagnostic) {
    Write-Host 'PASS: separate signed diagnostic payload inventory. NOT installable; baseline runtime trust NOT verified; startup disabled.'
} else {
    Write-Host 'PASS: separate signed preflight payload inventory. NOT installable; startup disabled.'
}
