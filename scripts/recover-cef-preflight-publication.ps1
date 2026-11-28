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
    [Parameter(Mandatory)][switch]$DisposableVm,
    [switch]$Resume,
    [switch]$Diagnostic
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
$prefix = if ($Diagnostic) { 'VEIL.cef-diagnostic' } else { 'VEIL.cef-preflight' }
$other = if ($Diagnostic) { 'VEIL.cef-preflight' } else { 'VEIL.cef-diagnostic' }
$suffix = if ($Diagnostic) { '.cef-diagnostic-pending' } else { '.cef-preflight-pending' }
$otherSuffix = if ($Diagnostic) { '.cef-preflight-pending' } else { '.cef-diagnostic-pending' }
$stage = Join-Path $program ($prefix + '-staging')
$prepared = Join-Path $program ($prefix + '-registration')
$transaction = Join-Path $program ($prefix + '-publication')
$otherPaths = @((Join-Path $program ($other + '-staging')),(Join-Path $program ($other + '-registration')),
    (Join-Path $program ($other + '-publication')),($bin + $otherSuffix),($registration + $otherSuffix))
$image = (Get-Item -LiteralPath $ImagePath -Force).FullName
$parent = Split-Path -Parent $image
if ((Split-Path -Parent $parent) -ne $program -or
    $parent -in @($bin,$stage,$prepared,$transaction,($bin + $suffix),(Join-Path $program 'VEIL.cef-candidate-staging')) -or
    (Get-Item -LiteralPath $image -Force).PSIsContainer) { throw 'Expected a separate administrator-owned test VHDX directory.' }
Assert-VeilAdminPath $program
# ProgramData itself permits some non-admin creation; the new pending
# registration directory must have the restricted ACL at creation.
if ((Get-Item -LiteralPath $data -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected ProgramData refused.' }
$requireSafe = {
    foreach ($path in $otherPaths) {
        if (Test-Path -LiteralPath $path) { throw 'Opposite-mode preparation or publication refused.' }
    }
    Assert-VeilAdminPath $parent
    Assert-VeilAdminPath $image
    if ((Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'") -or (Get-DiskImage -ImagePath $image).Attached) {
        throw 'Service exists or test image is attached. Preserve all publication state.'
    }
}
$observed = Get-VeilPreflightPublicationState $stage $prepared $bin $registration $transaction $SignerThumbprint $SourceCommit $image $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid $requireSafe -Diagnostic:$Diagnostic
if (-not $Resume) {
    Write-Host ('Publication state: ' + $observed.state)
    Write-Host 'Read-only inspection. No service created or started.'
    return
}
if ($observed.state -eq 'incomplete-copies') { throw 'Incomplete copies; no automatic recovery.' }
if ($observed.state -ne 'files-published-only') {
    # Reverify the existing recovery protector and detached test volume.
    $verified = Register-VeilRecoveryBackup $image ('\\?\Volume{' + $VolumeGuid.ToString() + '}\') $ProtectorId $RecoveryProtectorId
    if ($verified -ne $RecoveryProtectorId) { throw 'Recovery protector verification failed.' }
    & $requireSafe
    [void](Resume-VeilPreflightPublication $stage $prepared $bin $registration $transaction $SignerThumbprint $SourceCommit $image $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid $requireSafe -Diagnostic:$Diagnostic)
}
Write-Host 'PASS: verified files-published-only state. No service registered or started.'
