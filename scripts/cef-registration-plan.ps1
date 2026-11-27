# SPDX-License-Identifier: MPL-2.0
# Pure planning only. No registry, volume, service or filesystem mutation.
function Get-VeilCefRegistrationPlan {
    param(
        [Parameter(Mandatory)]$Broker,
        [Parameter(Mandatory)]$Client,
        [Parameter(Mandatory)][string]$ExpectedBrokerImage,
        [Parameter(Mandatory)][string]$ExpectedDesktopImage,
        [Parameter(Mandatory)][string]$ExpectedCefImage,
        [Parameter(Mandatory)][string]$ObservedDesktopSha256,
        [Parameter(Mandatory)][string]$ObservedCefSha256
    )
    $id = $Broker.volumeId
    if ($id -isnot [string] -or $id -cnotmatch '\A[A-Za-z0-9_-]{1,64}\z' -or
        $Client.volumeId -isnot [string] -or $Client.volumeId -cne $id) {
        throw 'CEF registration volume ID mismatch.'
    }
    if ($Broker.volumeGuid -isnot [string] -or
        $Broker.volumeGuid -notmatch '\A\\\\\?\\Volume\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}\\\z') {
        throw 'CEF registration volume GUID invalid.'
    }
    if ($Broker.pipeName -cne 'VEIL.VolumeBroker' -or $Client.pipeName -cne 'VEIL.VolumeBroker' -or
        $Client.requireWindowsService -isnot [bool] -or -not $Client.requireWindowsService -or
        ($null -ne $Broker.development -and ($Broker.development -isnot [bool] -or $Broker.development)) -or
        ($null -ne $Client.development -and ($Client.development -isnot [bool] -or $Client.development)) -or
        ($null -ne $Broker.allowUnsignedClients -and ($Broker.allowUnsignedClients -isnot [bool] -or $Broker.allowUnsignedClients)) -or
        ($null -ne $Broker.backend -and $Broker.backend -cne 'bitlocker') -or $null -ne $Broker.fake) {
        throw 'CEF registration requires the installed production service.'
    }
    $profile = $Client.profilePath
    if ($profile -isnot [string] -or $profile.Length -gt 32767 -or
        $profile -notmatch '\A[A-Za-z]:\\[^\\].*\z' -or
        $profile.Contains('/') -or $profile.Contains([char]0) -or
        $profile.Substring(2).Contains(':') -or
        $profile.EndsWith('\') -or $profile -ne [IO.Path]::GetFullPath($profile)) {
        throw 'CEF registration requires a canonical local profile directory.'
    }
    if ($Client.brokerImage -isnot [string] -or $Client.brokerImage -ne $ExpectedBrokerImage -or
        $Broker.clientImages -isnot [array] -or $Broker.clientImages.Count -ne 1 -or
        $Broker.clientImages[0] -isnot [string] -or $Broker.clientImages[0] -ne $ExpectedDesktopImage -or
        $Broker.cefChildImage -isnot [string] -or $Broker.cefChildImage -ne $ExpectedCefImage -or
        $ExpectedCefImage -eq $ExpectedDesktopImage -or $ExpectedCefImage -eq $ExpectedBrokerImage) {
        throw 'CEF registration executable identity mismatch.'
    }
    foreach ($hash in @($Broker.clientSha256, $Broker.cefChildSha256, $ObservedDesktopSha256, $ObservedCefSha256)) {
        if ($hash -isnot [string] -or $hash -notmatch '\A[0-9a-f]{64}\z') { throw 'CEF registration requires complete SHA-256 pins.' }
    }
    if ($Broker.clientSha256 -ne $ObservedDesktopSha256 -or $Broker.cefChildSha256 -ne $ObservedCefSha256) {
        throw 'CEF registration executable hash mismatch.'
    }
    [pscustomobject]@{
        ProductRoot = $profile
        VolumeGuid = $Broker.volumeGuid
        VolumeId = $id
    }
}
