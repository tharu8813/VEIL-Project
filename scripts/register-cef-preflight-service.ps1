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
. (Join-Path $PSScriptRoot 'cef-preflight-service.ps1')
. (Join-Path $PSScriptRoot 'cef-preflight-bootstrap.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'CefPreflightBootstrapRegistry.cs')
. (Join-Path $PSScriptRoot 'bitlocker-recovery.ps1')
$program = [Environment]::GetFolderPath('ProgramFiles')
$data = [Environment]::GetFolderPath('CommonApplicationData')
$bin = Join-Path $program 'VEIL'
$registration = Join-Path $data 'VEIL'
$stage = Join-Path $program 'VEIL.cef-preflight-staging'
$prepared = Join-Path $program 'VEIL.cef-preflight-registration'
$transaction = Join-Path $program 'VEIL.cef-preflight-publication'
$serviceJournal = Join-Path $program 'VEIL.cef-preflight-service'
$command = '"{0}" --service' -f (Join-Path $bin 'Broker\Veil.VolumeBroker.exe')
$image = (Get-Item -LiteralPath $ImagePath -Force).FullName
$parent = Split-Path -Parent $image
if ((Split-Path -Parent $parent) -ne $program -or
    $parent -in @($bin,$stage,$prepared,$transaction,$serviceJournal,($bin + '.cef-preflight-pending'),(Join-Path $program 'VEIL.cef-candidate-staging')) -or
    (Get-Item -LiteralPath $image -Force).PSIsContainer) { throw 'Expected separate administrator-owned test VHDX directory.' }
Assert-VeilAdminPath $program
if ((Get-Item -LiteralPath $data -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected ProgramData refused.' }
$readService = { Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'" }
if ((Test-Path -LiteralPath $serviceJournal) -or $null -ne (& $readService)) { throw 'Existing or interrupted service registration refused.' }
$requireSafe = {
    Assert-VeilAdminPath $parent
    Assert-VeilAdminPath $image
    if ((Get-DiskImage -ImagePath $image).Attached) { throw 'Test volume must be detached.' }
    $current = & $readService
    if ($null -ne $current) { Assert-VeilDisabledPreflightService $current $command }
}
$verifyFiles = {
    Get-VeilPreflightPublicationState $stage $prepared $bin $registration $transaction $SignerThumbprint $SourceCommit $image $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid $requireSafe
}
$initial = & $verifyFiles
if ($initial.state -cne 'files-published-only') { throw 'Complete file publication required.' }
$verified = Register-VeilRecoveryBackup $image ('\\?\Volume{' + $VolumeGuid.ToString() + '}\') $ProtectorId $RecoveryProtectorId
if ($verified -ne $RecoveryProtectorId) { throw 'Recovery protector verification failed.' }
& $requireSafe
Assert-VeilPreflightPublicationEqual (& $verifyFiles) $initial
$bootstrap = Get-VeilInstalledBootstrapPlan $registration $ProfilePath $VolumeGuid
[Veil.Preflight.BootstrapRegistry]::Create($bootstrap.ProductRoot,$bootstrap.VolumeGuid,$bootstrap.VolumeId)
$verifyPublication = {
    $verifiedFiles = & $verifyFiles
    Assert-VeilInstalledBootstrap $registration $ProfilePath $VolumeGuid
    return $verifiedFiles
}
Assert-VeilPreflightPublicationEqual (& $verifyPublication) $initial
$sc = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
$create = {
    param($binary)
    New-Service -Name 'Veil.VolumeBroker' -BinaryPathName $binary -DisplayName 'VEIL Volume Broker (CEF preflight)' -StartupType Disabled | Out-Null
}
$setDacl = {
    param($sddl)
    & $sc sdset 'Veil.VolumeBroker' $sddl | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Service DACL write failed.' }
}
$readDacl = {
    $lines = @(& $sc sdshow 'Veil.VolumeBroker')
    if ($LASTEXITCODE -ne 0) { throw 'Service DACL read failed.' }
    $descriptors = @($lines | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^(O:|G:|D:|S:)' })
    if ($descriptors.Count -ne 1) { throw 'Unexpected SCM descriptor output.' }
    return $descriptors[0]
}
[void](Register-VeilDisabledPreflightService $serviceJournal $command $verifyPublication $readService $create $setDacl $readDacl)
Write-Host 'PASS: bootstrap registry and broker registered disabled and stopped. Service startup and CEF initialization NOT RUN.'
