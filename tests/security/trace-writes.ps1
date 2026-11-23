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
    [string]$RootCommand = 'tests/integration/desktop.mjs',
    [string]$OutDir = (Join-Path (Resolve-Path "$PSScriptRoot\..\..") '.veil-dev\write-trace')
)
$ErrorActionPreference = 'Stop'

if (-not $IsolatedVm) { throw 'Refusing to run: pass -IsolatedVm, and only inside the isolated VEIL VM.' }
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Procmon capture needs an elevated PowerShell in the VM. Run the VEIL harness in a separate non-elevated window.'
}
if (-not (Test-Path $Procmon)) { throw "Procmon not found at $Procmon" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$pml = Join-Path $OutDir 'trace.pml'
$csv = Join-Path $OutDir 'trace.csv'

if ($Action -eq 'Start') {
    if (-not (Test-Path $Volume)) { throw "Protected volume $Volume is not mounted/unlocked." }
    $status = Get-BitLockerVolume -MountPoint $Volume.TrimEnd('\')
    if ($status.VolumeStatus -ne 'FullyEncrypted' -or $status.ProtectionStatus -ne 'On') {
        throw "$Volume is not fully encrypted with protection on ($($status.VolumeStatus), $($status.ProtectionStatus))."
    }
    Remove-Item -LiteralPath $pml, $csv -ErrorAction SilentlyContinue
    Start-Process $Procmon -ArgumentList '/AcceptEula', '/Quiet', '/Minimized', '/BackingFile', "`"$pml`""
    & $Procmon /WaitForIdle
    Write-Host "Capturing to $pml. Now run the harness in a NON-elevated window, then call -Action Stop."
    return
}

# Procmon is a GUI program: '&' returns before it finishes, so wait for each step explicitly.
Start-Process $Procmon -ArgumentList '/Terminate' -Wait
Start-Process $Procmon -ArgumentList '/AcceptEula', '/Quiet', '/OpenLog', "`"$pml`"", '/SaveAs', "`"$csv`"" -Wait
if (-not (Test-Path $csv) -or (Get-Item $csv).Length -eq 0) { throw "Procmon did not export $csv" }
$repo = Resolve-Path "$PSScriptRoot\..\.."
$report = Join-Path $OutDir 'outside-writes.json'
# Windows PowerShell 5.1 turns native stderr into terminating errors under 'Stop'.
$ErrorActionPreference = 'Continue'
node (Join-Path $repo 'tests\security\outside-writes.mjs') $csv --root-command $RootCommand --volume $Volume `
    --allow (Join-Path $repo '.veil-dev') --out $report
$code = $LASTEXITCODE
Write-Host "Report: $report"
exit $code
