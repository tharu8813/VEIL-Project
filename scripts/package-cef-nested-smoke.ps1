# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$RunnerRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
# Test-only artifact; never consume the product candidate or install tree.
$build = (Resolve-Path -LiteralPath $BuildRoot).Path
$runner = (Resolve-Path -LiteralPath $RunnerRoot).Path
$out = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $out) { throw 'Choose a fresh output directory.' }
$cefName = 'cef_binary_158.0.6+g23f1736+chromium-158.0.8127.89_windows64_minimal'
$cef = Join-Path $build $cefName
$release = Join-Path $build 'build/Release'
$smoke = Join-Path $out 'smoke'
$tools = Join-Path $out 'runner'
$null = New-Item -ItemType Directory -Path $out, $smoke, $tools
function Copy-Checked([string]$Source, [string]$Destination) {
    $item = Get-Item -LiteralPath $Source
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected source file: $Source"
    }
    if (Test-Path -LiteralPath $Destination) { throw "Duplicate output: $Destination" }
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent)) { $null = New-Item -ItemType Directory -Path $parent -Force }
    Copy-Item -LiteralPath $Source -Destination $Destination
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Source).Hash -ne
        (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash) { throw 'Copy hash mismatch.' }
}
# Take runtime payload only from the pinned distribution. Check that each
# selected binary/resource is also identical to the built smoke output.
foreach ($folder in @('Release', 'Resources')) {
    $source = Join-Path $cef $folder
    foreach ($item in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($source, $item.FullName)
        if ($item.Extension -notin @('.dll', '.pak', '.dat', '.bin', '.json', '.txt')) { continue }
        $built = Join-Path $release $relative
        if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { continue }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash -ne
            (Get-FileHash -Algorithm SHA256 -LiteralPath $built).Hash) {
            throw "Runtime build output differs from pinned distribution: $relative"
        }
        Copy-Checked $built (Join-Path $smoke $relative)
    }
}
foreach ($file in @('veil-cef-smoke.exe', 'veil-cef-smoke.dll')) {
    Copy-Checked (Join-Path $release $file) (Join-Path $smoke $file)
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $smoke 'veil-cef-smoke.exe')).Hash -ne
    (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $cef 'Release/bootstrap.exe')).Hash) {
    throw 'Smoke bootstrap differs from pinned distribution.'
}
foreach ($file in @('libcef.dll', 'icudtl.dat', 'resources.pak', 'locales/en-US.pak')) {
    if (-not (Test-Path -LiteralPath (Join-Path $smoke $file) -PathType Leaf)) { throw "Missing fixture runtime: $file" }
}
Copy-Checked (Join-Path $cef 'LICENSE.txt') (Join-Path $out 'CEF-LICENSE.txt')
foreach ($file in @('CefNestedSmoke.exe', 'CefNestedSmoke.dll', 'CefNestedSmoke.deps.json', 'CefNestedSmoke.runtimeconfig.json')) {
    Copy-Checked (Join-Path $runner $file) (Join-Path $tools $file)
}
$repo = Split-Path -Parent $PSScriptRoot
Copy-Checked (Join-Path $repo 'LICENSE') (Join-Path $out 'VEIL-LICENSE.txt')
Copy-Checked (Join-Path $PSScriptRoot 'run-cef-nested-bundle.ps1') (Join-Path $out 'run-cef-nested-bundle.ps1')
$files = @(Get-ChildItem -LiteralPath $out -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($out, $_.FullName).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
    }
})
[ordered]@{
    schema = 1
    scope = 'unsigned-nested-cef-smoke-only'
    sourceCommit = $SourceCommit
    cefArchiveSha256 = '1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a'
    runtimeStatus = 'NOT RUN'
    installable = $false
    files = $files
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'manifest.json') -Encoding utf8
Write-Host "Prepared unsigned test-only bundle: $out"
