# SPDX-License-Identifier: MPL-2.0
#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName='Update')]
param(
    [Parameter(Mandatory,ParameterSetName='Install')][switch]$Install,
    [Parameter(Mandatory,ParameterSetName='Update')][switch]$Update,
    [Parameter(Mandatory,ParameterSetName='Recover')][switch]$RecoverInterruptedUpdate,
    [Parameter(Mandatory,ParameterSetName='Remove')][switch]$Remove,
    [Parameter(Mandatory,ParameterSetName='RegisterBrowser')][switch]$RegisterBrowser,
    [Parameter(Mandatory,ParameterSetName='Install')]
    [Parameter(Mandatory,ParameterSetName='Update')][string]$Package,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SignerThumbprint,
    [Parameter(Mandatory,ParameterSetName='Install')][string]$ImagePath,
    [Parameter(Mandatory,ParameterSetName='Install')][string]$ProfilePath,
    [Parameter(Mandatory,ParameterSetName='Install')][guid]$VolumeGuid,
    [Parameter(Mandatory,ParameterSetName='Install')][guid]$ProtectorId,
    [Parameter(ParameterSetName='Install')][guid]$RecoveryProtectorId = [guid]::Empty,
    [Parameter(Mandatory,ParameterSetName='Install')][string]$UserSid
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'package-common.ps1')
$programFiles=[Environment]::GetFolderPath('ProgramFiles')
$programData=[Environment]::GetFolderPath('CommonApplicationData')
$bin=Join-Path $programFiles 'VEIL'
$data=Join-Path $programData 'VEIL'
$stage=Join-Path $programFiles 'VEIL.package-staging'
$previous=Join-Path $programFiles 'VEIL.update-previous'
$failed=Join-Path $programFiles 'VEIL.update-failed'
$service='Veil.VolumeBroker'
$broker=Join-Path $bin 'Broker\Veil.VolumeBroker.exe'
$client=Join-Path $bin 'Desktop\Veil.Desktop.exe'
$config=Join-Path $data 'broker.json'
$journal=Join-Path $data 'update-transaction'
$backup=Join-Path $journal 'broker.previous.json'
$committed=Join-Path $journal 'committed'
$serviceCommand='"{0}" --service' -f $broker

# Registration is opt-in for an authenticated installed package. Windows owns the actual default.
$browserCapabilities='Software\VEIL\Capabilities'
$browserClass='HKLM:\Software\Classes\VEIL.Url'
$registeredApps='HKLM:\Software\RegisteredApplications'
$browserCommand='"{0}" --open-url "%1"' -f $client
function Get-VeilBrowserCommand {
    $key=Get-Item -LiteralPath (Join-Path $browserClass 'shell\open\command') -ErrorAction SilentlyContinue
    if ($key) { return $key.GetValue('') }
}
function Register-VeilBrowser {
    $existing=(Get-ItemProperty -LiteralPath $registeredApps -Name 'VEIL' -ErrorAction SilentlyContinue).VEIL
    if ($existing -and $existing -ne $browserCapabilities) { throw 'Foreign RegisteredApplications entry.' }
    $command=Get-VeilBrowserCommand
    if ((Test-Path -LiteralPath $browserClass) -and $command -ne $browserCommand) { throw 'Foreign VEIL.Url registration.' }
    $cap='HKLM:\'+$browserCapabilities
    if ((Test-Path -LiteralPath $cap) -and $existing -ne $browserCapabilities) { throw 'Unowned capabilities registration.' }
    foreach ($key in @($browserClass,(Join-Path $browserClass 'shell\open\command'),$cap,(Join-Path $cap 'URLAssociations'),$registeredApps)) {
        if (-not (Test-Path -LiteralPath $key)) { [void](New-Item -Path $key -Force) }
    }
    Set-Item -LiteralPath $browserClass -Value 'VEIL web link'
    New-ItemProperty -LiteralPath $browserClass -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
    Set-Item -LiteralPath (Join-Path $browserClass 'shell\open\command') -Value $browserCommand
    New-ItemProperty -LiteralPath $cap -Name ApplicationName -Value 'VEIL' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $cap -Name ApplicationDescription -Value 'VEIL protected-profile browser (internal development build)' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath (Join-Path $cap 'URLAssociations') -Name http -Value 'VEIL.Url' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath (Join-Path $cap 'URLAssociations') -Name https -Value 'VEIL.Url' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $registeredApps -Name 'VEIL' -Value $browserCapabilities -PropertyType String -Force | Out-Null
}
function Remove-VeilBrowserRegistration {
    $existing=(Get-ItemProperty -LiteralPath $registeredApps -Name 'VEIL' -ErrorAction SilentlyContinue).VEIL
    if ($existing -eq $browserCapabilities -and (Get-VeilBrowserCommand) -eq $browserCommand) {
        Remove-ItemProperty -LiteralPath $registeredApps -Name 'VEIL'
        Remove-Item -LiteralPath ('HKLM:\'+$browserCapabilities) -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $browserClass -Recurse -Force
    }
}

function Assert-Tree([string]$Root) { [void]@(Get-VeilTreeFiles $Root -Restricted) }
function Remove-RestrictedTree([string]$Root) {
    if (Test-Path -LiteralPath $Root) { Assert-Tree $Root; Remove-Item -LiteralPath $Root -Recurse -Force }
}
function Assert-RegisteredService {
    $registered=Get-CimInstance Win32_Service -Filter "Name='$service'"
    if (-not $registered -or $registered.PathName -ne $serviceCommand -or $registered.StartName -ne 'LocalSystem') { throw 'Unexpected service registration.' }
    Assert-VeilAdminPath $data
    Assert-VeilAdminPath $config
    Assert-VeilAdminPath (Join-Path $data 'client.json')
}
function Assert-ClosedAndDetached {
    # Never kill a browser, renderer, or vault. Require the operator to close VEIL normally.
    if (@(Get-Process -Name 'Veil.Desktop','veil-vault' -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Close all VEIL browsers and vault operations first.' }
    $registered=Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if ((Get-DiskImage -ImagePath $registered.imagePath).Attached) { throw 'Registered volume is attached; close and secure it first.' }
}
function Disable-Broker {
    Set-Service -Name $service -StartupType Disabled
    $controller=Get-Service -Name $service
    try {
        if ($controller.Status -ne 'Stopped') {
            if ($controller.Status -ne 'StopPending') { $controller.Stop() }
            $controller.WaitForStatus('Stopped',[TimeSpan]::FromMinutes(4))
        }
    } finally { $controller.Dispose() }
}
function Start-Broker {
    Set-Service -Name $service -StartupType Automatic
    $controller=Get-Service -Name $service
    try {
        $controller.Start()
        $controller.WaitForStatus('Running',[TimeSpan]::FromMinutes(1))
    } finally { $controller.Dispose() }
}
function Write-Durable([string]$Path,[byte[]]$Bytes) {
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($Bytes,0,$Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    Assert-VeilAdminPath $Path
}
function Set-BrokerHash([string]$Hash) {
    $registered=Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if (@($registered.clientImages).Count -ne 1 -or $registered.clientImages[0] -ne $client) { throw 'Unexpected client registration.' }
    $registered.clientSha256=$Hash
    $new=Join-Path $journal 'broker.next.json'
    Write-Durable $new ([Text.UTF8Encoding]::new($false).GetBytes(($registered | ConvertTo-Json -Depth 12)))
    [IO.File]::Replace($new,$config,$null)
    Assert-VeilAdminPath $config
}
function Restore-PreActivation {
    if (Test-Path -LiteralPath $committed) { throw 'Activation may have occurred. Automatic downgrade is forbidden.' }
    Assert-Tree $journal
    Disable-Broker
    Assert-ClosedAndDetached
    if (Test-Path -LiteralPath $backup) {
        Assert-VeilAdminPath $backup
        $registered=Get-Content -LiteralPath $backup -Raw | ConvertFrom-Json
        if (Test-Path -LiteralPath $previous) {
            Assert-Tree $previous
            [void](Read-VeilPackage $previous $SignerThumbprint)
            $oldHash=(Get-FileHash -LiteralPath (Join-Path $previous 'Desktop\Veil.Desktop.exe') -Algorithm SHA256).Hash
            if ($registered.clientSha256 -ne $oldHash) { throw 'Rollback binary and registration do not match.' }
            if (Test-Path -LiteralPath $bin) {
                Assert-Tree $bin
                if (Test-Path -LiteralPath $failed) { throw 'Recovery collision; administrator review required.' }
                [IO.Directory]::Move($bin,$failed)
            }
            [IO.Directory]::Move($previous,$bin)
        }
        # A second interruption may leave the old tree already restored but the new pin still present.
        # Authenticate the actual old tree against the durable registration backup before restoring it.
        [void](Read-VeilPackage $bin $SignerThumbprint)
        if ($registered.clientSha256 -ne (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash) { throw 'Rollback binary and registration do not match.' }
        $restore=Join-Path $journal 'broker.restore.json'
        if (Test-Path -LiteralPath $restore) { Assert-VeilAdminPath $restore; Remove-Item -LiteralPath $restore -Force }
        Write-Durable $restore ([IO.File]::ReadAllBytes($backup))
        [IO.File]::Replace($restore,$config,$null)
    } elseif ((Test-Path -LiteralPath $previous) -or (Test-Path -LiteralPath $failed)) {
        throw 'Recovery metadata missing.'
    }
    # If interrupted before creating the backup, the original package and pin must still match.
    [void](Read-VeilPackage $bin $SignerThumbprint)
    $current=Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if ($current.clientSha256 -ne (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash) { throw 'Recovery hash mismatch.' }
    Remove-RestrictedTree $failed
    Remove-RestrictedTree $stage
    Start-Broker
    Remove-RestrictedTree $journal
}
function Copy-StagedPackage {
    # Copy only bytes into a restricted staging directory. Re-validate the complete signed inventory
    # AFTER copying; never execute anything from the caller-controlled source.
    $source=(Get-Item -LiteralPath $Package -Force).FullName.TrimEnd('\')
    if ($source -eq $bin -or $source.StartsWith($bin+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $source -eq $stage -or $source -eq $previous -or $source -eq $data) { throw 'Invalid package source.' }
    New-VeilRestrictedDirectory $stage
    foreach ($file in @(Get-VeilTreeFiles $source)) {
        $relative=$file.FullName.Substring($source.Length+1).Replace('\','/')
        if ($relative -notin @('manifest.json','manifest.p7s')) { Assert-VeilRelativeFile $relative }
        $target=Join-Path $stage ($relative.Replace('/','\'))
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        # File.Copy creates a new file under inherited restricted ACL; never copy source ACLs.
        [IO.File]::Copy($file.FullName,$target,$false)
    }
    return Read-VeilPackage $stage $SignerThumbprint
}

# Serializes the new maintenance path. A privileged operator must not run the legacy
# broker installer concurrently. Abandoned mutex is recovered only through the journal.
$mutex=[Threading.Mutex]::new($false,'Global\VEIL.PackageMaintenance')
$held=$false
try {
    try { $held=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held=$true }
    if (-not $held) { throw 'Another maintenance operation is running.' }
    if ($RecoverInterruptedUpdate) {
        Assert-RegisteredService
        if (-not (Test-Path -LiteralPath $journal)) { throw 'No interrupted transaction.' }
        if (Test-Path -LiteralPath $committed) {
            # Never restore older binaries after activation may have occurred. Verify the committed
            # package and client pin, finish activating it, then remove only maintenance artifacts.
            Assert-ClosedAndDetached
            Disable-Broker
            [void](Read-VeilPackage $bin $SignerThumbprint)
            $current=Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
            if ($current.clientSha256 -ne (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash) { throw 'Committed package mismatch.' }
            Start-Broker
            Remove-RestrictedTree $previous
            Remove-RestrictedTree $stage
            Remove-RestrictedTree $journal
        } else { Restore-PreActivation }
        return
    }
    foreach ($path in @($journal,$previous,$stage,$failed)) {
        if (Test-Path -LiteralPath $path) { throw 'Unresolved maintenance state. Inspect or recover before continuing.' }
    }
    if ($RegisterBrowser) {
        Assert-RegisteredService
        [void](Read-VeilPackage $bin $SignerThumbprint)
        $current=Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
        if ($current.clientSha256 -ne (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash) { throw 'Installed client pin mismatch.' }
        Register-VeilBrowser
        Write-Host 'VEIL registered as a browser candidate. Select it explicitly in Windows Default apps.'
        return
    }
    if ($Remove) {
        Assert-RegisteredService
        Assert-ClosedAndDetached
        [void](Read-VeilPackage $bin $SignerThumbprint)
        # Preserve VHDX and registration (including recovery protector ID). Remove only service/binaries.
        Disable-Broker
        Assert-ClosedAndDetached
        sc.exe delete $service | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
        Remove-VeilBrowserRegistration
        Remove-RestrictedTree $bin
        Write-Host 'Binaries removed; encrypted VHDX and registration retained. Re-registration requires administrator recovery.'
        return
    }
    try { $manifest=Copy-StagedPackage }
    catch {
        if (Test-Path -LiteralPath $stage) { Remove-RestrictedTree $stage }
        throw
    }
    if ((Get-VeilVersion $manifest.version).Major -ne 0) { throw 'Only internal packages are enabled.' }
    if ($Install) {
        # The existing installer owns BitLocker recovery registration and service creation.
        if ((Test-Path -LiteralPath $bin) -or (Test-Path -LiteralPath $data)) { throw 'Existing installation/registration requires administrator recovery, not a fresh install.' }
        & (Join-Path $PSScriptRoot 'broker-service.ps1') -BrokerSource (Join-Path $stage 'Broker') -DesktopSource (Join-Path $stage 'Desktop') -ImagePath $ImagePath -ProfilePath $ProfilePath -VolumeGuid $VolumeGuid -ProtectorId $ProtectorId -RecoveryProtectorId $RecoveryProtectorId -UserSid $UserSid -SignerThumbprint $SignerThumbprint
        foreach ($name in @('manifest.json','manifest.p7s')) { [IO.File]::Copy((Join-Path $stage $name),(Join-Path $bin $name),$false) }
        [void](Read-VeilPackage $bin $SignerThumbprint)
        Remove-RestrictedTree $stage
        return
    }
    Assert-RegisteredService
    Assert-ClosedAndDetached
    $installed=Read-VeilPackage $bin $SignerThumbprint
    if ((Get-VeilVersion $manifest.version) -le (Get-VeilVersion $installed.version) -or
        $manifest.profileFormat -ne $installed.profileFormat) { throw 'Downgrade, same version, or profile format change refused.' }
    $current=Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if ($current.clientSha256 -ne (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash) { throw 'Installed client pin mismatch.' }
    New-VeilRestrictedDirectory $journal
    Write-Durable $backup ([IO.File]::ReadAllBytes($config))
    try {
        Disable-Broker
        Assert-ClosedAndDetached
        [IO.Directory]::Move($bin,$previous)
        [IO.Directory]::Move($stage,$bin)
        [void](Read-VeilPackage $bin $SignerThumbprint)
        Set-BrokerHash ((Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash)
        # Durable commit BEFORE enabling the service. No downgrade after possible profile access.
        Write-Durable $committed ([byte[]](1))
    } catch {
        if (-not (Test-Path -LiteralPath $committed)) { Restore-PreActivation }
        throw
    }
    Start-Broker
    Remove-RestrictedTree $previous
    Remove-RestrictedTree $journal
    Write-Host 'Internal update installed. Browser/VM regression validation is still required.'
} finally {
    try {
        # Version/registration rejection before a transaction leaves no maintenance artifacts.
        # A partial initial installation is preserved for explicit administrator review.
        if ($held -and $Update -and -not (Test-Path -LiteralPath $journal) -and
            -not (Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $failed) -and
            (Test-Path -LiteralPath $stage)) { Remove-RestrictedTree $stage }
    } finally {
        if ($held) { $mutex.ReleaseMutex() }
    }
    $mutex.Dispose()
}
