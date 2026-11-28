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
    [Parameter(Mandatory)][switch]$RunProtectedSession,
    [switch]$Diagnostic,
    [string]$BaselineSpkiBase64,
    [string]$BaselineKeySha256,
    [string]$BaselinePayloadSha256,
    [long]$BaselineSequence,
    [string]$ApprovalSpkiBase64,
    [string]$ApprovalKeySha256
)
$ErrorActionPreference = 'Stop'
if (-not $DisposableVm -or -not $RunProtectedSession -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') {
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
$probe = Join-Path $program ($prefix + '-service-probe')
$sessionProbe = Join-Path $program ($prefix + '-protected-session')
$otherPaths = @((Join-Path $program ($other + '-staging')),(Join-Path $program ($other + '-registration')),
    (Join-Path $program ($other + '-publication')),(Join-Path $program ($other + '-service')),
    (Join-Path $program ($other + '-service-probe')),(Join-Path $program ($other + '-protected-session')),
    ($bin + $otherSuffix),($registration + $otherSuffix))
$desktopArgument = if ($Diagnostic) { '--cef-broker-suspended-probe' } else { '--cef-protected-preflight' }
if ($Diagnostic) {
    if ($BaselineSequence -le 0 -or $BaselineKeySha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or
        $BaselinePayloadSha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or $ApprovalKeySha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or
        $BaselineSpkiBase64 -notmatch '\A[A-Za-z0-9+/]{1,5464}={0,2}\z' -or
        $ApprovalSpkiBase64 -notmatch '\A[A-Za-z0-9+/]{1,5464}={0,2}\z') { throw 'Independent diagnostic trust anchors required.' }
} elseif ($BaselineSequence -ne 0 -or $BaselineSpkiBase64 -or $BaselineKeySha256 -or $BaselinePayloadSha256 -or
          $ApprovalSpkiBase64 -or $ApprovalKeySha256) { throw 'Diagnostic anchors refused in ordinary preflight.' }
$command = '"{0}" --service' -f (Join-Path $bin 'Broker\Veil.VolumeBroker.exe')
$image = (Get-Item -LiteralPath $ImagePath -Force).FullName
$parent = Split-Path -Parent $image
if ((Split-Path -Parent $parent) -ne $program -or
    $parent -in @($bin,$stage,$prepared,$transaction,$serviceJournal,$probe,$sessionProbe,($bin + $suffix),(Join-Path $program 'VEIL.cef-candidate-staging')) -or
    (Get-Item -LiteralPath $image -Force).PSIsContainer) { throw 'Expected separate administrator-owned test VHDX directory.' }
Assert-VeilAdminPath $program
if ((Get-Item -LiteralPath $data -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected ProgramData refused.' }
$readService = { Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'" }

$requireSafe = {
    foreach ($path in $otherPaths) {
        if (Test-Path -LiteralPath $path) { throw 'Opposite-mode state refused.' }
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
    $evidence = Read-VeilPreflightServiceRegistration $serviceJournal $command $verifyPublication $readService $readDacl -Diagnostic:$Diagnostic
    if ($Diagnostic) {
        $brokerImage = Join-Path $bin 'Broker\Veil.VolumeBroker.exe'
        Assert-VeilAdminPath $brokerImage
        Assert-VeilSignedImage $brokerImage $SignerThumbprint
        & $brokerImage --cef-check-diagnostic-trust --disposable-vm $BaselineSpkiBase64 $BaselineKeySha256 $BaselinePayloadSha256 ($BaselineSequence.ToString([Globalization.CultureInfo]::InvariantCulture)) $ApprovalSpkiBase64 $ApprovalKeySha256 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Installed independent diagnostic trust verification failed.' }
        Assert-VeilDisabledPreflightService (& $readService) $command
    }
    return $evidence
}
$initial = & $verifyRegistration
if (-not $Diagnostic) { Assert-VeilCompletedServiceProbe $probe $initial }
if (Test-Path -LiteralPath $sessionProbe) { throw 'Existing or interrupted protected session refused.' }
if (-not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) { throw 'Interactive administrator desktop required.' }
$admin = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $expectedSid = [Security.Principal.SecurityIdentifier]::new($UserSid)
    if ($admin.IsSystem -or $admin.User -eq $expectedSid) { throw 'Use a separate administrator account and a dedicated standard test user.' }
} finally { $admin.Dispose() }
$localUser = Get-LocalUser -SID $expectedSid -ErrorAction Stop
if (-not $localUser.Enabled) { throw 'Test account must be an enabled local user.' }
$adminGroup = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
if (@(Get-LocalGroupMember -SID $adminGroup | Where-Object { $_.SID -eq $expectedSid }).Count -ne 0) {
    throw 'Test user is a member of local Administrators.'
}
# Separate Windows account credential, not the volume passphrase. No
# password argument, file or transcript output. The app independently
# rejects elevated execution and its broker checks the registered SID.
$credential = Get-Credential -UserName ($env:COMPUTERNAME + '\' + $localUser.Name) -Message 'Dedicated standard test account (Windows logon password, not BitLocker)'
if ($null -eq $credential) { throw 'Test user logon cancelled.' }
$actualSid = ([Security.Principal.NTAccount]::new($credential.UserName)).Translate([Security.Principal.SecurityIdentifier])
if ($actualSid -ne $expectedSid) { throw 'Credential is not the registered test user.' }
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
Add-Type -Path (Join-Path $PSScriptRoot 'CefPreflightProcessObservation.cs')
$desktop = Join-Path $bin 'Desktop\Veil.Desktop.exe'
$expectedSession = (Get-Process -Id $PID).SessionId
$exercise = {
    Assert-VeilAdminPath $desktop
    Assert-VeilSignedImage $desktop $SignerThumbprint
    # No RunAs elevation, shell command or inherited user-supplied arguments.
    # The dedicated test user enters the volume passphrase in the app dialog.
    $start = [Diagnostics.ProcessStartInfo]::new($desktop,$desktopArgument)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path -Parent $desktop
    $start.Domain = $env:COMPUTERNAME
    $start.UserName = $localUser.Name
    $start.Password = $credential.Password
    $start.LoadUserProfile = $true
    $child = [Diagnostics.Process]::Start($start)
    if ($null -eq $child) { throw 'No desktop process returned.' }
    try {
        $observed = [Veil.Preflight.ProcessObservation]::Inspect($child)
        $observed.RequireStandard($desktop,$expectedSid.Value,$expectedSession)
        if (-not $child.WaitForExit(300000)) {
            throw 'Protected preflight timed out. No force-kill; close the test window and inspect the VM.'
        }
        return [pscustomobject]@{
            kind='standard-user-exit-observation';exitCode=[int]$child.ExitCode
            processId=$observed.ProcessId;sessionId=$observed.SessionId;tokenVerified=$true
        }
    } finally { $child.Dispose(); $start.Password = $null }
}
try {
    [void](Invoke-VeilPreflightServiceProbe $sessionProbe $command $verifyRegistration $readService $readDacl $setManual $startService $disable $stopService $requireDetached $exercise -Diagnostic:$Diagnostic)
    [void](Read-VeilCompletedProtectedSession $sessionProbe (& $verifyRegistration) -Diagnostic:$Diagnostic)
} finally { $credential = $null }
if ($Diagnostic) {
    Write-Host 'PASS: diagnostic suspended probe exit 31 observed; broker disabled/stopped and volume detached. CEF initialization NOT RUN.'
} else {
    Write-Host 'PASS: protected preflight exit 29 observed; broker disabled/stopped and volume detached. CEF browsing NOT RUN.'
}
