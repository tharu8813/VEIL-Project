<#
SEC-004 write trace. Run ONLY inside the isolated VEIL VM (Procmon needs administrator rights,
VEIL itself refuses to run elevated). Two windows are used:

  1. Administrator PowerShell:  .\tests\security\trace-writes.ps1 -Action Start -IsolatedVm
  2. Normal-user PowerShell:    node tests/integration/desktop.mjs --profile-parent 'V:\VEIL-Lab' --crash-cycle
  3. Administrator PowerShell:  .\tests\security\trace-writes.ps1 -Action Stop -IsolatedVm

Stop exports the capture to CSV and runs tests/security/outside-writes.mjs, which follows the
process tree started by the desktop harness and fails on any write outside -Volume. The harness'
own evidence folder under .veil-dev is the only allowed exception. Traces stay in the guest.
#>
param(
    [Parameter(Mandatory)][ValidateSet('Start', 'Stop')][string]$Action,
    [switch]$IsolatedVm,
    [string]$Procmon = 'C:\Tools\Procmon\Procmon64.exe',
    [string]$Volume = 'V:\',
    # Further protected lab volumes of the same run (e.g. the disk-full volume); writes there count as inside.
    [string[]]$ExtraVolume = @(),
    [string]$RootCommand = 'tests/integration/desktop.mjs',
    [switch]$Phase34,
    [string]$OutDir = (Join-Path (Resolve-Path "$PSScriptRoot\..\..") '.veil-dev\write-trace')
)
$ErrorActionPreference = 'Stop'

if (-not $IsolatedVm) { throw 'Refusing to run: pass -IsolatedVm, and only inside the isolated VEIL VM.' }
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Procmon capture needs an elevated PowerShell in the VM. Run the VEIL harness in a separate non-elevated window.'
}
if ($Phase34) {
    . (Join-Path $PSScriptRoot '..\phase3\phase34-guest-identity.ps1')
    $guest = Get-VeilPhase34Guest
    if (-not $guest.virtualBox -or -not $guest.noDefaultRoute -or $Volume.TrimEnd('\') -eq $guest.systemDrive) {
        throw 'Use the prepared offline VirtualBox guest and its separate lab volume.'
    }
}
if (-not (Test-Path $Procmon)) { throw "Procmon not found at $Procmon" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$pml = Join-Path $OutDir 'trace.pml'
$csv = Join-Path $OutDir 'trace.csv'
$sessionFile = Join-Path $OutDir 'phase34-capture.json'

if ($Action -eq 'Start') {
    foreach ($extra in $ExtraVolume) {
        $extraStatus = Get-BitLockerVolume -MountPoint $extra.TrimEnd('\')
        if ($extraStatus.VolumeStatus -ne 'FullyEncrypted' -or $extraStatus.ProtectionStatus -ne 'On') { throw "$extra is not a fully encrypted, protected volume." }
    }
    if (-not (Test-Path $Volume)) { throw "Protected volume $Volume is not mounted/unlocked." }
    $status = Get-BitLockerVolume -MountPoint $Volume.TrimEnd('\')
    if ($status.VolumeStatus -ne 'FullyEncrypted' -or $status.ProtectionStatus -ne 'On') {
        throw "$Volume is not fully encrypted with protection on ($($status.VolumeStatus), $($status.ProtectionStatus))."
    }
    if ($Phase34) {
        if ((Test-Path -LiteralPath $pml) -or (Test-Path -LiteralPath $csv) -or (Test-Path -LiteralPath $sessionFile) -or
            (Get-Process -Name 'Procmon', 'Procmon64', 'Procmon64a' -ErrorAction SilentlyContinue)) {
            throw 'Use a new capture directory and close any existing Procmon session first.'
        }
    }
    else { Remove-Item -LiteralPath $pml, $csv -ErrorAction SilentlyContinue }
    Start-Process $Procmon -ArgumentList '/AcceptEula', '/Quiet', '/Minimized', '/BackingFile', "`"$pml`"" -WindowStyle Hidden
    & $Procmon /WaitForIdle
    if ($Phase34) {
        @{ uuid = $guest.uuid; boot = $guest.boot; volume = $Volume.TrimEnd('\') } | ConvertTo-Json |
            Set-Content -LiteralPath $sessionFile -Encoding UTF8
    }
    Write-Host "Capturing to $pml. Now run the harness in a NON-elevated window, then call -Action Stop."
    return
}

# Procmon is a GUI program: '&' returns before it finishes, so wait for each step explicitly.
if ($Phase34) {
    if (-not (Test-Path -LiteralPath $pml) -or (Test-Path -LiteralPath $csv)) {
        throw 'No active Phase 3/4 capture in the requested directory.'
    }
    $session = Get-Content -LiteralPath $sessionFile -Raw | ConvertFrom-Json
    if ($session.uuid -ne $guest.uuid -or $session.boot -ne $guest.boot -or $session.volume -ne $Volume.TrimEnd('\')) {
        throw 'The trace must use the original VM boot and lab volume.'
    }
}
Start-Process $Procmon -ArgumentList '/Terminate' -Wait -WindowStyle Hidden
Start-Process $Procmon -ArgumentList '/AcceptEula', '/Quiet', '/OpenLog', "`"$pml`"", '/SaveAs', "`"$csv`"" -Wait -WindowStyle Hidden
if (-not (Test-Path $csv) -or (Get-Item $csv).Length -eq 0) { throw "Procmon did not export $csv" }
$repo = Resolve-Path "$PSScriptRoot\..\.."
$report = Join-Path $OutDir 'outside-writes.json'
# Windows PowerShell 5.1 turns native stderr into terminating errors under 'Stop'.
$ErrorActionPreference = 'Continue'
$arguments = @($csv, '--root-command', $RootCommand, '--volume', $Volume, '--out', $report)
if ($Phase34) {
    $arguments = @($csv, '--root-command', 'tests/phase3/phase34-vm.mjs', '--volume', $Volume, '--out', $report,
        '--allow-node', (Join-Path $repo '.veil-dev'), '--require-activity', 'true')
}
else { $arguments += @('--allow', (Join-Path $repo '.veil-dev')) }
foreach ($extra in $ExtraVolume) { $arguments += @('--volume', $extra) }
node (Join-Path $repo 'tests\security\outside-writes.mjs') @arguments
$code = $LASTEXITCODE
Write-Host "Report: $report"
exit $code
