#requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][switch]$IsolatedVm,
    [Parameter(Mandatory)][string]$ImagePath,
    [Parameter(Mandatory)][string]$ProfileParent,
    [Parameter(Mandatory)][string]$Out,
    [string]$DiskFullImage,
    [string]$DiskFullParent
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'phase34-guest-identity.ps1')
$guest = Get-VeilPhase34Guest
if (-not $IsolatedVm -or -not $guest.virtualBox -or -not $guest.noDefaultRoute -or -not $guest.uuid) {
    throw 'Use a dedicated offline VirtualBox guest with no default network route.'
}
if ([bool]$DiskFullImage -ne [bool]$DiskFullParent) { throw 'Provide both disk-full parameters or neither.' }

function Get-LabVolume([string]$Image, [string]$Parent, [string]$Role) {
    if ($Image -notmatch '^[A-Za-z]:\\.*\.vhdx$' -or $Parent -notmatch '^[A-Za-z]:\\.+') {
        throw 'Use absolute local paths to a VHDX and a dedicated lab folder.'
    }
    $folder = Get-Item -LiteralPath $Parent -ErrorAction Stop
    if (-not $folder.PSIsContainer) { throw 'The profile parent must be an existing directory.' }
    for ($item = $folder; $null -ne $item; $item = $item.Parent) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A lab path is redirected.' }
    }
    $drive = [IO.Path]::GetPathRoot($folder.FullName).TrimEnd('\')
    if ($drive -eq $guest.systemDrive) { throw 'The lab volume must be separate from the guest system volume.' }
    $imageItem = Get-Item -LiteralPath $Image
    if ($imageItem.PSIsContainer -or ($imageItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The specified VHDX is not a regular file.'
    }
    $diskImage = Get-DiskImage -ImagePath $imageItem.FullName
    if (-not $diskImage.Attached) { throw 'Attach the disposable lab VHDX first.' }
    $disk = $diskImage | Get-Disk
    if ($disk.IsBoot -or $disk.IsSystem) { throw 'System and boot disks are excluded from the lab.' }
    $partition = @($disk | Get-Partition | Where-Object DriveLetter -eq $drive[0])
    if ($partition.Count -ne 1) { throw 'The lab drive is not on the specified disposable VHDX.' }
    $volumeId = @($partition[0].AccessPaths | Where-Object { $_ -like '\\?\Volume{*' })
    if ($volumeId.Count -ne 1) { throw 'Could not identify the lab volume GUID.' }
    $protection = Get-BitLockerVolume -MountPoint $drive
    if ($protection.VolumeStatus -ne 'FullyEncrypted' -or $protection.ProtectionStatus -ne 'On' -or
        $protection.LockStatus -ne 'Unlocked') {
        throw 'Use an unlocked, fully encrypted lab volume with BitLocker protection on.'
    }
    $capacity = (Get-Volume -DriveLetter $drive[0]).Size
    if ($Role -eq 'disk-full' -and ($capacity -le 0 -or $capacity -gt 2GB)) {
        throw 'The disk-full test requires a separate lab volume of at most 2 GiB.'
    }
    @{ role = $Role; parent = $folder.FullName; drive = $drive; volumeId = $volumeId[0];
       image = $imageItem.FullName; capacity = $capacity }
}

$volumes = @(Get-LabVolume $ImagePath $ProfileParent 'primary')
if ($DiskFullParent) {
    $small = Get-LabVolume $DiskFullImage $DiskFullParent 'disk-full'
    if ($small.volumeId -eq $volumes[0].volumeId -or $small.image -eq $volumes[0].image) {
        throw 'Use a separate disposable VHDX for the disk-full test.'
    }
    $volumes += $small
}
$receipt = @{
    schema = 1; checkedAt = [datetime]::UtcNow.ToString('o'); guest = $guest; volumes = $volumes
} | ConvertTo-Json -Depth 5
$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Out)) -Force | Out-Null
$stream = [IO.File]::Open($Out, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes($receipt)
    $stream.Write($bytes, 0, $bytes.Length)
}
finally { $stream.Dispose() }
Write-Host "Verified disposable VM volume(s). Receipt: $Out"
