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
    [Parameter(Mandatory)][string]$UserSid
)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use elevated 64-bit Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot 'cef-preflight-registration.ps1')
. (Join-Path $PSScriptRoot 'bitlocker-recovery.ps1')
$program = [Environment]::GetFolderPath('ProgramFiles')
$data = [Environment]::GetFolderPath('CommonApplicationData')
$bin = Join-Path $program 'VEIL'
$destination = Join-Path $program 'VEIL.cef-preflight-registration'
$stage = Join-Path $program 'VEIL.cef-preflight-staging'
function Assert-Clean {
    if ((Test-Path -LiteralPath $bin) -or (Test-Path -LiteralPath (Join-Path $data 'VEIL')) -or
        (Test-Path -LiteralPath $destination) -or (Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'")) {
        throw 'Existing installation or registration preparation refused.'
    }
}
Assert-Clean
$plan = Get-VeilPreflightStagePlan $stage $SignerThumbprint $SourceCommit
$image = (Get-Item -LiteralPath $ImagePath -Force).FullName
$parent = Split-Path -Parent $image
if ((Split-Path -Parent $parent) -ne $program -or $parent -in @($bin,$stage,(Join-Path $program 'VEIL.cef-candidate-staging'),$destination) -or
    (Get-Item -LiteralPath $image -Force).PSIsContainer) { throw 'Expected a separate administrator-owned test VHDX directory under Program Files.' }
Assert-VeilAdminPath $parent
Assert-VeilAdminPath $image
$draft = New-VeilPreflightRegistrationDraft $plan $bin $image $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid ([guid]::NewGuid().ToString('N'))
if ((Get-DiskImage -ImagePath $image).Attached) { throw 'Test image must start detached.' }
# Use the existing recovery verification with an explicitly supplied existing
# recovery ID; never create a new protector or log secrets for this preparation.
$verified = Register-VeilRecoveryBackup $image $draft['broker.json'].volumeGuid $ProtectorId $RecoveryProtectorId
if ($verified -ne $RecoveryProtectorId -or (Get-DiskImage -ImagePath $image).Attached) { throw 'Recovery verification or detach failed.' }
Assert-Clean
$again = Get-VeilPreflightStagePlan $stage $SignerThumbprint $SourceCommit
if (($plan | ConvertTo-Json -Depth 10 -Compress) -cne ($again | ConvertTo-Json -Depth 10 -Compress)) { throw 'Stage plan changed during recovery verification.' }
Assert-VeilAdminPath $image
if ((Get-DiskImage -ImagePath $image).Attached) { throw 'Test volume was reattached.' }
[void](Write-VeilPreflightRegistrationDraft $destination $draft $again)
Write-Host 'PASS: registration preparation only. No service or live registration published.'
