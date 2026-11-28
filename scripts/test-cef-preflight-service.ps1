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
    [switch]$RunProbe,
    [switch]$Diagnostic
)
$ErrorActionPreference = 'Stop'
if ($Diagnostic -and $RunProbe) { throw 'Diagnostic service startup is not yet supported by this tool.' }
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
$prefix = if ($Diagnostic) { 'VEIL.cef-diagnostic' } else { 'VEIL.cef-preflight' }
$other = if ($Diagnostic) { 'VEIL.cef-preflight' } else { 'VEIL.cef-diagnostic' }
$suffix = if ($Diagnostic) { '.cef-diagnostic-pending' } else { '.cef-preflight-pending' }
$otherSuffix = if ($Diagnostic) { '.cef-preflight-pending' } else { '.cef-diagnostic-pending' }
$stage = Join-Path $program ($prefix + '-staging')
$prepared = Join-Path $program ($prefix + '-registration')
$transaction = Join-Path $program ($prefix + '-publication')
$serviceJournal = Join-Path $program ($prefix + '-service')
$otherPaths = @((Join-Path $program ($other + '-staging')),(Join-Path $program ($other + '-registration')),
    (Join-Path $program ($other + '-publication')),(Join-Path $program ($other + '-service')),
    ($bin + $otherSuffix),($registration + $otherSuffix))
$probe = Join-Path $program 'VEIL.cef-preflight-service-probe'
$command = '"{0}" --service' -f (Join-Path $bin 'Broker\Veil.VolumeBroker.exe')
$image = (Get-Item -LiteralPath $ImagePath -Force).FullName
$parent = Split-Path -Parent $image
if ((Split-Path -Parent $parent) -ne $program -or
    $parent -in @($bin,$stage,$prepared,$transaction,$serviceJournal,$probe,($bin + $suffix),(Join-Path $program 'VEIL.cef-candidate-staging')) -or
    (Get-Item -LiteralPath $image -Force).PSIsContainer) { throw 'Expected separate administrator-owned test VHDX directory.' }
Assert-VeilAdminPath $program
if ((Get-Item -LiteralPath $data -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected ProgramData refused.' }
$readService = { Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'" }

$requireSafe = {
    foreach ($path in $otherPaths) {
        if (Test-Path -LiteralPath $path) { throw 'Opposite-mode preparation or service state refused.' }
    }
    Assert-VeilAdminPath $parent
    Assert-VeilAdminPath $image
    if ((Get-DiskImage -ImagePath $image).Attached) { throw 'Test volume must be detached.' }
    $current = & $readService
    if ($null -ne $current) { Assert-VeilDisabledPreflightService $current $command }
}
$verifyPublication = {
    $verifiedFiles = Get-VeilPreflightPublicationState $stage $prepared $bin $registration $transaction $SignerThumbprint $SourceCommit $image $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid $requireSafe -Diagnostic:$Diagnostic
    Assert-VeilInstalledBootstrap $registration $ProfilePath $VolumeGuid
    return $verifiedFiles
}
$sc = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
$readDacl = {
    $lines = @(& $sc sdshow 'Veil.VolumeBroker')
    if ($LASTEXITCODE -ne 0) { throw 'Service DACL read failed.' }
    $descriptors = @($lines | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^(O:|G:|D:|S:)' })
    if ($descriptors.Count -ne 1) { throw 'Unexpected SCM descriptor output.' }
    return $descriptors[0]
}
$verifyRegistration = {
    Read-VeilPreflightServiceRegistration $serviceJournal $command $verifyPublication $readService $readDacl -Diagnostic:$Diagnostic
}
$initial = & $verifyRegistration
if (-not $RunProbe) {
    Write-Host 'PASS: read-only registered-disabled verification. No service startup.'
    return
}
if (Test-Path -LiteralPath $probe) { throw 'Existing or interrupted probe refused.' }
$verified = Register-VeilRecoveryBackup $image ('\\?\Volume{' + $VolumeGuid.ToString() + '}\') $ProtectorId $RecoveryProtectorId
if ($verified -ne $RecoveryProtectorId) { throw 'Recovery protector verification failed.' }
$requireDetached = {
    Assert-VeilAdminPath $parent
    Assert-VeilAdminPath $image
    if ((Get-DiskImage -ImagePath $image).Attached) { throw 'Test volume must be detached.' }
}
Assert-VeilPreflightPublicationEqual (& $verifyRegistration) $initial
$setManual = { Set-Service -Name 'Veil.VolumeBroker' -StartupType Manual }
$disable = { Set-Service -Name 'Veil.VolumeBroker' -StartupType Disabled }
$startService = {
    $controller = Get-Service -Name 'Veil.VolumeBroker'
    try {
        $controller.Start()
        $controller.WaitForStatus('Running',[TimeSpan]::FromMinutes(4))
    } finally { $controller.Dispose() }
}
$stopService = {
    $controller = Get-Service -Name 'Veil.VolumeBroker'
    try {
        if ($controller.Status -ne 'Stopped' -and $controller.Status -ne 'StopPending') { $controller.Stop() }
        $controller.WaitForStatus('Stopped',[TimeSpan]::FromMinutes(4))
    } finally { $controller.Dispose() }
}
[void](Invoke-VeilPreflightServiceProbe $probe $command $verifyRegistration $readService $readDacl $setManual $startService $disable $stopService $requireDetached)
Write-Host 'PASS: observed broker running then disabled/stopped and volume detached. CEF initialization NOT RUN.'
