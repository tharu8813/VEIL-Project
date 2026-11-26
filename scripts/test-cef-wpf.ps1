# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CefArchive,
    [switch]$ReuseExtractedCef
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess -or
    $env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -or -not [Environment]::UserInteractive) {
    throw 'Run this fixture in an interactive Windows x64 session.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run in a non-elevated Windows user session, not as administrator.'
    }
}
finally {
    $identity.Dispose()
}

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable exited with code $LASTEXITCODE."
    }
}

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$archive = (Resolve-Path -LiteralPath $CefArchive).Path
$expectedHash = '1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a'
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) {
    throw 'CEF archive SHA-256 does not match the pinned distribution. Nothing was extracted.'
}

$cmake = (Get-Command cmake.exe -CommandType Application).Source
$dotnet = (Get-Command dotnet.exe -CommandType Application).Source
$cargo = (Get-Command cargo.exe -CommandType Application).Source
$node = (Get-Command node.exe -CommandType Application).Source
$tar = (Get-Command tar.exe -CommandType Application).Source

$packRoot = Join-Path (Split-Path -Parent $dotnet) 'packs\Microsoft.NETCore.App.Host.win-x64'
$packs = @(Get-ChildItem -LiteralPath $packRoot -Directory |
    Where-Object { $_.Name -match '^10\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending)
if ($packs.Count -eq 0) {
    throw 'Install the x64 .NET 10 SDK and its Windows x64 host pack first.'
}
$hostPack = Join-Path $packs[0].FullName 'runtimes\win-x64\native'
foreach ($name in @('libnethost.lib', 'nethost.h', 'hostfxr.h', 'coreclr_delegates.h')) {
    if (-not (Test-Path -LiteralPath (Join-Path $hostPack $name) -PathType Leaf)) {
        throw "Required .NET host pack file is missing: $name"
    }
}

$cache = Join-Path $repo '.veil-dev\cef-smoke'
$distribution = 'cef_binary_158.0.6+g23f1736+chromium-158.0.8127.89_windows64_minimal'
$cefRoot = Join-Path $cache $distribution
$build = Join-Path $cache 'build-158.0.6'
$rustTarget = Join-Path $cache 'rust-target'
$null = New-Item -ItemType Directory -Path $cache -Force

Push-Location -LiteralPath $repo
try {
    Invoke-Checked $node @('--check', 'tests/cef/wpf-smoke.mjs')
    if (Test-Path -LiteralPath $cefRoot) {
        if (-not $ReuseExtractedCef) {
            throw "CEF cache already exists at $cefRoot. Use -ReuseExtractedCef only if it is your trusted, unchanged extraction of the verified archive."
        }
        Write-Warning 'Reusing trusted local extracted CEF files; archive hash does not attest to cached file contents.'
    }
    else {
        $stage = Join-Path $cache ("extract-" + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $stage
        Invoke-Checked $tar @('-xf', $archive, '-C', $stage)
        $extracted = Join-Path $stage $distribution
        if (-not (Test-Path -LiteralPath $extracted -PathType Container)) {
            throw "Unexpected extracted archive layout. Inspect $stage."
        }
        Move-Item -LiteralPath $extracted -Destination $cefRoot
        # Leave the staging directory for inspection if extraction or move fails.
        Remove-Item -LiteralPath $stage
    }

    foreach ($name in @('cmake\FindCEF.cmake', 'Release\bootstrap.exe', 'Release\libcef.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $cefRoot $name) -PathType Leaf)) {
            throw "Required CEF file is missing: $name"
        }
    }
    Invoke-Checked $cmake @('-S', 'apps/cef-smoke', '-B', $build,
        '-G', 'Visual Studio 18 2026', '-A', 'x64',
        "-DCEF_ROOT=$cefRoot", "-DDOTNET_HOST_PACK=$hostPack")
    Invoke-Checked $cmake @('--build', $build, '--config', 'Release')
    $output = Join-Path $build 'Release'
    Invoke-Checked $dotnet @('build', 'apps/cef-smoke/wpf/Veil.Cef.WpfSmoke.csproj',
        '-c', 'Release', '-o', $output)
    Invoke-Checked $cargo @('build', '--locked', '--release', '--manifest-path',
        'crates/veil-security/Cargo.toml', '--bin', 'veil-vault', '--target-dir', $rustTarget)
    $vault = Join-Path $rustTarget 'release\veil-vault.exe'
    $hostExe = Join-Path $output 'veil-cef-wpf.exe'
    foreach ($file in @($hostExe, $vault)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "Required built executable not found: $file"
        }
    }
    Invoke-Checked $node @('tests/cef/wpf-smoke.mjs', $hostExe, $vault)
    Write-Host 'PASS: local CEF WPF/vault fixture. This is not product release or required CI evidence.'
}
finally {
    Pop-Location
}
