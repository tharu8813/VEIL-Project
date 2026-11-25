# SPDX-License-Identifier: MPL-2.0
# Test-only source transformation. No product switches or fault hooks.
$script:VeilUpdateCheckpoints = [ordered]@{
    'backup-written' = 'Write-Durable $backup ([IO.File]::ReadAllBytes($config))'
    'old-tree-moved' = '[IO.Directory]::Move($bin,$previous)'
    'new-tree-published' = '[IO.Directory]::Move($stage,$bin)'
    'pre-commit' = 'Set-BrokerHash ((Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash)'
    'post-commit' = 'Write-Durable $committed ([byte[]](1))'
}
function New-VeilCheckpointSource([string]$Source, [string]$Checkpoint, [string]$Signal) {
    if ($Checkpoint -cnotin $script:VeilUpdateCheckpoints.Keys) { throw 'Unknown update checkpoint.' }
    if (-not [IO.Path]::IsPathRooted($Signal)) { throw 'Absolute checkpoint signal required.' }
    $anchor = $script:VeilUpdateCheckpoints[$Checkpoint]
    $pattern = '(?m)^([ \t]*)' + [regex]::Escape($anchor) + '[ \t]*(?=\r?$)'
    $hits = [regex]::Matches($Source, $pattern)
    if ($hits.Count -ne 1) { throw 'Expected exactly one update checkpoint anchor.' }
    $hit = $hits[0]
    $indent = $hit.Groups[1].Value
    # Single quoted literal, not a dynamically supplied script expression.
    $path = $Signal.Replace("'", "''")
    $insert = @(
        $indent + 'Write-Durable ' + "'" + $path + "'" + ' ([Text.Encoding]::UTF8.GetBytes(' + "'" + $Checkpoint + "'" + '))'
        $indent + 'Start-Sleep -Seconds 120'
        $indent + "throw 'VM checkpoint timed out without parent termination.'"
    ) -join [Environment]::NewLine
    return $Source.Insert($hit.Index + $hit.Length, [Environment]::NewLine + $insert)
}

function Assert-VeilUpdateBoundary([string]$Checkpoint, [bool]$OldTree, [bool]$NewTree,
    [bool]$Staging, [bool]$Commit, [string]$Pin, [string]$OldPin, [string]$NewPin) {
    if ($Checkpoint -cnotin $script:VeilUpdateCheckpoints.Keys) { throw 'Unknown update checkpoint.' }
    $expected = switch ($Checkpoint) {
        'backup-written' { @($false, $true, $true, $false, $OldPin) }
        'old-tree-moved' { @($true, $false, $true, $false, $OldPin) }
        'new-tree-published' { @($true, $true, $false, $false, $OldPin) }
        'pre-commit' { @($true, $true, $false, $false, $NewPin) }
        'post-commit' { @($true, $true, $false, $true, $NewPin) }
    }
    if ($OldTree -ne $expected[0] -or $NewTree -ne $expected[1] -or
        $Staging -ne $expected[2] -or $Commit -ne $expected[3] -or $Pin -ne $expected[4]) {
        throw "Observed transaction does not match checkpoint $Checkpoint."
    }
}
