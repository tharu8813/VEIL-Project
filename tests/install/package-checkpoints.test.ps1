# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\vm\package-checkpoints.ps1')
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\scripts\package-maintenance.ps1'))
function Check([bool]$Passed, [string]$Name) {
    if (-not $Passed) { throw $Name }
    Write-Host "PASS $Name"
}
function Refused([scriptblock]$Action) {
    try { & $Action | Out-Null; return $false } catch { return $true }
}
$signal = Join-Path ([IO.Path]::GetTempPath()) "veil-test-'quoted'.ready"
foreach ($phase in $script:VeilUpdateCheckpoints.Keys) {
    $instrumented = New-VeilCheckpointSource $source $phase $signal
    Check ($instrumented.Length -gt $source.Length) "checkpoint inserted $phase"
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseInput($instrumented, [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) "instrumented PowerShell parses $phase"
    $anchor = $script:VeilUpdateCheckpoints[$phase]
    Check (Refused { New-VeilCheckpointSource ($source.Replace($anchor, '# omitted')) $phase $signal }) "missing anchor refused $phase"
    Check (Refused { New-VeilCheckpointSource ($source + [Environment]::NewLine + $anchor) $phase $signal }) "duplicate anchor refused $phase"
}
Assert-VeilUpdateBoundary 'backup-written' $false $true $true $false 'old' 'old' 'new'
Assert-VeilUpdateBoundary 'old-tree-moved' $true $false $true $false 'old' 'old' 'new'
Assert-VeilUpdateBoundary 'new-tree-published' $true $true $false $false 'old' 'old' 'new'
Assert-VeilUpdateBoundary 'pre-commit' $true $true $false $false 'new' 'old' 'new'
Assert-VeilUpdateBoundary 'post-commit' $true $true $false $true 'new' 'old' 'new'
Check (Refused { Assert-VeilUpdateBoundary 'pre-commit' $true $true $false $true 'new' 'old' 'new' }) 'premature commit refused'
Check (Refused { Assert-VeilUpdateBoundary 'post-commit' $true $true $false $true 'old' 'old' 'new' }) 'stale pin refused'
Check (Refused { Assert-VeilUpdateBoundary 'old-tree-moved' $true $true $true $false 'old' 'old' 'new' }) 'unexpected tree refused'
Check (Refused { New-VeilCheckpointSource $source 'unknown' $signal }) 'unknown checkpoint refused'
