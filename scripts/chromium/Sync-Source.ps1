# SPDX-License-Identifier: MPL-2.0
# C1 preparation. Default is read-only plan; explicit execution creates a fresh checkout.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Workspace,
    [string]$DepotToolsRevision,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Workspace)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($root -notmatch '^[A-Za-z]:\\[A-Za-z0-9_.\\-]+$' -or $root -match '\s' -or $root.TrimEnd('\') -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'Use a dedicated absolute local drive path without spaces.' }
$root = $root.TrimEnd('\')
if ($root.Equals($repo,[StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($repo.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $repo.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Workspace must be separate from the VEIL checkout.' }
$pinScript = Join-Path $PSScriptRoot 'verify-candidate.mjs'
$pinJson = & node $pinScript
if ($LASTEXITCODE -ne 0) { throw 'Candidate validation failed.' }
$pin = $pinJson | ConvertFrom-Json
if ($PSBoundParameters.ContainsKey('DepotToolsRevision') -and $DepotToolsRevision -cne $pin.depotToolsRevision) { throw 'depot_tools revision does not match candidate manifest.' }
$DepotToolsRevision = $pin.depotToolsRevision
$depotUrl = $pin.depotToolsUpstream
$plan = [ordered]@{ status='PLAN_ONLY'; workspace=$root; chromiumVersion=$pin.version; chromiumRevision=$pin.revision; depotToolsRevision=$DepotToolsRevision; sourceSync='NOT RUN'; hooks='NOT RUN'; build='NOT RUN'; sandbox='NOT RUN' }
if (-not $Execute) { $plan | ConvertTo-Json; return }
if (Test-Path -LiteralPath $root) { throw 'Refusing to modify an existing workspace; choose a new dedicated path.' }
$parent = Split-Path -Parent $root
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Workspace parent must already exist.' }
# Reject junctions/symlinks in existing ancestor paths.
for ($p = Get-Item -LiteralPath $parent; $null -ne $p; $p = $p.Parent) {
    if (($p.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point in workspace path.' }
}
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))
if ($drive.DriveFormat -ne 'NTFS' -or $drive.AvailableFreeSpace -lt 100GB) { throw 'NTFS with at least 100 GiB free required; 300 GiB recommended.' }
& node $pinScript --verify-remote
if ($LASTEXITCODE -ne 0) { throw 'Remote candidate did not match; no checkout created.' }
function Invoke-Git([string[]]$Arguments) {
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git failed: $LASTEXITCODE" }
}
$null = New-Item -ItemType Directory -Path $root
$depot = Join-Path $root 'depot_tools'
$sourceRoot = Join-Path $root 'chromium'
$null = New-Item -ItemType Directory -Path $sourceRoot
Invoke-Git @('clone','--no-checkout',$depotUrl,$depot)
Invoke-Git @('-C',$depot,'checkout','--detach',$DepotToolsRevision)
$actualDepot = (& git -C $depot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $actualDepot -cne $DepotToolsRevision) { throw 'depot_tools revision mismatch.' }
# Keep tool configuration process-local; no global Git or Windows settings changed.
$oldPath=$env:Path; $oldUpdate=$env:DEPOT_TOOLS_UPDATE; $oldToolchain=$env:DEPOT_TOOLS_WIN_TOOLCHAIN
try {
    $env:Path="$depot;$oldPath"
    $env:DEPOT_TOOLS_UPDATE='0'
    $env:DEPOT_TOOLS_WIN_TOOLCHAIN='0'
    $gclient = Join-Path $depot 'gclient.bat'
    if (-not (Test-Path -LiteralPath $gclient -PathType Leaf)) { throw 'gclient.bat missing.' }
    Push-Location $sourceRoot
    try {
        & $gclient config --name src $pin.upstream
        if ($LASTEXITCODE -ne 0) { throw 'gclient config failed.' }
        & $gclient sync --nohooks --noprehooks --revision "src@$($pin.revision)"
        if ($LASTEXITCODE -ne 0) { throw 'gclient sync failed.' }
        $src=Join-Path $sourceRoot 'src'
        $actual=(& git -C $src rev-parse HEAD | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -cne $pin.revision) { throw 'Chromium revision mismatch.' }
        $actualDepot=(& git -C $depot rev-parse HEAD | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualDepot -cne $DepotToolsRevision) { throw 'depot_tools changed during sync.' }
        $deps=Get-FileHash -LiteralPath (Join-Path $src 'DEPS') -Algorithm SHA256
        [ordered]@{status='SOURCE_SYNC_ONLY'; chromiumRevision=$actual; depotToolsRevision=$actualDepot; depsSha256=$deps.Hash; hooks='NOT RUN'; build='NOT RUN'; sandbox='NOT RUN'} | ConvertTo-Json
    } finally { Pop-Location }
} finally {
    $env:Path=$oldPath; $env:DEPOT_TOOLS_UPDATE=$oldUpdate; $env:DEPOT_TOOLS_WIN_TOOLCHAIN=$oldToolchain
}
