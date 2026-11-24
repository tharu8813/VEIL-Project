<#
Guest side of the single offline VM batch (docs/10-test-strategy.md, single VM batch section).
Runs ONLY inside the disposable offline VirtualBox test VM. Three commands in total:

  1. Elevated PowerShell (from the attached DVD):
       D:\BATCH.PS1 -Stage Prepare -StandardUser <normal account>
     Verifies the bundle, unpacks it to C:\VEIL-Batch, creates two disposable BitLocker VHDX volumes
     (V: 1 GiB lab, W: 256 MiB disk-full), writes the preflight receipt and starts the Procmon trace.
  2. Normal-user PowerShell: the command printed by step 1 (phase34-vm.mjs --run ...).
  3. Elevated PowerShell:
       C:\VEIL-Batch\BATCH.PS1 -Stage Finish
     Stops and analyses the trace, packs RESULTS.ZIP and prints the result table. On full PASS the lab
     volumes are locked, detached and deleted; on any FAIL they stay attached and unlocked for inspection.

The BitLocker passwords are random, kept only in memory and never written or printed: the volumes are
disposable. Nothing here runs on the host.
#>
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Finish')][string]$Stage,
    [string]$StandardUser,
    # Folder holding the bundle (the attached DVD). Windows PowerShell 5.1 leaves $PSScriptRoot empty in
    # parameter defaults, so the default is resolved below.
    [string]$Media,
    [string]$Work = 'C:\VEIL-Batch',
    [string]$Procmon = 'C:\Tools\Procmon\Procmon64.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $Media) { $Media = Split-Path -Parent $MyInvocation.MyCommand.Path }
# The lab VM keeps Procmon on the desktop; use it when the default location is empty.
$desktopProcmon = Join-Path $env:USERPROFILE 'Desktop\ProcessMonitor\Procmon64.exe'
if (-not (Test-Path $Procmon) -and (Test-Path $desktopProcmon)) { $Procmon = $desktopProcmon }
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this stage from an elevated PowerShell in the VM.' }
$src = Join-Path $Work 'src'
$images = Join-Path $Work 'images'
$state = Join-Path $Work 'batch-state.json'
$trace = Join-Path $src '.veil-dev\phase34-trace'

function Assert-OfflineGuest {
    . (Join-Path $src 'tests\phase3\phase34-guest-identity.ps1')
    $guest = Get-VeilPhase34Guest
    if (-not $guest.virtualBox -or -not $guest.noDefaultRoute) { throw 'Use the offline VirtualBox test VM (no default route).' }
    $guest
}

function New-LabVolume([string]$Name, [int]$SizeMb, [char]$Letter, [string]$Sid) {
    $image = Join-Path $images "$Name.vhdx"
    if ((Test-Path $image) -or (Get-Partition -DriveLetter $Letter -ErrorAction SilentlyContinue)) { throw "Expected a fresh $Name image and free drive $($Letter):." }
    $script = Join-Path $images "$Name.diskpart"
    'create vdisk file="{0}" maximum={1} type=expandable' -f $image, $SizeMb | Set-Content -Encoding ASCII $script
    diskpart /s $script | Out-Null
    Remove-Item $script
    $disk = @(Mount-DiskImage -ImagePath $image -PassThru | Get-Disk)
    if ($disk.Count -ne 1 -or $disk[0].IsBoot -or $disk[0].IsSystem -or $disk[0].PartitionStyle -ne 'RAW') { throw "Unexpected disk for $Name." }
    Initialize-Disk -InputObject $disk[0] -PartitionStyle GPT
    New-Partition -DiskNumber $disk[0].Number -UseMaximumSize -DriveLetter $Letter |
        Format-Volume -FileSystem NTFS -NewFileSystemLabel "VEIL_$($Name.ToUpper())" -Confirm:$false | Out-Null
    $secret = ConvertTo-SecureString ([Guid]::NewGuid().ToString('N')) -AsPlainText -Force
    Enable-BitLocker -MountPoint "$($Letter):" -EncryptionMethod XtsAes256 -PasswordProtector -Password $secret | Out-Null
    for ($n = 0; $n -lt 300; $n++) {
        $volume = Get-BitLockerVolume -MountPoint "$($Letter):"
        if ($volume.VolumeStatus -eq 'FullyEncrypted' -and $volume.ProtectionStatus -eq 'On') { break }
        Start-Sleep -Seconds 2
    }
    if ($volume.VolumeStatus -ne 'FullyEncrypted' -or $volume.ProtectionStatus -ne 'On') { throw "$Name did not finish encrypting." }
    $parent = "$($Letter):\VEIL-Lab"
    New-Item -ItemType Directory $parent | Out-Null
    icacls $parent /grant ('*{0}:(OI)(CI)(M)' -f $Sid) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Lab ACL failed for $parent." }
    @{ image = $image; parent = $parent; drive = "$($Letter):" }
}

if ($Stage -eq 'Prepare') {
    if (-not $StandardUser) { throw 'Pass -StandardUser <the normal (non-admin) account that runs step 2>.' }
    $sid = ([Security.Principal.NTAccount]$StandardUser).Translate([Security.Principal.SecurityIdentifier]).Value
    if (Test-Path $Work) { throw "$Work already exists. Restore the VM snapshot or remove it first." }

    # 1. Bundle integrity: every file listed in the manifest, by SHA-256.
    $manifest = Get-Content (Join-Path $Media 'MANIFEST.JSON') -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.files.PSObject.Properties) {
        $actual = (Get-FileHash (Join-Path $Media $entry.Name) -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $entry.Value) { throw "Bundle file $($entry.Name) does not match the manifest." }
    }
    New-Item -ItemType Directory $Work, $images | Out-Null
    Expand-Archive (Join-Path $Media 'SOURCE.ZIP') $src
    Expand-Archive (Join-Path $Media 'DESKTOP.ZIP') (Join-Path $Work 'desktop')
    Copy-Item (Join-Path $Media 'BATCH.PS1'), (Join-Path $Media 'MANIFEST.JSON') $Work
    # Unsigned Release payloads for tests\vm\guest-package.ps1 (signed in the VM with a disposable certificate).
    Copy-Item (Join-Path $Media 'PAYLOAD-*.ZIP') $Work
    # Loopback TLS test certificate made on the host: the offline guest has no openssl.
    Expand-Archive (Join-Path $Media 'TESTCERT.ZIP') (Join-Path $Work 'testcert')
    icacls $src /grant ('*{0}:(OI)(CI)(M)' -f $sid) | Out-Null
    icacls (Join-Path $Work 'testcert') /grant ('*{0}:(OI)(CI)(RX)' -f $sid) | Out-Null
    $guest = Assert-OfflineGuest

    # 2. Disposable protected volumes.
    $lab = New-LabVolume 'lab' 1024 'V' $sid
    $small = New-LabVolume 'full' 256 'W' $sid
    @{ commit = $manifest.commit; boot = $guest.boot; volumes = @($lab, $small); standardUserSid = $sid } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $state

    # 3. Preflight receipt and write trace.
    Push-Location $src
    try {
        $receipt = Join-Path $src '.veil-dev\phase34-preflight.json'
        & (Join-Path $src 'tests\phase3\phase34-vm-preflight.ps1') -IsolatedVm -ImagePath $lab.image -ProfileParent $lab.parent `
            -DiskFullImage $small.image -DiskFullParent $small.parent -Out $receipt
        icacls (Join-Path $src '.veil-dev') /grant ('*{0}:(OI)(CI)(M)' -f $sid) | Out-Null
        if (Test-Path $Procmon) {
            & (Join-Path $src 'tests\security\trace-writes.ps1') -Action Start -IsolatedVm -Phase34 -Procmon $Procmon -Volume 'V:\' -ExtraVolume 'W:\' -OutDir $trace
        }
        else { Write-Warning "Procmon not found at ${Procmon}: physical-writes stays NOT RUN." }
    }
    finally { Pop-Location }
    $desktop = Join-Path $Work 'desktop\Veil.Desktop.dll'
    Write-Host ''
    Write-Host "READY. In a NORMAL-USER PowerShell signed in as $StandardUser run:"
    Write-Host "  cd $src; node tests\phase3\phase34-vm.mjs --run --isolated-vm --preflight .veil-dev\phase34-preflight.json --desktop-dll $desktop --cert-dir $Work\testcert"
    Write-Host "Then here (elevated): $Work\BATCH.PS1 -Stage Finish"
    Write-Host "After Finish (the write trace is stopped), the package tests here (docs/10-test-strategy.md):"
    Write-Host "  cd $src; .\tests\vm\guest-package.ps1 -Stage Sign   (then Install, the operator checks, Update, Remove)"
    return
}

# Finish
$batch = Get-Content $state -Raw | ConvertFrom-Json
$guest = Assert-OfflineGuest
if ($guest.boot -ne $batch.boot) { throw 'The VM rebooted since Prepare; the trace and receipt are no longer valid.' }
$traceCode = $null
if (Test-Path (Join-Path $trace 'trace.pml')) {
    $ErrorActionPreference = 'Continue'
    & (Join-Path $src 'tests\security\trace-writes.ps1') -Action Stop -IsolatedVm -Phase34 -Procmon $Procmon -Volume 'V:\' -ExtraVolume 'W:\' -OutDir $trace
    $traceCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
}
$run = Get-ChildItem (Join-Path $src '.veil-dev') -Directory -Filter 'phase34-vm-*' | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $run) { throw 'No phase34-vm run found. Run step 2 first.' }
$summary = Get-Content (Join-Path $run.FullName 'summary.json') -Raw | ConvertFrom-Json
$results = Join-Path $Work 'results'
New-Item -ItemType Directory -Force $results | Out-Null
Copy-Item (Join-Path $run.FullName '*') $results
foreach ($name in 'outside-writes.json', 'phase34-capture.json') {
    if (Test-Path (Join-Path $trace $name)) { Copy-Item (Join-Path $trace $name) $results }
}
$physical = if ($null -eq $traceCode) { 'NOT RUN' } elseif ($traceCode -eq 0) { 'PASS' } else { 'FAIL' }
@{ commit = $batch.commit; physicalWrites = $physical; traceExitCode = $traceCode } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $results 'batch.json')
$zip = Join-Path $Work 'RESULTS.ZIP'
Compress-Archive -Path (Join-Path $results '*') -DestinationPath $zip -Force

Write-Host "commit $($batch.commit)"
foreach ($test in $summary.tests) {
    $status = if ($test.id -eq 'physical-writes') { $physical } else { $test.status }
    Write-Host ("{0,-8} {1}" -f $status, $test.id)
}
$failed = @($summary.tests | Where-Object status -eq 'FAIL').Count -gt 0 -or $physical -eq 'FAIL'
if ($failed) {
    Write-Warning 'FAIL present: lab volumes stay attached and unlocked for inspection. Evidence is kept.'
}
else {
    foreach ($volume in $batch.volumes) {
        Lock-BitLocker -MountPoint $volume.drive -ForceDismount | Out-Null
        Dismount-DiskImage -ImagePath $volume.image | Out-Null
        Remove-Item $volume.image
    }
    Write-Host 'Lab volumes locked, detached and deleted.'
}
Write-Host "Results: $zip"
