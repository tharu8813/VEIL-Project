# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [string]$Archive,
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) ("veil-cef-runtime-" + [Guid]::NewGuid().ToString("N")))
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess -or
    -not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) {
    throw 'Use an interactive 64-bit Windows user session, not a service or CI runner.'
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Refusing elevated execution. Open a regular non-elevated Windows terminal.'
}
$repo = Split-Path -Parent $PSScriptRoot
$git = (Get-Command git.exe -ErrorAction Stop).Source
$node = (Get-Command node.exe -ErrorAction Stop).Source
$cargo = (Get-Command cargo.exe -ErrorAction Stop).Source
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$head = (& $git -C $repo rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify source commit.' }
function Assert-CleanSource {
    $current = (& $git -C $repo rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $current -ne $head) { throw 'Source commit changed.' }
    $dirty = & $git -C $repo status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Use a clean checkout of the commit being tested.' }
}
Assert-CleanSource
$work = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $work) { throw 'Choose a fresh runtime work directory.' }
$null = New-Item -ItemType Directory -Path $work
$evidence = Join-Path $work 'evidence'
# Chromium adds deep CacheStorage paths; keep its TEMP independent of WorkRoot.
$runtimeTemp = Join-Path ([IO.Path]::GetTempPath()) ('vc-' + [IO.Path]::GetRandomFileName())
$null = New-Item -ItemType Directory -Path $evidence, $runtimeTemp
$buildRoot = Join-Path $work 'cef'
$rustTarget = Join-Path $work 'rust'
$reportPath = Join-Path $evidence 'result.json'
$report = [ordered]@{
    schema = 1
    scope = 'synthetic-runtime-only'
    sourceCommit = $head
    runtimeTemp = $runtimeTemp
    startedUtc = [DateTime]::UtcNow.ToString('o')
    finishedUtc = $null
    status = 'NOT RUN'
    stages = [ordered]@{ build = 'NOT RUN'; vaultBuild = 'NOT RUN'; win32 = 'NOT RUN'; nestedBuild = 'NOT RUN'; nestedWin32 = 'NOT RUN'; siteIsolation = 'NOT RUN'; wpf = 'NOT RUN'; tabs = 'NOT RUN' }
    hashes = [ordered]@{}
    productReleaseGates = 'NOT RUN'
    limitations = @('No product credentials or profile.', 'Not independent token, site-isolation or OS write tracing evidence.', 'No release approval.')
}
function Write-Report {
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
function Invoke-Stage([string]$Name, [scriptblock]$Action) {
    $report.stages[$Name] = 'RUNNING'
    Write-Report
    try {
        & $Action
        $report.stages[$Name] = 'PASS'
    } catch {
        $report.stages[$Name] = 'FAIL'
        throw
    } finally { Write-Report }
}
function Invoke-Bounded([string]$Name, [string]$Program, [string[]]$Arguments, [int]$Seconds) {
    $options = @{
        FilePath = $Program
        WorkingDirectory = $repo
        RedirectStandardOutput = (Join-Path $evidence "$Name.stdout.log")
        RedirectStandardError = (Join-Path $evidence "$Name.stderr.log")
        PassThru = $true
        NoNewWindow = $true
    }
    # All arguments are fixed tokens or absolute file paths (not shell input).
    if ($Arguments.Count -gt 0) {
        foreach ($arg in $Arguments) {
            if ($arg.Contains('"') -or $arg.EndsWith('\')) { throw 'Unsupported process argument.' }
        }
        $options.ArgumentList = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    }
    $child = Start-Process @options
    try {
        if (-not $child.WaitForExit($Seconds * 1000)) {
            # Only the process tree started by this runner, never a name-wide kill.
            if (-not $child.HasExited) {
                & taskkill.exe /PID $child.Id /T /F
                if (-not $child.WaitForExit(10000)) { throw "$Name timeout; process tree cleanup failed." }
            }
            throw "$Name timed out; inspect the retained logs."
        }
        $child.Refresh()
        if ($child.ExitCode -ne 0) { throw "$Name failed with exit code $($child.ExitCode)." }
    } finally { $child.Dispose() }
}
function Assert-NoTestProcesses([string[]]$Executables) {
    $names = @($Executables | ForEach-Object { [IO.Path]::GetFileName($_) })
    $remaining = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        ($_.ExecutablePath -and $Executables -contains $_.ExecutablePath) -or
        (-not $_.ExecutablePath -and $names -contains $_.Name)
    })
    if ($remaining.Count -ne 0) { throw 'Test executable processes remain or could not be identified safely.' }
}
$environmentNames = @('TEMP', 'TMP', 'VEIL_CEF_SITE_SMOKE_PORT', 'VEIL_CEF_WPF_TABS', 'VEIL_CEF_WPF_TABS_FRESH_PROBE',
    'VEIL_CEF_WPF_TEST_URL', 'VEIL_CEF_WPF_VAULT_EXE')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Write-Report
try {
    Invoke-Stage 'build' {
        $buildArgs = @{ WorkRoot = $buildRoot }
        if ($Archive) { $buildArgs.Archive = $Archive }
        & (Join-Path $PSScriptRoot 'build-cef-smoke.ps1') @buildArgs
    }
    Invoke-Stage 'vaultBuild' {
        Invoke-Bounded 'vault-build' $cargo @('build', '--locked', '--release', '--bin', 'veil-vault',
            '--manifest-path', (Join-Path $repo 'crates/veil-security/Cargo.toml'), '--target-dir', $rustTarget) 900
    }
    Assert-CleanSource
    $out = Join-Path $buildRoot 'build/Release'
    $win32 = Join-Path $out 'veil-cef-smoke.exe'
    $wpf = Join-Path $out 'veil-cef-wpf.exe'
    $vault = Join-Path $rustTarget 'release/veil-vault.exe'
    foreach ($file in @($win32, $wpf, $vault, (Join-Path $out 'veil-cef-smoke.dll'),
            (Join-Path $out 'veil-cef-wpf.dll'), (Join-Path $out 'libcef.dll'),
            (Join-Path $out 'Veil.Cef.WpfSmoke.dll'))) {
        $report.hashes[[IO.Path]::GetFileName($file)] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
    }
    Write-Report
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    $env:TEMP = $runtimeTemp
    $env:TMP = $runtimeTemp
    $executables = @($win32, $wpf, $vault)
    Assert-NoTestProcesses $executables
    Invoke-Stage 'win32' {
        Invoke-Bounded 'win32' $win32 @() 45
        Assert-NoTestProcesses $executables
    }
    $nestedOut = Join-Path $work 'nested-runner'
    Invoke-Stage 'nestedBuild' {
        Invoke-Bounded 'nested-build' $dotnet @('build', (Join-Path $repo 'tests/cef/CefNestedSmoke/CefNestedSmoke.csproj'),
            '-c', 'Release', '-o', $nestedOut) 180
    }
    Invoke-Stage 'nestedWin32' {
        $nested = Join-Path $nestedOut 'CefNestedSmoke.exe'
        $report.hashes['CefNestedSmoke.exe'] = (Get-FileHash -Algorithm SHA256 -LiteralPath $nested).Hash
        $report.hashes['CefNestedSmoke.dll'] = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $nestedOut 'CefNestedSmoke.dll')).Hash
        Write-Report
        Invoke-Bounded 'nested-win32' $nested @($win32, (Join-Path $runtimeTemp 'nested-fixture')) 60
        Assert-NoTestProcesses ($executables + @($nested))
    }
    Invoke-Stage 'siteIsolation' {
        Invoke-Bounded 'site-isolation' $node @((Join-Path $repo 'tests/cef/site-isolation-smoke.mjs'), $win32) 50
        Assert-NoTestProcesses $executables
    }
    Invoke-Stage 'wpf' {
        Invoke-Bounded 'wpf' $node @((Join-Path $repo 'tests/cef/wpf-smoke.mjs'), $wpf, $vault) 120
        Assert-NoTestProcesses $executables
    }
    Invoke-Stage 'tabs' {
        Invoke-Bounded 'tabs' $node @((Join-Path $repo 'tests/cef/tabs-smoke.mjs'), $wpf) 300
        Assert-NoTestProcesses $executables
    }
    Assert-CleanSource
    $report.status = 'PASS'
    Write-Host 'PASS: synthetic runtime checks only; product release gates are NOT RUN.'
} catch {
    $report.status = 'FAIL'
    throw
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    Write-Report
    Write-Host "Retained runtime evidence: $evidence"
}
