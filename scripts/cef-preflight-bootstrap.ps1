# SPDX-License-Identifier: MPL-2.0
# Call only after reconstructing signed publication and registration.
function Get-VeilInstalledBootstrapPlan([string]$Registration,[string]$ProfilePath,[guid]$VolumeGuid) {
    $broker = [Text.UTF8Encoding]::new($false,$true).GetString((Read-VeilPreflightDraftBytes (Join-Path $Registration 'broker.json'))) | ConvertFrom-Json
    $client = [Text.UTF8Encoding]::new($false,$true).GetString((Read-VeilPreflightDraftBytes (Join-Path $Registration 'client.json'))) | ConvertFrom-Json
    $guid = '\\?\Volume{' + $VolumeGuid.ToString() + '}\'
    if ($VolumeGuid -eq [guid]::Empty -or $broker.volumeGuid -cne $guid -or $client.profilePath -cne $ProfilePath -or
        $broker.volumeId -isnot [string] -or $broker.volumeId -cnotmatch '\A[A-Za-z0-9_-]{1,64}\z' -or $client.volumeId -cne $broker.volumeId) {
        throw 'Bootstrap plan does not match verified registration.'
    }
    return [pscustomobject]@{ProductRoot=$ProfilePath;VolumeGuid=$guid;VolumeId=$broker.volumeId}
}

function Assert-VeilInstalledBootstrap([string]$Registration,[string]$ProfilePath,[guid]$VolumeGuid) {
    $plan = Get-VeilInstalledBootstrapPlan $Registration $ProfilePath $VolumeGuid
    [Veil.Preflight.BootstrapRegistry]::Verify($plan.ProductRoot,$plan.VolumeGuid,$plan.VolumeId)
}
