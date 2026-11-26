# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [string]$Archive,
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) ("veil-cef-build-" + [Guid]::NewGuid().ToString("N"))),
    [ValidateSet('Visual Studio 18 2026', 'Visual Studio 17 2022')]
    [string]$Generator,
    [string]$DotnetHostPack
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) {
    throw 'Run this build from 64-bit Windows PowerShell.'
}
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with code $LASTEXITCODE" }
}
$repo = Split-Path -Parent $PSScriptRoot
$name = 'cef_binary_158.0.6+g23f1736+chromium-158.0.8127.89_windows64_minimal'
$expectedSha256 = '1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a'
$work = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $work) { throw "Work directory already exists: $work. Choose a fresh directory." }
$null = New-Item -ItemType Directory -Path $work
if (-not $Archive) {
    $Archive = Join-Path $work "$name.tar.bz2"
    $url = 'https://cef-builds.spotifycdn.com/' + [Uri]::EscapeDataString("$name.tar.bz2")
    Invoke-Checked 'curl.exe' @('--fail', '--location', '--retry', '2', '--output', $Archive, $url)
}
$Archive = (Resolve-Path -LiteralPath $Archive).Path
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Archive).Hash
if ($actual -ne $expectedSha256) { throw "CEF archive SHA-256 mismatch: $actual" }
Write-Host "Verified CEF archive SHA-256: $actual"
Invoke-Checked 'tar.exe' @('-xf', $Archive, '-C', $work)
$cef = Join-Path $work $name
if (-not (Test-Path -LiteralPath (Join-Path $cef 'Release/bootstrap.exe') -PathType Leaf)) {
    throw 'Pinned distribution does not contain the expected sandbox bootstrap.'
}
if (-not $Generator) {
    $vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio/Installer/vswhere.exe'
    $cmakeHelp = (& cmake --help | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Could not query CMake generators.' }
    foreach ($candidate in @(@{ Generator = 'Visual Studio 18 2026'; Range = '[18.0,19.0)' },
                             @{ Generator = 'Visual Studio 17 2022'; Range = '[17.0,18.0)' })) {
        if ($cmakeHelp -notmatch [regex]::Escape($candidate.Generator)) { continue }
        $installation = & $vswhere -latest -products '*' -version $candidate.Range -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -ne 0) { throw 'vswhere failed.' }
        if ($installation) { $Generator = $candidate.Generator; break }
    }
    if (-not $Generator) { throw 'No supported CMake generator with installed x64 Visual C++ tools found.' }
}
if (-not $DotnetHostPack) {
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet.exe -ErrorAction Stop).Source
    $packRoot = Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Host.win-x64'
    $pack = Get-ChildItem -LiteralPath $packRoot -Directory |
        Where-Object { $_.Name -match '^10\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $pack) { throw 'Install the .NET 10 x64 SDK or specify -DotnetHostPack.' }
    $DotnetHostPack = Join-Path $pack.FullName 'runtimes/win-x64/native'
}
$DotnetHostPack = (Resolve-Path -LiteralPath $DotnetHostPack).Path
foreach ($file in @('nethost.h', 'hostfxr.h', 'coreclr_delegates.h', 'libnethost.lib')) {
    if (-not (Test-Path -LiteralPath (Join-Path $DotnetHostPack $file) -PathType Leaf)) {
        throw "Missing .NET native host file: $file"
    }
}
$build = Join-Path $work 'build'
$out = Join-Path $build 'Release'
Write-Host "Building native CEF smoke targets using $Generator"
Invoke-Checked 'cmake' @('-S', (Join-Path $repo 'apps/cef-smoke'), '-B', $build, '-G', $Generator, '-A', 'x64', "-DCEF_ROOT=$cef", "-DDOTNET_HOST_PACK=$DotnetHostPack")
Invoke-Checked 'cmake' @('--build', $build, '--config', 'Release', '--target', 'veil-cef-wpf')
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'apps/cef-smoke/wpf/Veil.Cef.WpfSmoke.csproj'), '-c', 'Release', '-o', $out, '--nologo')
foreach ($file in @('veil-cef-smoke.exe', 'veil-cef-smoke.dll', 'veil-cef-wpf.exe', 'veil-cef-wpf.dll', 'libcef.dll', 'Veil.Cef.WpfSmoke.dll', 'Veil.Cef.WpfSmoke.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $out $file) -PathType Leaf)) {
        throw "Missing expected build output: $file"
    }
}
$bootstrapHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $cef 'Release/bootstrap.exe')).Hash
foreach ($file in @('veil-cef-smoke.exe', 'veil-cef-wpf.exe')) {
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $out $file)).Hash -ne $bootstrapHash) {
        throw "Unexpected bootstrap executable: $file"
    }
}
Write-Host "PASS: native and managed CEF compilation; output $out"
Write-Host 'NOT RUN: interactive WPF, renderer sandbox, site isolation, protected storage and download runtime checks.'
