#requires -RunAsAdministrator
# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$SignerThumbprint,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$ImagePath,
    [Parameter(Mandatory)][string]$ProfilePath,
    [Parameter(Mandatory)][guid]$VolumeGuid,
    [Parameter(Mandatory)][guid]$ProtectorId,
    [Parameter(Mandatory)][guid]$RecoveryProtectorId,
    [Parameter(Mandatory)][string]$UserSid,
    [switch]$Diagnostic,
    [switch]$DisposableVm
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use elevated 64-bit Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot 'cef-preflight-registration.ps1')
$program = [Environment]::GetFolderPath('ProgramFiles')
$data = [Environment]::GetFolderPath('CommonApplicationData')
$bin = Join-Path $program 'VEIL'
$ordinaryStage = Join-Path $program 'VEIL.cef-preflight-staging'
$ordinaryDraft = Join-Path $program 'VEIL.cef-preflight-registration'
$diagnosticStage = Join-Path $program 'VEIL.cef-diagnostic-staging'
$diagnosticDraft = Join-Path $program 'VEIL.cef-diagnostic-registration'
if ($Diagnostic -and -not $DisposableVm) { throw 'Explicit disposable VM confirmation required.' }
if ($Diagnostic) {
    $stage = $diagnosticStage; $destination = $diagnosticDraft
    $otherStage = $ordinaryStage; $otherDraft = $ordinaryDraft
} else {
    $stage = $ordinaryStage; $destination = $ordinaryDraft
    $otherStage = $diagnosticStage; $otherDraft = $diagnosticDraft
}
function Assert-NoLiveInstallation {
    if ((Test-Path -LiteralPath $otherStage) -or (Test-Path -LiteralPath $otherDraft) -or
        (Test-Path -LiteralPath $bin) -or (Test-Path -LiteralPath (Join-Path $data 'VEIL')) -or
        (Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'")) {
        throw 'Live or interrupted installation detected. Preserve all state; no automatic recovery.'
    }
}
Assert-NoLiveInstallation
if (-not (Test-Path -LiteralPath $destination)) { throw 'Registration preparation is missing.' }
Assert-VeilAdminPath $destination
if (-not (Test-Path -LiteralPath (Join-Path $destination 'preparation.json') -PathType Leaf)) {
    throw 'Interrupted registration preparation. Preserve files; no automatic recovery.'
}
$image = (Get-Item -LiteralPath $ImagePath -Force).FullName
$parent = Split-Path -Parent $image
if ((Split-Path -Parent $parent) -ne $program -or $parent -in @($bin,$ordinaryStage,$ordinaryDraft,$diagnosticStage,$diagnosticDraft,(Join-Path $program 'VEIL.cef-candidate-staging')) -or
    (Get-Item -LiteralPath $image -Force).PSIsContainer) { throw 'Expected a separate administrator-owned test VHDX directory.' }
Assert-VeilAdminPath $parent
Assert-VeilAdminPath $image
if ((Get-DiskImage -ImagePath $image).Attached) { throw 'Test volume must be detached.' }
$plan = Get-VeilPreflightStagePlan $stage $SignerThumbprint $SourceCommit -Diagnostic:$Diagnostic
[void](Read-VeilPreflightRegistrationDraft $destination $plan $bin $image $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid -Diagnostic:$Diagnostic)
$again = Get-VeilPreflightStagePlan $stage $SignerThumbprint $SourceCommit -Diagnostic:$Diagnostic
if (($plan | ConvertTo-Json -Depth 10 -Compress) -cne ($again | ConvertTo-Json -Depth 10 -Compress)) { throw 'Stage changed during inspection.' }
Assert-NoLiveInstallation
Assert-VeilAdminPath $image
if ((Get-DiskImage -ImagePath $image).Attached) { throw 'Test volume was reattached.' }
Write-Host 'PASS: prepared-only registration snapshot matches verified stage and independent inputs.'
Write-Host 'NOT RUN: registration publication, recovery credential re-verification, installation and startup.'
