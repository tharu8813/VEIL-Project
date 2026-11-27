# SPDX-License-Identifier: MPL-2.0
# Registration preparation, never publication. No existing registration edits.
. (Join-Path $PSScriptRoot 'cef-preflight-staging.ps1')
. (Join-Path $PSScriptRoot 'cef-registration-plan.ps1')

function New-VeilPreflightRegistrationDraft($StagePlan,[string]$BinRoot,[string]$ImagePath,[string]$ProfilePath,
    [guid]$VolumeGuid,[guid]$ProtectorId,[guid]$RecoveryProtectorId,[string]$UserSid,[string]$VolumeId) {
    if ($StagePlan.kind -cne 'cef-preflight-stage-plan' -or
        $StagePlan.installable -isnot [bool] -or $StagePlan.installable -or
        $StagePlan.startupEnabled -isnot [bool] -or $StagePlan.startupEnabled -or
        $StagePlan.sourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or
        $StagePlan.runtimePins.kind -cne 'cef-admin-runtime-pins' -or
        $StagePlan.runtimePins.sourceCommit -cne $StagePlan.sourceCommit -or
        $StagePlan.runtimePins.startupEnabled -isnot [bool] -or $StagePlan.runtimePins.startupEnabled) {
        throw 'Expected disabled verified preflight stage plan.'
    }
    $proofNames = @('cef-candidate.json','cef-candidate.p7s','preflight-package.json','preflight-package.p7s')
    if ($StagePlan.signerThumbprint -isnot [string] -or $StagePlan.signerThumbprint -cnotmatch '\A[0-9a-f]{40}\z' -or
        $null -eq $StagePlan.proofHashes -or @($StagePlan.proofHashes.PSObject.Properties).Count -ne 4) {
        throw 'Full signed package evidence binding required.'
    }
    foreach ($name in $proofNames) {
        $value = $StagePlan.proofHashes.$name
        if ($value -isnot [string] -or $value -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Missing signed proof digest.' }
    }
    foreach ($hash in @($StagePlan.desktopSha256,$StagePlan.brokerSha256,$StagePlan.cefChildSha256)) {
        if ($hash -isnot [string] -or $hash -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Missing image pin.' }
    }
    $child = @($StagePlan.runtimePins.files | Where-Object { $_.path -ceq 'veil-cef-product.exe' })
    if ($child.Count -ne 1 -or $child[0].sha256 -cne $StagePlan.cefChildSha256) { throw 'CEF child pin mismatch.' }
    if ($VolumeGuid -eq [guid]::Empty -or $ProtectorId -eq [guid]::Empty -or
        $RecoveryProtectorId -eq [guid]::Empty -or $ProtectorId -eq $RecoveryProtectorId) {
        throw 'Expected distinct nonempty registered protector IDs and volume GUID.'
    }
    $sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
    if (-not $sid.IsAccountSid()) { throw 'Expected a single account SID.' }
    if ($ImagePath -notmatch '\A[A-Za-z]:\\[^\\].+\.vhdx\z' -or $ImagePath -ne [IO.Path]::GetFullPath($ImagePath) -or
        $ImagePath.Substring(2).Contains(':') -or $ImagePath.Contains('/')) { throw 'Expected canonical local VHDX path.' }
    $desktop = Join-Path $BinRoot 'Desktop\Veil.Desktop.exe'
    $brokerImage = Join-Path $BinRoot 'Broker\Veil.VolumeBroker.exe'
    $cef = Join-Path $BinRoot 'Cef\veil-cef-product.exe'
    $broker = [ordered]@{
        volumeId=$VolumeId; imagePath=$ImagePath; volumeGuid=('\\?\Volume{' + $VolumeGuid.ToString() + '}\')
        protectorId=$ProtectorId.ToString('B'); recoveryProtectorId=$RecoveryProtectorId.ToString('B')
        userSid=$sid.Value; clientImages=@($desktop); clientSha256=$StagePlan.desktopSha256
        cefChildImage=$cef; cefChildSha256=$StagePlan.cefChildSha256; pipeName='VEIL.VolumeBroker'
        backend='bitlocker'; development=$false; allowUnsignedClients=$false
    }
    $client = [ordered]@{
        volumeId=$VolumeId; pipeName='VEIL.VolumeBroker'; brokerImage=$brokerImage
        profilePath=$ProfilePath; requireWindowsService=$true; development=$false
    }
    [void](Get-VeilCefRegistrationPlan ([pscustomobject]$broker) ([pscustomobject]$client) $brokerImage $desktop $cef $StagePlan.desktopSha256 $StagePlan.cefChildSha256)
    return [ordered]@{ 'broker.json'=$broker; 'client.json'=$client; 'cef-runtime-pins.json'=$StagePlan.runtimePins }
}

function Write-VeilPreflightDraftFile([string]$Path,$Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 10))
    if ($bytes.Length -le 0 -or $bytes.Length -gt 4MB) { throw 'Invalid draft length.' }
    $stream = [IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    Assert-VeilAdminPath $Path
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -cne [Convert]::ToBase64String($bytes)) {
        throw 'Draft bytes changed.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Write-VeilPreflightRegistrationDraft([string]$Destination,$Draft,$StagePlan) {
    if (Test-Path -LiteralPath $Destination) { throw 'Preserve existing registration preparation for inspection.' }
    Assert-VeilAdminPath (Split-Path -Parent $Destination)
    $names = @('broker.json','client.json','cef-runtime-pins.json')
    if ($Draft.Count -ne 3 -or @($names | Where-Object { -not $Draft.Contains($_) }).Count -ne 0) { throw 'Invalid draft file set.' }
    New-VeilRestrictedDirectory $Destination
    $files = @()
    foreach ($name in $names) {
        $path = Join-Path $Destination $name
        $digest = Write-VeilPreflightDraftFile $path $Draft[$name]
        $files += [ordered]@{name=$name;sha256=$digest}
    }
    # Last record is only preparation evidence, not a commit/activation marker.
    # Partial writes remain preserved; no automatic resume or deletion.
    $journal = New-VeilPreflightPreparationJournal $StagePlan $files
    foreach ($file in $files) {
        if ((Get-FileHash -LiteralPath (Join-Path $Destination $file.name) -Algorithm SHA256).Hash -ne $file.sha256) {
            throw 'Registration draft changed before preparation marker.'
        }
    }
    [void](Write-VeilPreflightDraftFile (Join-Path $Destination 'preparation.json') $journal)
    return $journal
}


function New-VeilPreflightPreparationJournal($StagePlan,[object[]]$Files) {
    return [ordered]@{
        kind='cef-preflight-registration-journal'; format=2; state='prepared-only'
        sourceCommit=$StagePlan.sourceCommit; installable=$false; startupEnabled=$false
        desktopSha256=$StagePlan.desktopSha256; brokerSha256=$StagePlan.brokerSha256
        cefChildSha256=$StagePlan.cefChildSha256
        signerThumbprint=$StagePlan.signerThumbprint; proofHashes=$StagePlan.proofHashes; files=$Files
    }
}

function Read-VeilPreflightDraftBytes([string]$Path) {
    Assert-VeilAdminPath $Path
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer) { throw 'Expected a registration file.' }
    $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt 4MB) { throw 'Invalid registration draft length.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $count = $stream.Read($bytes,$offset,$bytes.Length-$offset)
            if ($count -le 0) { throw 'Incomplete registration draft read.' }
            $offset += $count
        }
        if ($stream.ReadByte() -ne -1) { throw 'Registration draft length changed.' }
        return ,$bytes
    } finally { $stream.Dispose() }
}

function Assert-VeilPreflightDraftBytes([byte[]]$Actual,$Expected) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Expected | ConvertTo-Json -Depth 10))
    # Compare the exact writer format: duplicate keys, unknown fields and
    # alternate encodings cannot be accepted via a permissive JSON parser.
    if ([Convert]::ToBase64String($Actual) -cne [Convert]::ToBase64String($bytes)) {
        throw 'Registration draft differs from independently reconstructed content.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Actual))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Read-VeilPreflightRegistrationDraft([string]$Directory,$StagePlan,[string]$BinRoot,[string]$ImagePath,[string]$ProfilePath,
    [guid]$VolumeGuid,[guid]$ProtectorId,[guid]$RecoveryProtectorId,[string]$UserSid) {
    Assert-VeilAdminPath $Directory
    $names = @('broker.json','client.json','cef-runtime-pins.json','preparation.json')
    $items = @(Get-ChildItem -LiteralPath $Directory -Force)
    if ($items.Count -ne 4 -or @($items | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $names }).Count -ne 0) {
        throw 'Incomplete or unexpected registration preparation. Preserve it for inspection.'
    }
    $observed = @{}
    foreach ($name in $names) { $observed[$name] = Read-VeilPreflightDraftBytes (Join-Path $Directory $name) }
    # Only the generated opaque volume ID is recovered from disk. Every
    # security-sensitive field comes from independently supplied inputs and
    # a newly verified stage. The ID is validated by the draft constructor.
    $broker = [Text.UTF8Encoding]::new($false,$true).GetString($observed['broker.json']) | ConvertFrom-Json
    if ($broker.volumeId -isnot [string]) { throw 'Missing prepared volume ID.' }
    $draft = New-VeilPreflightRegistrationDraft $StagePlan $BinRoot $ImagePath $ProfilePath $VolumeGuid $ProtectorId $RecoveryProtectorId $UserSid $broker.volumeId
    $files = @()
    foreach ($name in @('broker.json','client.json','cef-runtime-pins.json')) {
        $digest = Assert-VeilPreflightDraftBytes $observed[$name] $draft[$name]
        $files += [ordered]@{name=$name;sha256=$digest}
    }
    $journal = New-VeilPreflightPreparationJournal $StagePlan $files
    [void](Assert-VeilPreflightDraftBytes $observed['preparation.json'] $journal)
    # Check a second observation for ordinary concurrent changes. This is
    # still a read-only snapshot, not retained file handles or launch authority.
    foreach ($name in $names) {
        if ([Convert]::ToBase64String((Read-VeilPreflightDraftBytes (Join-Path $Directory $name))) -cne
            [Convert]::ToBase64String($observed[$name])) { throw 'Registration changed during inspection.' }
    }
    return $journal
}
