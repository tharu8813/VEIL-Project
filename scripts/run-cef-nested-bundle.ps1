# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit,
    [string]$EvidenceRoot = (Join-Path ([IO.Path]::GetTempPath()) ("veil-nested-evidence-" + [Guid]::NewGuid().ToString("N")))
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess -or
    -not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) {
    throw 'Use an interactive x64 Windows session.'
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Do not run elevated.' }
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$manifestPath = Join-Path $root 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.scope -cne 'unsigned-nested-cef-smoke-only' -or
    $manifest.sourceCommit -cne $ExpectedCommit -or $manifest.installable -ne $false -or
    $manifest.runtimeStatus -cne 'NOT RUN' -or
    $manifest.cefArchiveSha256 -cne '1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a') {
    throw 'Wrong smoke bundle manifest.'
}
function Assert-Inventory {
    $items = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected bundle root.' }
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -ne 0) { throw 'Redirected bundle item.' }
    $actual = @($items | Where-Object { -not $_.PSIsContainer -and $_.FullName -ne $manifestPath })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($pin in $manifest.files) {
        if ($pin.path -notmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$' -or
            @($pin.path.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -ne 0 -or
            -not $seen.Add($pin.path) -or $pin.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid inventory pin.' }
        $file = Get-Item -LiteralPath (Join-Path $root $pin.path)
        if ($file.PSIsContainer -or $file.Length -ne $pin.bytes -or
            (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant() -cne $pin.sha256) {
            throw "Bundle file mismatch: $($pin.path)"
        }
    }
    if ($actual.Count -ne $seen.Count) { throw 'Extra or missing bundle files.' }
    foreach ($file in $actual) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        if (-not $seen.Contains($relative)) { throw "Unexpected bundle file: $relative" }
    }
}
Assert-Inventory
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if ((Test-Path -LiteralPath $evidence) -or $evidence.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a new evidence directory outside the extracted bundle.'
}
$null = New-Item -ItemType Directory -Path $evidence
$report = [ordered]@{
    scope = 'nested-win32-smoke-only'
    sourceCommit = $ExpectedCommit
    manifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash
    status = 'NOT RUN'
    productReleaseGates = 'NOT RUN'
}
try {
    $report.status = 'RUNNING'
    $program = Join-Path $root 'runner/CefNestedSmoke.exe'
    $smoke = Join-Path $root 'smoke/veil-cef-smoke.exe'
    $scratch = Join-Path $evidence 'scratch'
    foreach ($arg in @($smoke, $scratch)) { if ($arg.Contains('"') -or $arg.EndsWith('\')) { throw 'Invalid fixture path.' } }
    $child = Start-Process -FilePath $program -ArgumentList @('"' + $smoke + '"', '"' + $scratch + '"') -WorkingDirectory $root -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $evidence 'stdout.json') -RedirectStandardError (Join-Path $evidence 'stderr.log')
    try {
        if (-not $child.WaitForExit(60000)) {
            # The isolated runner owns kill-on-close jobs. Kill only the
            # retained runner, never unrelated browser names or processes.
            $child.Kill()
            if (-not $child.WaitForExit(10000)) { throw 'Runner cleanup unconfirmed.' }
            throw 'Nested fixture timed out.'
        }
        $child.Refresh()
        if ($child.ExitCode -ne 0) { throw "Nested fixture failed: $($child.ExitCode)" }
    } finally { $child.Dispose() }
    Assert-Inventory
    $sample = Get-Content -LiteralPath (Join-Path $evidence 'stdout.json') -Raw | ConvertFrom-Json
    if ($sample.scope -cne 'nested-win32-smoke-only' -or $sample.exit -ne 0 -or
        $sample.outerEmpty -ne $true -or $sample.innerEmpty -ne $true -or $sample.restricted -ne $true) {
        throw 'Missing required fixture evidence.'
    }
    $report.status = 'PASS'
} catch {
    $report.status = 'FAIL'
    throw
} finally {
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    Write-Host "Retained evidence: $evidence"
}
