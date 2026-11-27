#requires -RunAsAdministrator
# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$SignerThumbprint)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Use elevated 64-bit Windows PowerShell 5.1.'
}
. (Join-Path $PSScriptRoot 'package-common.ps1')
. (Join-Path $PSScriptRoot 'cef-registration-plan.ps1')

# Proposed future layout only. The current package does not contain this host.
$bin = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'VEIL'
$data = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'VEIL'
$brokerPath = Join-Path $data 'broker.json'
$clientPath = Join-Path $data 'client.json'
$brokerImage = Join-Path $bin 'Broker\Veil.VolumeBroker.exe'
$desktopImage = Join-Path $bin 'Desktop\Veil.Desktop.exe'
$cefImage = Join-Path $bin 'Cef\veil-cef-product.exe'
$paths = @($bin,$data,$brokerPath,$clientPath,$brokerImage,$desktopImage,$cefImage,
    (Split-Path -Parent $brokerImage),(Split-Path -Parent $desktopImage),(Split-Path -Parent $cefImage))
foreach ($path in $paths) { Assert-VeilAdminPath $path }
$service = Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'"
if (-not $service -or $service.State -ne 'Stopped' -or $service.StartName -ne 'LocalSystem' -or
    $service.PathName -ne ('"{0}" --service' -f $brokerImage)) {
    throw 'Stop the registered VEIL broker before inspecting CEF registration.'
}
foreach ($file in @($brokerImage,$desktopImage,$cefImage)) { Assert-VeilSignedImage $file $SignerThumbprint }
$hashPaths = @($brokerPath,$clientPath,$brokerImage,$desktopImage,$cefImage)
$before = @{}
foreach ($path in $hashPaths) { $before[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
foreach ($path in @($brokerPath,$clientPath)) {
    if ((Get-Item -LiteralPath $path).Length -gt 64KB) { throw 'Oversized registration.' }
}
$broker = Get-Content -LiteralPath $brokerPath -Raw | ConvertFrom-Json
$client = Get-Content -LiteralPath $clientPath -Raw | ConvertFrom-Json
$plan = Get-VeilCefRegistrationPlan $broker $client $brokerImage $desktopImage $cefImage $before[$desktopImage] $before[$cefImage]
foreach ($path in $paths) { Assert-VeilAdminPath $path }
foreach ($path in $hashPaths) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $before[$path]) { throw 'Registration or image changed during planning.' }
}
$service = Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'"
if (-not $service -or $service.State -ne 'Stopped' -or $service.StartName -ne 'LocalSystem' -or
    $service.PathName -ne ('"{0}" --service' -f $brokerImage)) {
    throw 'Broker service state changed during planning.'
}
# Do not emit paths or IDs into logs. No registry publication, service change,
# package trust, volume verification or product authorization is implied.
Write-Host 'PASS: CEF registration plan matches the installed configuration and observed executable pins.'
Write-Host 'NOT RUN: registration publication, full package trust, BitLocker and product startup.'
