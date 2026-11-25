# SPDX-License-Identifier: MPL-2.0
# Read-only diagnostics; no install, download, build, or execution of Chromium.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($Workspace)
$driveRoot = [System.IO.Path]::GetPathRoot($root)
$drive = New-Object System.IO.DriveInfo -ArgumentList $driveRoot
$commands = [ordered]@{}
foreach ($name in @('git','gclient','fetch','gn','autoninja','node')) {
    $c = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $commands[$name] = if ($c) { $c.Source } else { $null }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = @()
if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
    $vs = @(& $vswhere -all -products '*' -format json | Out-String | ConvertFrom-Json)
}
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Include'
$sdk = @()
if (Test-Path -LiteralPath $sdkRoot -PathType Container) {
    $sdk = @(Get-ChildItem -LiteralPath $sdkRoot -Directory | Select-Object -ExpandProperty Name)
}
$report = [ordered]@{
    status = 'OBSERVATION_ONLY'
    workspace = $root
    workspaceExists = (Test-Path -LiteralPath $root -PathType Container)
    pathContainsWhitespace = ($root -match '\s')
    os = [Environment]::OSVersion.VersionString
    architecture = $env:PROCESSOR_ARCHITECTURE
    driveFormat = $drive.DriveFormat
    freeGiB = [Math]::Round($drive.AvailableFreeSpace / 1GB, 1)
    commands = $commands
    visualStudio = @($vs | Select-Object installationPath, installationVersion)
    sdkIncludeVersions = $sdk
    revisionCompatibility = 'NOT RUN'
    chromiumBuild = 'NOT RUN'
    sandbox = 'NOT RUN'
}
$report | ConvertTo-Json -Depth 6
