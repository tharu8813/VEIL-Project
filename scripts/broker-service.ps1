#requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Remove')][switch]$Remove,
    [Parameter(Mandatory, ParameterSetName = 'VerifyRecovery')][switch]$VerifyRecovery,
    [Parameter(Mandatory, ParameterSetName = 'Install')][string]$BrokerSource,
    [Parameter(Mandatory, ParameterSetName = 'Install')][string]$DesktopSource,
    [Parameter(ParameterSetName = 'Install')][string]$PackageRoot,
    [Parameter(Mandatory, ParameterSetName = 'Install')][string]$ImagePath,
    [Parameter(Mandatory, ParameterSetName = 'Install')][string]$ProfilePath,
    [Parameter(Mandatory, ParameterSetName = 'Install')][guid]$VolumeGuid,
    [Parameter(Mandatory, ParameterSetName = 'Install')][guid]$ProtectorId,
    [Parameter(ParameterSetName = 'Install')][guid]$RecoveryProtectorId = [guid]::Empty,
    [Parameter(Mandatory, ParameterSetName = 'Install')][string]$UserSid,
    [Parameter(Mandatory, ParameterSetName = 'Install')][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SignerThumbprint
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Veil.VolumeBroker'
$binRoot = Join-Path $env:ProgramFiles 'VEIL'
$dataRoot = Join-Path $env:ProgramData 'VEIL'
$brokerImage = Join-Path $binRoot 'Broker\Veil.VolumeBroker.exe'
$desktopImage = Join-Path $binRoot 'Desktop\Veil.Desktop.exe'
$serviceCommand = '"{0}" --service' -f $brokerImage
$brokerConfig = Join-Path $dataRoot 'broker.json'
$clientConfig = Join-Path $dataRoot 'client.json'
$trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
. (Join-Path $PSScriptRoot 'bitlocker-recovery.ps1')
. (Join-Path $PSScriptRoot 'package-common.ps1')

function Assert-AdminOnly([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $Path" }
    $acl = Get-Acl -LiteralPath $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw "Untrusted owner: $Path" }
    $write = [Security.AccessControl.FileSystemRights]'Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    foreach ($ace in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($ace.AccessControlType -eq 'Allow' -and ($ace.FileSystemRights -band $write) -and $ace.IdentityReference.Value -notin $trusted) {
            throw "Untrusted write access: $Path"
        }
    }
}

function New-RestrictedDirectory([string]$Path) {
    # Apply the restricted ACL at creation, not after a permissive window.
    New-VeilRestrictedDirectory $Path
}

function Assert-Signed([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SignerThumbprint) {
        throw "Expected Authenticode signer could not be verified: $Path"
    }
}

function Assert-Detached([string]$Path) {
    if ((Get-DiskImage -ImagePath $Path).Attached) { throw 'The registered volume is still attached. Close VEIL and secure the volume before removing the service.' }
}

function Resolve-VeilImagePath([string]$Path) {
    $image = (Get-Item -LiteralPath $Path).FullName
    $parent = Split-Path -Parent $image
    if ([IO.Path]::GetExtension($image) -ne '.vhdx' -or (Split-Path -Parent $parent) -ne $env:ProgramFiles -or $parent -eq $binRoot) {
        throw 'Place the VHDX in a separate administrator-owned directory directly under Program Files.'
    }
    Assert-AdminOnly $parent
    Assert-AdminOnly $image
    return $image
}

function Remove-VeilTree([string]$Path, [string]$ExpectedParent) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = (Get-Item -LiteralPath $Path -Force).FullName
    if ($resolved -ne (Join-Path $ExpectedParent 'VEIL')) { throw 'Unexpected VEIL installation path.' }
    Assert-AdminOnly $resolved
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($resolved)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected installed link: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$existing = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if ($VerifyRecovery) {
    if (-not $existing -or $existing.PathName -ne $serviceCommand -or $existing.State -ne 'Stopped') {
        throw 'An installed VEIL service must be stopped before testing its recovery backup.'
    }
    Assert-AdminOnly $dataRoot
    Assert-AdminOnly $brokerConfig
    $registered = Get-Content -LiteralPath $brokerConfig -Raw | ConvertFrom-Json
    $recoveryId = [guid]::Empty
    if (-not [guid]::TryParse([string]$registered.recoveryProtectorId, [ref]$recoveryId)) {
        throw 'The installed registration has no verified recovery protector.'
    }
    $verifiedImage = Resolve-VeilImagePath $registered.imagePath
    Test-VeilRecoveryBackup $verifiedImage $registered.volumeGuid $recoveryId
    Write-Host 'The recovery backup unlocked the registered volume. The volume is locked and detached.'
    return
}
if ($Remove) {
    if ($existing -and $existing.PathName -ne $serviceCommand) { throw 'The existing service registration does not match VEIL.' }
    if (-not $existing -and -not (Test-Path -LiteralPath $binRoot) -and -not (Test-Path -LiteralPath $dataRoot)) { return }
    if (Test-Path -LiteralPath $binRoot) { Assert-AdminOnly $binRoot }
    Assert-AdminOnly $dataRoot
    Assert-AdminOnly $brokerConfig
    $registered = Get-Content -LiteralPath $brokerConfig -Raw | ConvertFrom-Json
    if ($existing) {
        $controller = Get-Service -Name $serviceName
        try {
            if ($controller.Status -ne 'Stopped') {
                if ($controller.Status -ne 'StopPending') { $controller.Stop() }
                $controller.WaitForStatus('Stopped', [TimeSpan]::FromMinutes(4))
            }
        }
        finally { $controller.Dispose() }
    }
    Assert-Detached $registered.imagePath
    if ($existing) {
        sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Service removal failed: $LASTEXITCODE" }
    }
    Remove-VeilTree $binRoot $env:ProgramFiles
    Remove-VeilTree $dataRoot $env:ProgramData
    return
}

if ($existing -or (Test-Path -LiteralPath $binRoot) -or (Test-Path -LiteralPath $dataRoot)) {
    throw 'An existing VEIL installation or registration is present.'
}

$ImagePath = Resolve-VeilImagePath $ImagePath
Assert-Detached $ImagePath
$ProfilePath = [IO.Path]::GetFullPath($ProfilePath)
if ([IO.Path]::GetPathRoot($ProfilePath) -notmatch '^[A-Z]:\\$' -or $ProfilePath -eq [IO.Path]::GetPathRoot($ProfilePath)) {
    throw 'The profile must name a directory on the registered local volume.'
}
$sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
$pipe = 'VEIL.VolumeBroker'
$installed = $false
$binCreated = $false
$dataCreated = $false
try {
    New-RestrictedDirectory $binRoot
    $binCreated = $true
    New-RestrictedDirectory $dataRoot
    $dataCreated = $true
    if ($PackageRoot) {
        # The package installer supplies its restricted, already authenticated staging tree.
        # Copy the complete inventory, including manifest and signature, before SCM registration.
        Assert-VeilAdminPath $PackageRoot
        [void](Read-VeilPackage $PackageRoot $SignerThumbprint)
        Copy-VeilPackageFiles $PackageRoot $binRoot
        [void](Read-VeilPackage $binRoot $SignerThumbprint)
    } else {
        # Legacy developer-only installation without a package manifest.
        foreach ($source in @(@{ From=$BrokerSource; To=(Split-Path -Parent $brokerImage) }, @{ From=$DesktopSource; To=(Split-Path -Parent $desktopImage) })) {
            New-Item -ItemType Directory -Path $source.To | Out-Null
            Copy-Item -Path (Join-Path $source.From '*') -Destination $source.To -Recurse
        }
    }
    Assert-Signed $brokerImage
    Assert-Signed $desktopImage
    $volumeId = [guid]::NewGuid().ToString('N')
    $volumePath = '\\?\Volume{' + $VolumeGuid.ToString() + '}\'
    $recoveryId = Register-VeilRecoveryBackup $ImagePath $volumePath $ProtectorId $RecoveryProtectorId
    @{
        volumeId=$volumeId; imagePath=$ImagePath; volumeGuid=$volumePath;
        protectorId=$ProtectorId.ToString('B'); userSid=$sid.Value; pipeName=$pipe;
        recoveryProtectorId=$recoveryId.ToString('B');
        clientImages=@($desktopImage); clientSha256=(Get-FileHash -LiteralPath $desktopImage).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath $brokerConfig -Encoding UTF8
    @{
        volumeId=$volumeId; pipeName=$pipe; brokerImage=$brokerImage; profilePath=$ProfilePath; requireWindowsService=$true
    } | ConvertTo-Json | Set-Content -LiteralPath $clientConfig -Encoding UTF8
    foreach ($file in @($brokerConfig, $clientConfig)) {
        $acl = Get-Acl -LiteralPath $file
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        Set-Acl -LiteralPath $file -AclObject $acl
        Assert-AdminOnly $file
    }
    # Do not permit automatic boot activation before recovery policy and startup are verified.
    New-Service -Name $serviceName -BinaryPathName $serviceCommand -DisplayName 'VEIL Volume Broker' -StartupType Manual | Out-Null
    $installed = $true
    # Restart the privileged broker on failure, including an unexpected non-zero service exit.
    # Its startup gate refuses to accept clients until it has checked and secured any orphaned mount.
    # The retries are finite; do not make the machine spin indefinitely on a bad registration.
    sc.exe failure $serviceName reset= 86400 actions= restart/30000/restart/60000/restart/300000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Service recovery registration failed: $LASTEXITCODE" }
    sc.exe failureflag $serviceName flag= 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Service failure flag registration failed: $LASTEXITCODE" }
    $controller = Get-Service -Name $serviceName
    try {
        $controller.Start()
        $controller.WaitForStatus('Running', [TimeSpan]::FromMinutes(1))
    }
    finally { $controller.Dispose() }
    Set-Service -Name $serviceName -StartupType Automatic
}
catch {
    # New-Service may fail after SCM has created a record. Recheck before deleting
    # any binaries or registration; an unknown SCM state must preserve evidence.
    $installError = $_
    $observed = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($installed -or $observed) {
        if ($observed -and $observed.PathName -eq $serviceCommand -and $observed.StartName -eq 'LocalSystem') {
            Set-Service -Name $serviceName -StartupType Disabled
        }
        # Never remove files or a volume once SCM registration may exist.
        throw $installError
    }
    if ($binCreated) { Remove-VeilTree $binRoot $env:ProgramFiles }
    if ($dataCreated) { Remove-VeilTree $dataRoot $env:ProgramData }
    throw $installError
}
