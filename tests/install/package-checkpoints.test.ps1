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

# Execute the actual production functions with only the ACL check mocked.
# This is synthetic filesystem evidence, not an installed-service or power-loss test.
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'production script parses'
foreach ($name in @('Write-Durable','Assert-CommitMarker','Restore-VeilRegistration')) {
    $nodes = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true))
    Check ($nodes.Count -eq 1) "unique production function $name"
    . ([scriptblock]::Create($nodes[0].Extent.Text))
}
$script:RejectJournalAcl = $false
function Assert-VeilAdminPath([string]$Path) {
    if ($script:RejectJournalAcl) { throw 'Synthetic ACL rejection before publication.' }
}
$lab = Join-Path ([IO.Path]::GetTempPath()) ('veil-journal-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($lab)
try {
    $record = Join-Path $lab 'broker.previous.json'
    $payload = [Text.Encoding]::UTF8.GetBytes('{"synthetic":true}')
    Write-Durable $record $payload
    Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($record)) -ceq [Convert]::ToBase64String($payload)) 'complete backup published'
    Check (Refused { Write-Durable $record ([byte[]](2)) }) 'existing record cannot be overwritten'
    Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($record)) -ceq [Convert]::ToBase64String($payload)) 'existing record preserved'
    $config = Join-Path $lab 'broker.json'
    $restore = Join-Path $lab 'broker.restore.json'
    [IO.File]::WriteAllBytes($config,$payload)
    # Allow reads but deny replacement/deletion: identical registration needs no write.
    $locked = [IO.File]::Open($config,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { Restore-VeilRegistration $record $config $restore }
    finally { $locked.Dispose() }
    Check (-not (Test-Path -LiteralPath $restore)) 'unchanged registration requires no staging write'
    [IO.File]::WriteAllBytes($config,[byte[]](2))
    $locked = [IO.File]::Open($config,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        Check (Refused { Restore-VeilRegistration $record $config $restore }) 'changed locked registration fails closed'
    } finally { $locked.Dispose() }
    Check ([IO.File]::ReadAllBytes($config)[0] -eq 2) 'failed replacement preserves current registration'
    Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($record)) -ceq [Convert]::ToBase64String($payload)) 'failed replacement preserves backup'
    Restore-VeilRegistration $record $config $restore
    Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($config)) -ceq [Convert]::ToBase64String($payload)) 'retry restores exact registration'
    Check (-not (Test-Path -LiteralPath $restore)) 'successful replacement consumes staging file'
    $marker = Join-Path $lab 'committed'
    $script:RejectJournalAcl = $true
    Check (Refused { Write-Durable $marker ([byte[]](1)) }) 'pre-publication failure refused'
    $script:RejectJournalAcl = $false
    Check (-not (Test-Path -LiteralPath $marker)) 'failed write has no final marker'
    Check (@(Get-ChildItem -LiteralPath $lab -Force | Where-Object Name -like '*.pending-*').Count -eq 0) 'temporary records cleaned'
    Write-Durable $marker ([byte[]](1))
    Assert-CommitMarker $marker
    foreach ($bad in @('', 'Ag==', 'AQI=')) {
        [IO.File]::WriteAllBytes($marker,[Convert]::FromBase64String($bad))
        Check (Refused { Assert-CommitMarker $marker }) 'invalid or partial marker refused'
    }
} finally {
    Remove-Item -LiteralPath $lab -Recurse -Force
}
