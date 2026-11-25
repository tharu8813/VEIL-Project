# SPDX-License-Identifier: MPL-2.0
# Read-only diagnostics; no install, download, build, or execution of Chromium.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Workspace)
$driveRoot = [IO.Path]::GetPathRoot($root)
$driveError = $null
$driveFormat = $null
$freeBytes = $null
try {
    if ($driveRoot -notmatch '^[A-Za-z]:\\$') { throw 'Not a local drive-letter path.' }
    $drive = [IO.DriveInfo]::new($driveRoot)
    if (-not $drive.IsReady) { throw 'Drive is not ready.' }
    $driveFormat = $drive.DriveFormat
    $freeBytes = $drive.AvailableFreeSpace
} catch {
    $driveError = $_.Exception.Message
}
if ($root.Length -gt $driveRoot.Length) { $root = $root.TrimEnd('\') }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$pathShape = ($root -match '^[A-Za-z]:\\[A-Za-z0-9_.\\-]+$' -and $root -notmatch '\s' -and $root -ne $driveRoot)
$rootPrefix = $root.TrimEnd('\') + '\'
$separate = -not ($root.Equals($repo,[StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($repo+'\',[StringComparison]::OrdinalIgnoreCase) -or $repo.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase))
$exists = $false
$parentExists = $false
$ancestorState = 'NOT CHECKED'
if ($pathShape) {
    $exists = Test-Path -LiteralPath $root
    $parent = Split-Path -Parent $root
    $parentExists = Test-Path -LiteralPath $parent -PathType Container
    if ($parentExists) {
        try {
            $ancestorState = 'NO REPARSE POINT OBSERVED'
            for ($p = Get-Item -LiteralPath $parent -Force; $null -ne $p; $p = $p.Parent) {
                if (($p.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $ancestorState = 'REPARSE POINT OBSERVED'
                    break
                }
            }
        } catch { $ancestorState = 'NOT CHECKED' }
    }
}
$commands = [ordered]@{}
foreach ($name in @('git','gclient','fetch','gn','autoninja','node')) {
    $c = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $commands[$name] = if ($c) { $c.Source } else { $null }
}
$vs = @()
$sdk = @()
$programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
if ($programFilesX86) {
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $vs = @(& $vswhere -all -products '*' -format json | Out-String | ConvertFrom-Json)
    }
    $sdkRoot = Join-Path $programFilesX86 'Windows Kits\10\Include'
    if (Test-Path -LiteralPath $sdkRoot -PathType Container) {
        $sdk = @(Get-ChildItem -LiteralPath $sdkRoot -Directory | Select-Object -ExpandProperty Name)
    }
}
$blockers = @()
if (-not $pathShape) { $blockers += 'WORKSPACE_PATH_SHAPE' }
if (-not $separate) { $blockers += 'WORKSPACE_OVERLAPS_VEIL' }
if ($exists) { $blockers += 'WORKSPACE_ALREADY_EXISTS' }
if (-not $parentExists) { $blockers += 'PARENT_MISSING_OR_INVALID' }
if ($ancestorState -ne 'NO REPARSE POINT OBSERVED') { $blockers += 'ANCESTOR_NOT_VERIFIED' }
if ($null -eq $freeBytes -or $driveFormat -ne 'NTFS') { $blockers += 'NTFS_DRIVE_NOT_OBSERVED' }
if ($null -eq $freeBytes -or $freeBytes -lt 100GB) { $blockers += 'MINIMUM_FREE_SPACE_NOT_OBSERVED' }
foreach ($name in @('git','node')) {
    if (-not $commands[$name]) { $blockers += ('MISSING_' + $name.ToUpperInvariant()) }
}
$report = [ordered]@{
    status = 'OBSERVATION_ONLY'
    workspace = $root
    workspaceExists = ($exists -and (Test-Path -LiteralPath $root -PathType Container))
    workspacePathExists = $exists
    workspacePathShapeAccepted = $pathShape
    workspaceSeparateFromVeil = $separate
    workspaceParentExists = $parentExists
    ancestorObservation = $ancestorState
    sourceSyncPreflightBlockers = @($blockers)
    sourceSyncApproved = $false
    driveObservationError = $driveError
    pathContainsWhitespace = ($root -match '\s')
    os = [Environment]::OSVersion.VersionString
    architecture = $env:PROCESSOR_ARCHITECTURE
    driveFormat = $driveFormat
    freeGiB = if ($null -ne $freeBytes) { [Math]::Round($freeBytes / 1GB, 1) } else { $null }
    recommended300GiBObserved = ($null -ne $freeBytes -and $freeBytes -ge 300GB)
    commands = $commands
    visualStudio = @($vs | Select-Object installationPath, installationVersion)
    sdkIncludeVersions = $sdk
    revisionCompatibility = 'NOT RUN'
    chromiumBuild = 'NOT RUN'
    sandbox = 'NOT RUN'
}
$report | ConvertTo-Json -Depth 6
