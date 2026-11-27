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
    [Parameter(Mandatory)][switch]$DisposableVm
)
$ErrorActionPreference = 'Stop'
if (-not $DisposableVm -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Explicit disposable VM confirmation and elevated Windows PowerShell 5.1 x64 required.'
}
. (Join-Path $PSScriptRoot 'cef-preflight-publication.ps1')
. (Join-Path $PSScriptRoot 'bitlocker-recovery.ps1')
$program = [Environment]::GetFolderPath('ProgramFiles')
$data = [Environment]::GetFolderPath('CommonApplicationData')
$bin = Join-Path $program 'VEIL'
$registration = Join-Path $data 'VEIL'
$stage = Join-Path $program 'VEIL.cef-preflight-staging'
$prepared = Join-Path $program 'VEIL.cef-preflight-registration'
$transaction = Join-Path $program 'VEIL.cef-preflight-publication'
$image = (Get-Item -LiteralPath $ImagePath -Force).FullName
$parent = Split-Path -Parent $image
if ((Split-Path -Parent $parent) -ne $program -or
    $parent -in @($bin,$stage,$prepared,$transaction,($bin + '.cef-preflight-pending'),(Join-Path $program 'VEIL.cef-candidate-staging')) -or
    (Get-Item -LiteralPath $image -Force).PSIsContainer) { throw 'Expected a separate administrator-owned test VHDX directory.' }
Assert-VeilAdminPath $program
# ProgramData itself permits some non-admin creation; the new pending
# registration directory must have the restricted ACL at creation.
if ((Get-Item -LiteralPath $data -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected ProgramData refused.' }
$requireSafe = {
    Assert-VeilAdminPath $parent
    Assert-VeilAdminPath $image
    if ((Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'") -or (Get-DiskImage -ImagePath $image).Attached) {
        throw 'Service exists or test image is attached. Preserve all publication state.'
    }
}
foreach ($path in @($bin,$registration,$transaction,($bin + '.cef-preflight-pending'),($registration + '.cef-preflight-pending'))) {
    if (Test-Path -LiteralPath $path) { throw 'Existing installation or interrupted publication refused.' }
}
& $requireSafe
$plan = Get-VeilPreflightStagePlan $stage $SignerThumbprint $SourceCommit
[void](Read-VeilPreflightRegistrationDraft $prepared $plan $bin $image $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid)
# The prepared journal alone is not evidence of recovery verification.
$verified = Register-VeilRecoveryBackup $image ('\\?\Volume{' + $VolumeGuid.ToString() + '}\') $ProtectorId $RecoveryProtectorId
if ($verified -ne $RecoveryProtectorId) { throw 'Recovery protector verification failed.' }
& $requireSafe
[void](Publish-VeilPreflightFiles $stage $prepared $bin $registration $transaction $SignerThumbprint $SourceCommit $image $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid $requireSafe)
Write-Host 'PASS: preflight files published only. Service not registered or started; CEF disabled.'
