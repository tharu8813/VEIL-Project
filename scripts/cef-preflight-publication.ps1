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
