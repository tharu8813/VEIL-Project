# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\cef-registration-plan.ps1')
$brokerImage = 'C:\Program Files\VEIL\Broker\Veil.VolumeBroker.exe'
$desktopImage = 'C:\Program Files\VEIL\Desktop\Veil.Desktop.exe'
$cefImage = 'C:\Program Files\VEIL\Cef\veil-cef-product.exe'
$desktopHash = 'a' * 64
$cefHash = 'b' * 64
function New-Broker {
    [pscustomobject]@{
        volumeId='synthetic'; volumeGuid='\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\'
        pipeName='VEIL.VolumeBroker'; clientImages=@($desktopImage); clientSha256=$desktopHash
        cefChildImage=$cefImage; cefChildSha256=$cefHash
    }
}
function New-Client {
    [pscustomobject]@{
        volumeId='synthetic'; pipeName='VEIL.VolumeBroker'; requireWindowsService=$true
        brokerImage=$brokerImage; profilePath='V:\VEIL\profile'
    }
}
function Plan($b,$c) {
    Get-VeilCefRegistrationPlan $b $c $brokerImage $desktopImage $cefImage $desktopHash $cefHash
}
function Reject([scriptblock]$Action) {
    $rejected=$false
    try { [void](& $Action) } catch { $rejected=$true }
    if (-not $rejected) { throw 'Invalid synthetic registration accepted.' }
}
$b=New-Broker; $c=New-Client
$plan=Plan $b $c
if ($plan.ProductRoot -cne $c.profilePath -or $plan.VolumeGuid -cne $b.volumeGuid -or
    $plan.VolumeId -cne $b.volumeId -or @($plan.PSObject.Properties).Count -ne 3) {
    throw 'Registration plan does not match native contract.'
}
Reject { $b=New-Broker; $c=New-Client; $c.volumeId='other'; Plan $b $c }
Reject { $b=New-Broker; $b.volumeGuid='wrong'; Plan $b (New-Client) }
Reject { $b=New-Broker; $b.volumeId='bad/id'; Plan $b (New-Client) }
Reject { $b=New-Broker; $b.pipeName='other'; Plan $b (New-Client) }
Reject { $c=New-Client; $c.requireWindowsService='true'; Plan (New-Broker) $c }
Reject { $c=New-Client; $c.profilePath='V:\'; Plan (New-Broker) $c }
Reject { $c=New-Client; $c.profilePath='V:\VEIL\..\profile'; Plan (New-Broker) $c }
Reject { $c=New-Client; $c.profilePath='V:\VEIL\profile:alternate'; Plan (New-Broker) $c }
Reject { $b=New-Broker; $b.cefChildSha256=$null; Plan $b (New-Client) }
Reject { $b=New-Broker; $b.cefChildSha256='c'*64; Plan $b (New-Client) }
Reject { $b=New-Broker; $b.clientImages=@($desktopImage,$cefImage); Plan $b (New-Client) }
Reject { $b=New-Broker; $b.cefChildImage=$desktopImage; Plan $b (New-Client) }
Reject { $b=New-Broker; $b | Add-Member NoteProperty development $true; Plan $b (New-Client) }
Reject { $b=New-Broker; $b | Add-Member NoteProperty allowUnsignedClients $true; Plan $b (New-Client) }
Write-Host 'PASS: synthetic CEF registration plan consistency and refusal checks'
