# SPDX-License-Identifier: MPL-2.0
# Fresh disposable-VM publication only. No service creation or startup.
. (Join-Path $PSScriptRoot 'cef-preflight-registration.ps1')

function Assert-VeilPreflightPublicationEqual($Actual,$Expected) {
    if (($Actual | ConvertTo-Json -Depth 12 -Compress) -cne ($Expected | ConvertTo-Json -Depth 12 -Compress)) {
        throw 'Preflight publication evidence changed.'
    }
}

function Publish-VeilPreflightFiles([string]$Stage,[string]$Prepared,[string]$BinRoot,[string]$DataRoot,
    [string]$TransactionRoot,[string]$Thumbprint,[string]$Commit,[string]$ImagePath,[string]$ProfilePath,
    [guid]$VolumeGuid,[guid]$ProtectorId,[guid]$RecoveryProtectorId,[string]$UserSid,[scriptblock]$RequireSafeState) {
    $binPending = $BinRoot + '.cef-preflight-pending'
    $dataPending = $DataRoot + '.cef-preflight-pending'
    $targets = @($BinRoot,$DataRoot,$TransactionRoot,$binPending,$dataPending)
    foreach ($target in $targets) {
        if (Test-Path -LiteralPath $target) { throw 'Existing or interrupted publication refused. Preserve all files.' }
    }
    # The production caller checks the fixed roots, absent service, and
    # detached VHDX at every publication boundary.
    & $RequireSafeState
    $plan = Get-VeilPreflightStagePlan $Stage $Thumbprint $Commit
    $journal = Read-VeilPreflightRegistrationDraft $Prepared $plan $BinRoot $ImagePath $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid
    $payload = Join-Path $Stage 'payload'
    $cefEvidence = Join-Path $Stage 'cef-evidence'
    $proof = Join-Path $Stage 'package-evidence'
    $manifest = Read-VeilPreflightPackageEvidence $payload $cefEvidence $proof $Thumbprint $Commit
    $proofHash = (Get-FileHash -LiteralPath (Join-Path $proof 'preflight-package.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $prepHash = (Get-FileHash -LiteralPath (Join-Path $Prepared 'preparation.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    # Claim a fresh journal directory before creating either pending tree.
    # Never delete or reuse a partial transaction automatically.
    New-VeilRestrictedDirectory $TransactionRoot
    $intent = [ordered]@{
        kind='cef-preflight-publication';format=1;state='intent-only';sourceCommit=$Commit
        packageManifestSha256=$proofHash;preparationSha256=$prepHash
        startupEnabled=$false;serviceCreated=$false
    }
    $intentHash = Write-VeilPreflightDraftFile (Join-Path $TransactionRoot '01-intent.json') $intent
    New-VeilRestrictedDirectory $binPending
    New-VeilRestrictedDirectory $dataPending
    foreach ($entry in $manifest.files) {
        $relative = $entry.path.Replace('/','\')
        $from = Join-Path $payload $relative
        $to = Join-Path $binPending $relative
        Assert-VeilAdminPath $from
        $directory = Split-Path -Parent $to
        [void][IO.Directory]::CreateDirectory($directory)
        Assert-VeilAdminPath $directory
        [IO.File]::Copy($from,$to,$false)
    }
    foreach ($name in @('broker.json','client.json','cef-runtime-pins.json','preparation.json')) {
        $from = Join-Path $Prepared $name
        Assert-VeilAdminPath $from
        [IO.File]::Copy($from,(Join-Path $dataPending $name),$false)
    }
    function Assert-VerifiedPair([string]$Binaries,[string]$Registration) {
        Assert-VeilPreflightPublicationEqual (Read-VeilPreflightPackageEvidence $Binaries $cefEvidence $proof $Thumbprint $Commit) $manifest
        Assert-VeilPreflightPublicationEqual (Read-VeilPreflightRegistrationDraft $Registration $plan $BinRoot $ImagePath $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid) $journal
        if ((Get-FileHash -LiteralPath (Join-Path $proof 'preflight-package.json') -Algorithm SHA256).Hash -ne $proofHash -or
            (Get-FileHash -LiteralPath (Join-Path $Registration 'preparation.json') -Algorithm SHA256).Hash -ne $prepHash) {
            throw 'Publication source binding changed.'
        }
    }
    Assert-VerifiedPair $binPending $dataPending
    Assert-VerifiedPair $payload $Prepared
    & $RequireSafeState
    if ((Test-Path -LiteralPath $BinRoot) -or (Test-Path -LiteralPath $DataRoot)) { throw 'Live destination appeared.' }
    [void](Write-VeilPreflightDraftFile (Join-Path $TransactionRoot '02-copies-verified.json') ([ordered]@{
        state='copies-verified';intentSha256=$intentHash;startupEnabled=$false
    }))
    # Each same-parent rename is a separate boundary, not an atomic pair.
    # On interruption the intent and verified-copy record remain available.
    [IO.Directory]::Move($binPending,$BinRoot)
    Assert-VerifiedPair $BinRoot $dataPending
    [void](Write-VeilPreflightDraftFile (Join-Path $TransactionRoot '03-binaries-published.json') ([ordered]@{
        state='binaries-published';intentSha256=$intentHash;startupEnabled=$false
    }))
    & $RequireSafeState
    if (Test-Path -LiteralPath $DataRoot) { throw 'Live registration appeared.' }
    [IO.Directory]::Move($dataPending,$DataRoot)
    Assert-VerifiedPair $BinRoot $DataRoot
    & $RequireSafeState
    $final = [ordered]@{
        kind='cef-preflight-publication';format=1;state='files-published-only';sourceCommit=$Commit
        intentSha256=$intentHash;startupEnabled=$false;serviceCreated=$false
    }
    [void](Write-VeilPreflightDraftFile (Join-Path $TransactionRoot '04-files-published.json') $final)
    return $final
}


# Only an exact prefix of the four original journal records is accepted.
# Missing records after a rename are distinguishable from an incomplete copy.
function Get-VeilPreflightPublicationState([string]$Stage,[string]$Prepared,[string]$BinRoot,[string]$DataRoot,
    [string]$TransactionRoot,[string]$Thumbprint,[string]$Commit,[string]$ImagePath,[string]$ProfilePath,
    [guid]$VolumeGuid,[guid]$ProtectorId,[guid]$RecoveryProtectorId,[string]$UserSid,[scriptblock]$RequireSafeState) {
    & $RequireSafeState
    Assert-VeilAdminPath $TransactionRoot
    $records = @('01-intent.json','02-copies-verified.json','03-binaries-published.json','04-files-published.json')
    $items = @(Get-ChildItem -LiteralPath $TransactionRoot -Force)
    if ($items.Count -lt 1 -or $items.Count -gt 4 -or @($items | Where-Object {
        $_.PSIsContainer -or $_.Name -cnotin $records[0..($items.Count-1)]
    }).Count -ne 0) { throw 'Missing, unexpected or out-of-order publication record.' }
    $count = $items.Count
    $plan = Get-VeilPreflightStagePlan $Stage $Thumbprint $Commit
    $journal = Read-VeilPreflightRegistrationDraft $Prepared $plan $BinRoot $ImagePath $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid
    $payload = Join-Path $Stage 'payload'
    $cefEvidence = Join-Path $Stage 'cef-evidence'
    $proof = Join-Path $Stage 'package-evidence'
    $manifest = Read-VeilPreflightPackageEvidence $payload $cefEvidence $proof $Thumbprint $Commit
    $proofHash = (Get-FileHash -LiteralPath (Join-Path $proof 'preflight-package.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $prepHash = (Get-FileHash -LiteralPath (Join-Path $Prepared 'preparation.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $intent = [ordered]@{
        kind='cef-preflight-publication';format=1;state='intent-only';sourceCommit=$Commit
        packageManifestSha256=$proofHash;preparationSha256=$prepHash
        startupEnabled=$false;serviceCreated=$false
    }
    $intentHash = Assert-VeilPreflightDraftBytes (Read-VeilPreflightDraftBytes (Join-Path $TransactionRoot $records[0])) $intent
    $expected = @($intent,
        [ordered]@{state='copies-verified';intentSha256=$intentHash;startupEnabled=$false},
        [ordered]@{state='binaries-published';intentSha256=$intentHash;startupEnabled=$false},
        [ordered]@{kind='cef-preflight-publication';format=1;state='files-published-only';sourceCommit=$Commit;intentSha256=$intentHash;startupEnabled=$false;serviceCreated=$false})
    for ($i=1; $i -lt $count; $i++) {
        [void](Assert-VeilPreflightDraftBytes (Read-VeilPreflightDraftBytes (Join-Path $TransactionRoot $records[$i])) $expected[$i])
    }
    $binPending = $BinRoot + '.cef-preflight-pending'
    $dataPending = $DataRoot + '.cef-preflight-pending'
    $b = Test-Path -LiteralPath $BinRoot
    $d = Test-Path -LiteralPath $DataRoot
    $bp = Test-Path -LiteralPath $binPending
    $dp = Test-Path -LiteralPath $dataPending
    $state = 'incomplete-copies'
    if ($count -eq 1) {
        if ($b -or $d) { throw 'Live paths without verified-copy record.' }
        foreach ($pending in @($binPending,$dataPending)) {
            if (Test-Path -LiteralPath $pending) { Assert-VeilAdminPath $pending }
        }
    } else {
        if (($b -eq $bp) -or ($d -eq $dp) -or ($d -and -not $b)) { throw 'Ambiguous publication paths.' }
        if ($count -eq 2 -and -not $b -and -not $d) { $state='publish-binaries' }
        elseif ($count -eq 2 -and $b -and -not $d) { $state='record-binaries' }
        elseif ($count -eq 3 -and $b -and -not $d) { $state='publish-registration' }
        elseif ($count -eq 3 -and $b -and $d) { $state='record-completion' }
        elseif ($count -eq 4 -and $b -and $d) { $state='files-published-only' }
        else { throw 'Journal does not match current publication boundary.' }
        $actualBin = if ($b) { $BinRoot } else { $binPending }
        $actualData = if ($d) { $DataRoot } else { $dataPending }
        Assert-VeilPreflightPublicationEqual (Read-VeilPreflightPackageEvidence $actualBin $cefEvidence $proof $Thumbprint $Commit) $manifest
        Assert-VeilPreflightPublicationEqual (Read-VeilPreflightRegistrationDraft $actualData $plan $BinRoot $ImagePath $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid) $journal
        if ((Get-FileHash -LiteralPath (Join-Path $actualData 'preparation.json') -Algorithm SHA256).Hash -ne $prepHash) {
            throw 'Published registration differs from prepared evidence.'
        }
    }
    & $RequireSafeState
    if (@(Get-ChildItem -LiteralPath $TransactionRoot -Force).Count -ne $count -or
        (Test-Path -LiteralPath $BinRoot) -ne $b -or (Test-Path -LiteralPath $DataRoot) -ne $d -or
        (Test-Path -LiteralPath $binPending) -ne $bp -or (Test-Path -LiteralPath $dataPending) -ne $dp) { throw 'Publication boundary changed during inspection.' }
    return [pscustomobject]@{state=$state;intentSha256=$intentHash;sourceCommit=$Commit;startupEnabled=$false;serviceCreated=$false}
}

function Resume-VeilPreflightPublication([string]$Stage,[string]$Prepared,[string]$BinRoot,[string]$DataRoot,
    [string]$TransactionRoot,[string]$Thumbprint,[string]$Commit,[string]$ImagePath,[string]$ProfilePath,
    [guid]$VolumeGuid,[guid]$ProtectorId,[guid]$RecoveryProtectorId,[string]$UserSid,[scriptblock]$RequireSafeState) {
    # Recheck all authenticated evidence before each single move or marker.
    # No deletion, replacement, or service operations are permitted here.
    for ($i=0; $i -lt 5; $i++) {
        $observed = Get-VeilPreflightPublicationState $Stage $Prepared $BinRoot $DataRoot $TransactionRoot $Thumbprint $Commit $ImagePath $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid $RequireSafeState
        if ($observed.state -eq 'files-published-only') { return $observed }
        & $RequireSafeState
        switch ($observed.state) {
            'publish-binaries' { [IO.Directory]::Move(($BinRoot + '.cef-preflight-pending'),$BinRoot) }
            'record-binaries' {
                [void](Write-VeilPreflightDraftFile (Join-Path $TransactionRoot '03-binaries-published.json') ([ordered]@{
                    state='binaries-published';intentSha256=$observed.intentSha256;startupEnabled=$false
                }))
            }
            'publish-registration' { [IO.Directory]::Move(($DataRoot + '.cef-preflight-pending'),$DataRoot) }
            'record-completion' {
                [void](Write-VeilPreflightDraftFile (Join-Path $TransactionRoot '04-files-published.json') ([ordered]@{
                    kind='cef-preflight-publication';format=1;state='files-published-only';sourceCommit=$Commit
                    intentSha256=$observed.intentSha256;startupEnabled=$false;serviceCreated=$false
                }))
            }
            default { throw 'No verified-copy record; preserve incomplete transaction and restore a clean VM snapshot.' }
        }
    }
    throw 'Publication recovery exceeded known boundaries.'
}
