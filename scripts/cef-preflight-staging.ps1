# SPDX-License-Identifier: MPL-2.0
# Pre-publication only: no installed path, service, volume or registration mutation.
. (Join-Path $PSScriptRoot 'cef-preflight-package.ps1')

function Get-VeilPreflightProofHashes([string]$CefEvidence,[string]$PackageEvidence) {
    $hashes = [ordered]@{}
    foreach ($pair in @(
        @{root=$CefEvidence;name='cef-candidate.json'},
        @{root=$CefEvidence;name='cef-candidate.p7s'},
        @{root=$PackageEvidence;name='preflight-package.json'},
        @{root=$PackageEvidence;name='preflight-package.p7s'}
    )) {
        Assert-VeilAdminPath $pair.root
        $path = Join-Path $pair.root $pair.name
        Assert-VeilAdminPath $path
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -or $item.Length -le 0 -or $item.Length -gt 4MB) { throw 'Invalid signed proof file.' }
        $hashes.Add($pair.name,(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())
    }
    return [pscustomobject]$hashes
}

function Get-VeilPreflightStagePlan([string]$Stage,[string]$Thumbprint,[string]$Commit,[switch]$Diagnostic) {
    Assert-VeilAdminPath $Stage
    $items = @(Get-ChildItem -LiteralPath $Stage -Force)
    if ($items.Count -ne 3 -or @($items | Where-Object {
        -not $_.PSIsContainer -or $_.Name -cnotin @('payload','cef-evidence','package-evidence')
    }).Count -ne 0) { throw 'Incomplete or unexpected preflight staging layout.' }
    $payload = Join-Path $Stage 'payload'
    $cefEvidence = Join-Path $Stage 'cef-evidence'
    $proof = Join-Path $Stage 'package-evidence'
    $proofHashes = Get-VeilPreflightProofHashes $cefEvidence $proof
    $manifest = Read-VeilPreflightPackageEvidence $payload $cefEvidence $proof $Thumbprint $Commit -Diagnostic:$Diagnostic
    $runtime = (Get-VeilCefVerifiedRuntimePinPlan (Join-Path $payload 'Cef') $cefEvidence $Thumbprint $Commit) | ConvertFrom-Json
    $byName = @{}
    foreach ($entry in $manifest.files) { $byName.Add($entry.path,$entry) }
    foreach ($pin in $runtime.files) {
        $entry = $byName['Cef/' + $pin.path]
        if ($null -eq $entry -or $entry.path -cne ('Cef/' + $pin.path) -or $entry.bytes -ne $pin.bytes -or $entry.sha256 -cne $pin.sha256) {
            throw 'Runtime plan no longer matches the verified package.'
        }
    }
    if (@($manifest.files | Where-Object { $_.path.StartsWith('Cef/',[StringComparison]::Ordinal) }).Count -ne $runtime.files.Count) {
        throw 'Runtime plan inventory changed.'
    }
    $again = Get-VeilPreflightProofHashes $cefEvidence $proof
    if (($proofHashes | ConvertTo-Json -Compress) -cne ($again | ConvertTo-Json -Compress)) {
        throw 'Signed proof changed during stage verification.'
    }
    # A reviewable snapshot, not an authorization or published configuration.
    $kind = if ($Diagnostic) { 'cef-diagnostic-stage-plan' } else { 'cef-preflight-stage-plan' }
    $plan = [ordered]@{
        kind=$kind; sourceCommit=$Commit; startupEnabled=$false; installable=$false
        desktopSha256=$byName['Desktop/Veil.Desktop.exe'].sha256
        brokerSha256=$byName['Broker/Veil.VolumeBroker.exe'].sha256
        cefChildSha256=$byName['Cef/veil-cef-product.exe'].sha256
        signerThumbprint=$Thumbprint.ToLowerInvariant(); proofHashes=$proofHashes
        runtimePins=$runtime
    }
    if ($Diagnostic) {
        $plan.Add('baselinePayloadSha256',$byName['Broker/cef-system-baseline.json'].sha256)
        $plan.Add('baselineSignatureSha256',$byName['Broker/cef-system-baseline.sig'].sha256)
    }
    return [pscustomobject]$plan
}

function Copy-VeilVerifiedPreflightPackage([string]$Source,[string]$CefEvidence,[string]$PackageEvidence,
    [string]$Stage,[string]$Thumbprint,[string]$Commit,[switch]$Diagnostic) {
    $target = [IO.Path]::GetFullPath($Stage).TrimEnd('\')
    $parent = Split-Path -Parent $target
    Assert-VeilAdminPath $parent
    if (-not (Get-Item -LiteralPath $parent -Force).PSIsContainer) { throw 'Invalid staging parent.' }
    foreach ($inputRoot in @($Source,$CefEvidence,$PackageEvidence)) {
        $inputPath = (Get-Item -LiteralPath $inputRoot -Force).FullName.TrimEnd('\')
        if ($inputPath -eq $target -or $target.StartsWith($inputPath + '\',[StringComparison]::OrdinalIgnoreCase) -or
            $inputPath.StartsWith($target + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Overlapping staging input.' }
    }
    if (Test-Path -LiteralPath $target) { throw 'Existing staging must be preserved for administrator inspection.' }
    $proofFiles = @(
        @{root=$CefEvidence; folder='cef-evidence'; name='cef-candidate.json'},
        @{root=$CefEvidence; folder='cef-evidence'; name='cef-candidate.p7s'},
        @{root=$PackageEvidence; folder='package-evidence'; name='preflight-package.json'},
        @{root=$PackageEvidence; folder='package-evidence'; name='preflight-package.p7s'}
    )
    foreach ($record in $proofFiles) {
        $file = Join-Path $record.root $record.name
        Assert-VeilAdminPath $file
        $item = Get-Item -LiteralPath $file -Force
        if ($item.PSIsContainer -or $item.Length -le 0 -or $item.Length -gt 4MB) { throw 'Invalid staging proof file.' }
        $record.hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    $manifest = Read-VeilPreflightPackageEvidence $Source $CefEvidence $PackageEvidence $Thumbprint $Commit -Diagnostic:$Diagnostic
    # Only create fresh restricted staging after verification. A partial copy
    # remains quarantined; no automatic deletion or reuse of ambiguous state.
    New-VeilRestrictedDirectory $target
    foreach ($name in @('payload','cef-evidence','package-evidence')) {
        New-VeilRestrictedDirectory (Join-Path $target $name)
    }
    $payload = Join-Path $target 'payload'
    # Reuse only the authenticated, previously validated relative file names.
    foreach ($entry in $manifest.files) {
        $relative = $entry.path.Replace('/','\')
        $from = Join-Path $Source $relative
        $to = Join-Path $payload $relative
        Assert-VeilAdminPath $from
        $directory = Split-Path -Parent $to
        [void][IO.Directory]::CreateDirectory($directory)
        Assert-VeilAdminPath $directory
        [IO.File]::Copy($from,$to,$false)
    }
    foreach ($record in $proofFiles) {
        $from = Join-Path $record.root $record.name
        $to = Join-Path (Join-Path $target $record.folder) $record.name
        [IO.File]::Copy($from,$to,$false)
        if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne $record.hash -or
            (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne $record.hash) { throw 'Signed proof changed during staging.' }
    }
    $copied = Read-VeilPreflightPackageEvidence $payload (Join-Path $target 'cef-evidence') (Join-Path $target 'package-evidence') $Thumbprint $Commit -Diagnostic:$Diagnostic
    $sourceAgain = Read-VeilPreflightPackageEvidence $Source $CefEvidence $PackageEvidence $Thumbprint $Commit -Diagnostic:$Diagnostic
    $expected = $manifest | ConvertTo-Json -Depth 8 -Compress
    if (($copied | ConvertTo-Json -Depth 8 -Compress) -cne $expected -or
        ($sourceAgain | ConvertTo-Json -Depth 8 -Compress) -cne $expected) { throw 'Signed package changed during staging.' }
    return (Get-VeilPreflightStagePlan $target $Thumbprint $Commit -Diagnostic:$Diagnostic)
}
