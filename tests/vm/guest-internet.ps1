<#
Gate D internet-VM session (docs/10-test-strategy.md, internet VM section). Runs ONLY inside the isolated
internet-connected test VM, never on the host and never in the offline lab VM. Three commands:

  1. Elevated PowerShell:   <src>\tests\vm\guest-internet.ps1 -Stage Start -Src <src>
     Starts a pktmon capture of port 53 (plain DNS only; no payloads of other traffic are kept).
  2. Normal-user PowerShell: cd <src>; node tests\vm\internet-batch.mjs --run --internet-vm --desktop-dll <dll>
  3. Elevated PowerShell:   <src>\tests\vm\guest-internet.ps1 -Stage Finish -Src <src>
     Stops the capture and decides dns-path from it; prints the result table.
#>
param(
    [Parameter(Mandatory)][ValidateSet('Start', 'Finish')][string]$Stage,
    [Parameter(Mandatory)][string]$Src
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this stage from an elevated PowerShell in the VM.' }
. (Join-Path $Src 'tests\phase3\phase34-guest-identity.ps1')
$guest = Get-VeilPhase34Guest
if (-not $guest.virtualBox) { throw 'Use the isolated VirtualBox test VM.' }
if ($guest.noDefaultRoute) { throw 'This session needs the internet-connected test VM (no default route found).' }
$capture = Join-Path $Src '.veil-dev\internet-dns.etl'
$pktmon = Join-Path $env:SystemRoot 'System32\pktmon.exe'

if ($Stage -eq 'Start') {
    if (Test-Path $capture) { throw "$capture exists; move it away first." }
    New-Item -ItemType Directory -Force (Split-Path $capture) | Out-Null
    & $pktmon filter remove | Out-Null
    & $pktmon filter add VEIL-DNS -p 53 | Out-Null
    & $pktmon start --capture --pkt-size 0 --file-name $capture | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'pktmon did not start.' }
    Write-Host "Capturing port 53 to $capture. Now run in a NORMAL-USER PowerShell:"
    Write-Host "  cd $Src; node tests\vm\internet-batch.mjs --run --internet-vm --desktop-dll <path to Veil.Desktop.dll>"
    Write-Host "Then here: $Src\tests\vm\guest-internet.ps1 -Stage Finish -Src $Src"
    return
}

& $pktmon stop | Out-Null
& $pktmon filter remove | Out-Null
if (-not (Test-Path $capture)) { throw 'No capture file; run -Stage Start first.' }
$run = Get-ChildItem (Join-Path $Src '.veil-dev') -Directory -Filter 'internet-vm-*' | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $run) { throw 'No internet-batch run found.' }
$ErrorActionPreference = 'Continue'
node (Join-Path $Src 'tests\vm\internet-batch.mjs') --analyze-capture $capture --summary (Join-Path $run.FullName 'summary.json')
Write-Host "Evidence: $($run.FullName)"
