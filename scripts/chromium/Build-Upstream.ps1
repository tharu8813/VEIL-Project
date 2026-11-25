# SPDX-License-Identifier: MPL-2.0
# C1 upstream-only build. Not a VEIL release.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Workspace,
    [ValidateSet('Debug','Release')][string]$Configuration='Debug',
    [switch]$Execute,
    [switch]$ApproveHooks
)
$ErrorActionPreference='Stop'
if ($ApproveHooks -and -not $Execute) { throw 'ApproveHooks requires Execute.' }
if ($Execute -and -not $ApproveHooks) { throw 'Execution requires explicit upstream hook/dependency review approval (-ApproveHooks).' }
if ($Workspace -notmatch '^[A-Za-z]:\\[A-Za-z0-9_.\\-]+$') { throw 'Use an absolute dedicated local Windows path without spaces.' }
$root=[IO.Path]::GetFullPath($Workspace).TrimEnd('\')
if ($root -eq [IO.Path]::GetPathRoot($root+'\').TrimEnd('\')) { throw 'Drive root is not a workspace.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
if ($root.Equals($repo,[StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($repo+'\',[StringComparison]::OrdinalIgnoreCase) -or $repo.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Workspace must be separate from VEIL checkout.' }
$pinJson=& node (Join-Path $PSScriptRoot 'verify-candidate.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Candidate validation failed.' }
$pin=$pinJson | ConvertFrom-Json
$src=Join-Path $root 'chromium\src'
$depot=Join-Path $root 'depot_tools'
$outRel="out\Veil$Configuration"
$out=Join-Path $src $outRel
$template=Join-Path $repo "chromium\gn\$Configuration.args.gn"
$argsHash=(Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash
$plan=[ordered]@{status='PLAN_ONLY'; workspace=$root; configuration=$Configuration; chromiumRevision=$pin.revision; depotToolsRevision=$pin.depotToolsRevision; output=$out; argsSha256=$argsHash; commands=@('gclient runhooks',"gn gen $outRel","autoninja -C $outRel chrome"); hooks='NOT RUN'; build='NOT RUN'; runtime='NOT RUN'; sandbox='NOT RUN'}
if (-not $Execute) { $plan | ConvertTo-Json -Depth 4; return }

function Assert-OrdinaryDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Required directory missing: $Path" }
    for ($p=Get-Item -LiteralPath $Path; $null -ne $p; $p=$p.Parent) {
        if (($p.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused: $($p.FullName)" }
    }
}
function Get-GitText([string]$Path,[string[]]$GitArgs) {
    $s=& git -C $Path @GitArgs | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Git check failed: $Path" }
    return $s.Trim()
}
. (Join-Path $PSScriptRoot 'Assert-BuildIndex.ps1')
function Assert-Pins {
    foreach ($entry in @(@{path=$src;revision=$pin.revision;url=$pin.upstream},@{path=$depot;revision=$pin.depotToolsRevision;url=$pin.depotToolsUpstream})) {
        $top=Get-GitText $entry.path @('rev-parse','--show-toplevel')
        if (-not ([IO.Path]::GetFullPath($top).Equals([IO.Path]::GetFullPath($entry.path),[StringComparison]::OrdinalIgnoreCase))) { throw 'Unexpected Git checkout root.' }
        if ((Get-GitText $entry.path @('rev-parse','HEAD')) -cne $entry.revision) { throw 'Checkout revision mismatch.' }
        if ((Get-GitText $entry.path @('remote','get-url','origin')) -cne $entry.url) { throw 'Checkout origin mismatch.' }
        Assert-VeilOrdinaryBuildIndex -Checkout $entry.path
        if (Get-GitText $entry.path @('status','--porcelain','--untracked-files=no')) { throw 'Tracked upstream files are modified.' }
    }
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal=[Security.Principal.WindowsPrincipal]::new($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Do not run the upstream build elevated.' }
} finally { $identity.Dispose() }
if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'Native Windows x64 process required.' }
Assert-OrdinaryDirectory $root
Assert-OrdinaryDirectory $src
Assert-OrdinaryDirectory $depot
$drive=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))
if ($drive.DriveFormat -ne 'NTFS' -or $drive.AvailableFreeSpace -lt 100GB) { throw 'At least 100 GiB free on NTFS required before build; 300 GiB recommended for complete workspace.' }
Assert-Pins
$deps=Join-Path $src 'DEPS'
$depsHash=(Get-FileHash -LiteralPath $deps -Algorithm SHA256).Hash
$outParent=Join-Path $src 'out'
if (Test-Path -LiteralPath $outParent) { Assert-OrdinaryDirectory $outParent }
if (Test-Path -LiteralPath $out) { throw 'Build output exists; refusing to overwrite it. Preserve it for inspection.' }
$gclient=Join-Path $depot 'gclient.bat'
$gn=Join-Path $depot 'gn.bat'
$ninja=Join-Path $depot 'autoninja.bat'
foreach ($tool in @($gclient,$gn,$ninja)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Pinned build wrapper missing: $tool" }
}
$logDir=Join-Path $root ('veil-build-'+$Configuration+'-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $logDir
function Invoke-Logged([string]$Tool,[string[]]$ToolArgs,[string]$LogName) {
    $log=Join-Path $logDir $LogName
    Write-Host "Running $Tool; log: $log"
    $saved=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        $global:LASTEXITCODE=-1
        & $Tool @ToolArgs *> $log
        $code=$LASTEXITCODE
    } finally { $ErrorActionPreference=$saved }
    if ($code -ne 0) { throw "Upstream step failed ($code); see $log" }
}
$oldPath=$env:Path; $oldUpdate=$env:DEPOT_TOOLS_UPDATE; $oldToolchain=$env:DEPOT_TOOLS_WIN_TOOLCHAIN
try {
    $env:Path="$depot;$oldPath"
    $env:DEPOT_TOOLS_UPDATE='0'
    $env:DEPOT_TOOLS_WIN_TOOLCHAIN='0'
    Push-Location $src
    try {
        Invoke-Logged $gclient @('runhooks') '01-hooks.log'
        Assert-Pins
        if ((Get-FileHash -LiteralPath $deps -Algorithm SHA256).Hash -cne $depsHash) { throw 'DEPS changed after hooks.' }
        if (Test-Path -LiteralPath $outParent) { Assert-OrdinaryDirectory $outParent }
        if (Test-Path -LiteralPath $out) { throw 'Output appeared during hooks.' }
        $null=New-Item -ItemType Directory -Path $out
        Copy-Item -LiteralPath $template -Destination (Join-Path $out 'args.gn')
        Invoke-Logged $gn @('gen',$outRel) '02-gn.log'
        Invoke-Logged $ninja @('-C',$outRel,'chrome') '03-build.log'
        Assert-Pins
        if ((Get-FileHash -LiteralPath (Join-Path $out 'args.gn') -Algorithm SHA256).Hash -cne $argsHash -or (Get-FileHash -LiteralPath $deps -Algorithm SHA256).Hash -cne $depsHash) { throw 'Build input changed during execution.' }
        $exe=Join-Path $out 'chrome.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build exited without chrome.exe.' }
        $receipt=[ordered]@{status='BUILD_OUTPUT_OBSERVED'; chromiumRevision=$pin.revision; depotToolsRevision=$pin.depotToolsRevision; depsSha256=$depsHash; argsSha256=$argsHash; configuration=$Configuration; executable=$exe; executableSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash; logs=$logDir; hooks='EXIT_0'; build='EXIT_0'; dependencyGraph='NOT VERIFIED'; runtime='NOT RUN'; sandbox='NOT RUN'; releaseApproved=$false}
        $json=$receipt | ConvertTo-Json -Depth 4
        $json | Set-Content -LiteralPath (Join-Path $logDir 'receipt.json') -Encoding UTF8
        $json
    } finally { Pop-Location }
} finally {
    $env:Path=$oldPath; $env:DEPOT_TOOLS_UPDATE=$oldUpdate; $env:DEPOT_TOOLS_WIN_TOOLCHAIN=$oldToolchain
}
