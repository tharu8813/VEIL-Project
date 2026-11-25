<#
Offline VM package test (ADR-0010 E1, E2, B3). Runs ONLY inside the disposable offline VirtualBox test VM,
from an elevated Windows PowerShell 5.1, after BATCH.PS1 -Stage Finish (so the write trace of the lab volumes
is already stopped and this test's own install volume X: is not mistaken for a leak).

  Sign       creates a disposable code-signing certificate (trusted only in this VM), signs the prebuilt
             alpha.1/alpha.2/alpha.3 payloads, writes their manifests with the production function, runs the
             tamper matrix against Read-VeilPackage, and creates a separate install volume (X:) whose
             BitLocker passphrase the operator types (it is never stored or printed).
  Install    OPERATOR STEP (interactive): installs alpha.1 with package-maintenance.ps1. The production
             installer asks for the volume passphrase, shows the new recovery password and asks for it again
             (the recovery drill). Then the operator runs the installed browser as the normal user (A1, A3).
  Update     update to alpha.2, refuse same/lower versions, interrupt alpha.3 before and after commit and
             recover with -RecoverInterruptedUpdate, check that registration and the VHDX are preserved.
  Remove     removes the service and binaries and checks that the VHDX and registration remain.

  Record     -Id <check> -Status PASS|FAIL|"NOT RUN" -Detail <text>: stores an operator check result.

Every stage appends to C:\VEIL-Batch\package-results.json; BATCH.PS1 -Stage Finish adds it to RESULTS.ZIP.
Nothing here runs on the host. Restore the VM snapshot afterwards: the test certificate stays trusted in it.
#>
param(
    [Parameter(Mandatory)][ValidateSet('Sign', 'Install', 'Update', 'Remove', 'Record')][string]$Stage,
    [string]$Work = 'C:\VEIL-Batch',
    # -Stage Record: the operator's result for a manual check (installed-unlock, installed-relock, ...).
    [string]$Id,
    [ValidateSet('PASS', 'FAIL', 'NOT RUN')][string]$Status,
    [string]$Detail = ''
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run from an elevated PowerShell in the VM.' }
$src = Join-Path $Work 'src'
. (Join-Path $src 'tests\phase3\phase34-guest-identity.ps1')
$guest = Get-VeilPhase34Guest
if (-not $guest.virtualBox -or -not $guest.noDefaultRoute) { throw 'Use the offline VirtualBox test VM (no default route).' }
. (Join-Path $src 'scripts\package-common.ps1')
. (Join-Path $src 'tests\vm\package-evidence.ps1')
. (Join-Path $src 'tests\vm\package-checkpoints.ps1')
$maintenance = Join-Path $src 'scripts\package-maintenance.ps1'
$packages = 'C:\VEIL-Packages'
$stateFile = Join-Path $Work 'package-state.json'
$resultsFile = Join-Path $Work 'package-results.json'
$versions = @('1.0.0-alpha.1', '1.0.0-alpha.2', '1.0.0-alpha.3')
$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$programData = [Environment]::GetFolderPath('CommonApplicationData')
$bin = Join-Path $programFiles 'VEIL'
$data = Join-Path $programData 'VEIL'

function Read-Results {
    if (Test-Path -LiteralPath $resultsFile) { Get-Content -LiteralPath $resultsFile -Raw | ConvertFrom-Json }
}
function Save-Result([string]$Id, [string]$Status, [string]$Detail, [string]$Source = 'automatic') {
    $all = @(Read-Results)
    Assert-VeilPackageUnrecorded $all @($Id)
    $all += [pscustomobject]@{ id = $Id; status = $Status; source = $Source; detail = $Detail; at = (Get-Date).ToString('o') }
    $next = $resultsFile + '.next'
    if (Test-Path -LiteralPath $next) { throw 'Unresolved package evidence write. Preserve it for review.' }
    ConvertTo-Json -InputObject @($all) -Depth 4 | Set-Content -LiteralPath $next -Encoding UTF8
    if (Test-Path -LiteralPath $resultsFile) { [IO.File]::Replace($next, $resultsFile, $null) }
    else { [IO.File]::Move($next, $resultsFile) }
    Write-Host ('{0,-8} {1}  {2}' -f $Status, $Id, $Detail)
}
function Get-State { Get-Content $stateFile -Raw | ConvertFrom-Json }
function Package-Path([string]$Version) { Join-Path $packages $Version }
function Installed-Version { (Get-Content (Join-Path $bin 'manifest.json') -Raw | ConvertFrom-Json).version }
function Invoke-Maintenance([string[]]$Arguments) {
    # A child process, so a refusal is an exit code and the mutex is released exactly as in real use.
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $maintenance @Arguments 2>&1
    [pscustomobject]@{ code = $LASTEXITCODE; text = ($output | Out-String).Trim() }
}
function Registration-Fingerprint {
    # Everything an update must keep, except the client pin it is allowed to change.
    $broker = Get-Content (Join-Path $data 'broker.json') -Raw | ConvertFrom-Json
    $broker.PSObject.Properties.Remove('clientSha256')
    $client = (Get-FileHash (Join-Path $data 'client.json') -Algorithm SHA256).Hash
    ($broker | ConvertTo-Json -Compress) + '|' + $client
}

if ($Stage -eq 'Sign') {
    if ((Test-Path $packages) -or (Test-Path $stateFile) -or (Test-Path $resultsFile) -or (Test-Path ($resultsFile + '.next'))) {
        throw 'Existing package trial or evidence. Restore the VM snapshot first.'
    }
    $commit = (Get-Content (Join-Path $Work 'MANIFEST.JSON') -Raw | ConvertFrom-Json).commit
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=VEIL VM disposable test signing' `
        -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddDays(3)
    $public = Join-Path $Work 'vm-test-signing.cer'
    Export-Certificate -Cert $cert -FilePath $public | Out-Null
    foreach ($store in 'Root', 'TrustedPublisher') { Import-Certificate -FilePath $public -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null }
    New-VeilRestrictedDirectory $packages
    foreach ($version in $versions) {
        $root = Package-Path $version
        New-VeilRestrictedDirectory $root
        Expand-Archive (Join-Path $Work "PAYLOAD-$version.ZIP") $root
        foreach ($image in 'Desktop\Veil.Desktop.exe', 'Desktop\veil-vault.exe', 'Broker\Veil.VolumeBroker.exe') {
            $signed = Set-AuthenticodeSignature -FilePath (Join-Path $root $image) -Certificate $cert -HashAlgorithm SHA256
            if ($signed.Status -ne 'Valid') { throw "Signing $image failed: $($signed.StatusMessage)" }
        }
        Write-VeilPackageManifest $root $version $commit $cert
    }
    Save-Result 'package-sign' 'PASS' "3 packages signed (no timestamp: offline VM) by $($cert.Thumbprint)"

    # Re-sign malformed synthetic manifests with the trusted disposable VM certificate.
    function Edit-SignedTestManifest([string]$Root, [scriptblock]$Change) {
        $path = Join-Path $Root 'manifest.json'
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        & $Change $manifest
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 8))
        [IO.File]::WriteAllBytes($path, $bytes)
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes), $true)
        $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
        $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
        $cms.ComputeSignature($signer)
        [IO.File]::WriteAllBytes((Join-Path $Root 'manifest.p7s'), $cms.Encode())
    }

    # E2 tamper matrix: each case is a restricted copy of alpha.1 with one change.
    $cases = [ordered]@{
        'valid'            = @{ change = {}; expect = $null }
        'modified-file'    = @{ change = { param($r) $f = Join-Path $r 'Desktop\Veil.Desktop.dll'; $b = [IO.File]::ReadAllBytes($f); $b[$b.Length - 1] = $b[$b.Length - 1] -bxor 1; [IO.File]::WriteAllBytes($f, $b) }; expect = 'hash or length' }
        'extra-file'       = @{ change = { param($r) [IO.File]::WriteAllText((Join-Path $r 'Desktop\extra.dll'), 'x') }; expect = 'Unexpected package file' }
        'missing-file'     = @{ change = { param($r) Remove-Item (Join-Path $r 'Broker\Veil.VolumeBroker.dll') }; expect = 'missing' }
        'edited-manifest'  = @{ change = { param($r) $m = Join-Path $r 'manifest.json'; [IO.File]::WriteAllText($m, ([IO.File]::ReadAllText($m) -replace 'alpha\.1', 'alpha.9')) }; expect = 'hash|signature|Cryptographic' }
        'wrong-signer'     = @{ change = {}; thumbprint = ('0' * 40); expect = 'signer mismatch' }
        'signed-duplicate-path' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files = @($m.files) + @($m.files[0]) } }; expect = 'Invalid or duplicate manifest entry' }
        'signed-unsafe-path' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files[0].path = 'Desktop/../outside.dll' } }; expect = 'Unsafe package path' }
        'signed-missing-required' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files = @($m.files | Where-Object path -ne 'Desktop/veil-vault.exe') } }; expect = 'Required binary missing' }
        'signed-boolean-length' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files[0].bytes = $true } }; expect = 'Invalid or duplicate manifest entry' }
        'signed-fractional-length' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files[0].bytes = 0.5 } }; expect = 'Invalid or duplicate manifest entry' }
        'signed-bad-hash' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files[0].sha256 = 'not-a-hash' } }; expect = 'Invalid or duplicate manifest entry' }
        'signed-profile-format' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.profileFormat = 2 } }; expect = 'Unsupported package contract' }
        # An unsigned image with a correctly re-signed manifest: only the Authenticode check can catch it.
        'unsigned-binary'  = @{ change = {
            param($r)
            $raw = Join-Path $Work 'unsigned-check'
            if (-not (Test-Path $raw)) { Expand-Archive (Join-Path $Work "PAYLOAD-$($versions[0]).ZIP") $raw }
            [IO.File]::Copy((Join-Path $raw 'Desktop\veil-vault.exe'), (Join-Path $r 'Desktop\veil-vault.exe'), $true)
            Remove-Item (Join-Path $r 'manifest.json'), (Join-Path $r 'manifest.p7s')
            Write-VeilPackageManifest $r $versions[0] $commit $cert
        }; expect = 'signature mismatch' }
    }
    foreach ($name in $cases.Keys) {
        $case = $cases[$name]
        $copy = Join-Path $packages "tamper-$name"
        New-VeilRestrictedDirectory $copy
        Get-ChildItem (Package-Path $versions[0]) -Recurse -File | ForEach-Object {
            $target = Join-Path $copy $_.FullName.Substring((Package-Path $versions[0]).Length + 1)
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
            [IO.File]::Copy($_.FullName, $target, $false)
        }
        & $case.change $copy
        $thumbprint = if ($case.thumbprint) { $case.thumbprint } else { $cert.Thumbprint }
        $message = $null
        try { [void](Read-VeilPackage $copy $thumbprint) } catch { $message = $_.Exception.Message }
        $ok = if ($null -eq $case.expect) { $null -eq $message } else { $null -ne $message -and $message -match $case.expect }
        Save-Result "package-tamper-$name" ($(if ($ok) { 'PASS' } else { 'FAIL' })) ($(if ($message) { $message } else { 'accepted' }))
        Remove-Item $copy -Recurse -Force
    }

    Assert-VeilPackageEvidence @(Read-Results) $script:VeilPackageSignChecks

    # Separate install volume: the operator chooses its passphrase (typed again by the installer).
    $images = Join-Path $Work 'images'
    $image = Join-Path $images 'install.vhdx'
    if (Get-Partition -DriveLetter X -ErrorAction SilentlyContinue) { throw 'Drive X: must be free.' }
    $script = Join-Path $images 'install.diskpart'
    'create vdisk file="{0}" maximum=1024 type=expandable' -f $image | Set-Content -Encoding ASCII $script
    diskpart /s $script | Out-Null
    Remove-Item $script
    $disk = @(Mount-DiskImage -ImagePath $image -PassThru | Get-Disk)
    if ($disk.Count -ne 1 -or $disk[0].IsBoot -or $disk[0].IsSystem -or $disk[0].PartitionStyle -ne 'RAW') { throw 'Unexpected install disk.' }
    Initialize-Disk -InputObject $disk[0] -PartitionStyle GPT
    New-Partition -DiskNumber $disk[0].Number -UseMaximumSize -DriveLetter X | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'VEIL_INSTALL' -Confirm:$false | Out-Null
    $secret = Read-Host 'Choose a passphrase for the disposable install volume X: (you will type it again in the installer)' -AsSecureString
    Enable-BitLocker -MountPoint 'X:' -EncryptionMethod XtsAes256 -PasswordProtector -Password $secret | Out-Null
    $secret.Dispose()
    for ($n = 0; $n -lt 300 -and (Get-BitLockerVolume -MountPoint 'X:').VolumeStatus -ne 'FullyEncrypted'; $n++) { Start-Sleep -Seconds 2 }
    $volume = Get-BitLockerVolume -MountPoint 'X:'
    if ($volume.VolumeStatus -ne 'FullyEncrypted' -or $volume.ProtectionStatus -ne 'On') { throw 'Install volume did not finish encrypting.' }
    $protector = @($volume.KeyProtector | Where-Object KeyProtectorType -eq 'Password')
    if ($protector.Count -ne 1) { throw 'Expected one passphrase protector.' }
    $volumeGuid = ((Get-Volume -DriveLetter X).UniqueId -replace '^\\\\\?\\Volume\{|\}\\$', '')
    $sid = (Get-Content (Join-Path $Work 'batch-state.json') -Raw | ConvertFrom-Json).standardUserSid
    New-Item -ItemType Directory 'X:\VEIL-Profile' | Out-Null
    icacls 'X:\VEIL-Profile' /grant ('*{0}:(OI)(CI)(M)' -f $sid) | Out-Null
    Lock-BitLocker -MountPoint 'X:' -ForceDismount | Out-Null
    Dismount-DiskImage -ImagePath $image | Out-Null
    @{ thumbprint = $cert.Thumbprint; image = $image; profile = 'X:\VEIL-Profile'; volumeGuid = $volumeGuid
       protectorId = $protector[0].KeyProtectorId.Trim('{}'); userSid = $sid } | ConvertTo-Json | Set-Content -Encoding UTF8 $stateFile
    Write-Host ''
    Write-Host 'NEXT (operator, elevated): .\tests\vm\guest-package.ps1 -Stage Install'
    return
}

if ($Stage -eq 'Record') {
    if ($Id -cnotin $script:VeilPackageManualChecks -or -not $Status) {
        throw 'Record accepts only installed-unlock, installed-relock or installed-refuses.'
    }
    Assert-VeilPackageEvidence @(Read-Results) ($script:VeilPackageSignChecks + @('package-install'))
    Save-Result $Id $Status $Detail 'operator'
    return
}

$state = Get-State
if ($Stage -eq 'Install') {
    Assert-VeilPackageEvidence @(Read-Results) $script:VeilPackageSignChecks
    Assert-VeilPackageUnrecorded @(Read-Results) @('package-install')
    Write-Host 'The installer asks for the X: passphrase, shows a NEW recovery password (write it down outside the VM),'
    Write-Host 'then asks for that recovery password again. This is the B3 recovery drill.'
    & $maintenance -Install -Package (Package-Path $versions[0]) -SignerThumbprint $state.thumbprint -ImagePath $state.image `
        -ProfilePath $state.profile -VolumeGuid $state.volumeGuid -ProtectorId $state.protectorId -UserSid $state.userSid
    $service = Get-Service Veil.VolumeBroker -ErrorAction SilentlyContinue
    $ok = $service.Status -eq 'Running' -and (Installed-Version) -eq $versions[0] -and
        (Get-Content (Join-Path $data 'broker.json') -Raw | ConvertFrom-Json).recoveryProtectorId -match '^\{[0-9a-f-]{36}\}$'
    Save-Result 'package-install' ($(if ($ok) { 'PASS' } else { 'FAIL' })) "installed $(Installed-Version), service $($service.Status), recovery protector registered"
    Write-Host ''
    Write-Host 'OPERATOR CHECKS, then record each with -Stage Record -Id <id> -Status PASS|FAIL -Detail <what you saw>:'
    Write-Host "  installed-unlock   As the normal user start '$bin\Desktop\Veil.Desktop.exe', unlock with the X: passphrase, open a page."
    Write-Host '  installed-relock   Close VEIL normally; Get-DiskImage for the install VHDX shows Attached=False.'
    Write-Host '  installed-refuses  Rename C:\ProgramData\VEIL\client.json, start VEIL: it must refuse (no plaintext profile). Rename it back.'
    return
}

if ($Stage -eq 'Update') {
    Assert-VeilPackageEvidence @(Read-Results) ($script:VeilPackageSignChecks + $script:VeilPackageInstallChecks)
    Assert-VeilPackageUnrecorded @(Read-Results) $script:VeilPackageUpdateChecks
    $before = Registration-Fingerprint
    $vhdx = (Get-FileHash $state.image -Algorithm SHA256).Hash
    $result = Invoke-Maintenance @('-Update', '-Package', (Package-Path $versions[1]), '-SignerThumbprint', $state.thumbprint)
    Save-Result 'package-update' ($(if ($result.code -eq 0 -and (Installed-Version) -eq $versions[1]) { 'PASS' } else { 'FAIL' })) "alpha.1 -> $(Installed-Version) (exit $($result.code))"
    Assert-VeilPackageEvidence @(Read-Results) @('package-update')
    foreach ($case in @(@{ id = 'package-same-version'; v = $versions[1] }, @{ id = 'package-lower-version'; v = $versions[0] })) {
        $result = Invoke-Maintenance @('-Update', '-Package', (Package-Path $case.v), '-SignerThumbprint', $state.thumbprint)
        $ok = $result.code -ne 0 -and $result.text -match 'Downgrade, same version' -and (Installed-Version) -eq $versions[1] -and
            -not (Test-Path (Join-Path $programFiles 'VEIL.package-staging'))
        Save-Result $case.id ($(if ($ok) { 'PASS' } else { 'FAIL' })) "refused=$($result.code -ne 0), installed $(Installed-Version)"
    }
    Save-Result 'package-registration-kept' ($(if ((Registration-Fingerprint) -eq $before -and (Get-FileHash $state.image -Algorithm SHA256).Hash -eq $vhdx) { 'PASS' } else { 'FAIL' })) 'registration (except client pin), client.json and VHDX unchanged'

    Assert-VeilPackageEvidence @(Read-Results) @('package-same-version', 'package-lower-version', 'package-registration-kept')

    # Test-only copies of the trusted source pause at exact transaction boundaries.
    # Recovery always uses the original, unmodified maintenance script.
    $previous = Join-Path $programFiles 'VEIL.update-previous'
    $staging = Join-Path $programFiles 'VEIL.package-staging'
    $failed = Join-Path $programFiles 'VEIL.update-failed'
    $journal = Join-Path $data 'update-transaction'
    $committed = Join-Path $journal 'committed'
    $backup = Join-Path $journal 'broker.previous.json'
    $client = Join-Path $bin 'Desktop\Veil.Desktop.exe'
    $source = [IO.File]::ReadAllText($maintenance)
    $oldPin = (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash
    $newPin = (Get-FileHash -LiteralPath (Join-Path (Package-Path $versions[2]) 'Desktop\Veil.Desktop.exe') -Algorithm SHA256).Hash
    foreach ($kind in $script:VeilUpdateCheckpoints.Keys) {
        if ((Installed-Version) -ne $versions[1]) { throw 'Unexpected starting package version.' }
        foreach ($artifact in @($previous,$staging,$failed,$journal)) {
            if (Test-Path -LiteralPath $artifact) { throw 'Unresolved maintenance state before test.' }
        }
        $testRoot = Join-Path $packages "checkpoint-$kind"
        New-VeilRestrictedDirectory $testRoot
        $signal = Join-Path $testRoot 'ready'
        $instrumented = Join-Path $testRoot 'package-maintenance.ps1'
        [IO.File]::Copy((Join-Path $src 'scripts\package-common.ps1'), (Join-Path $testRoot 'package-common.ps1'), $false)
        [IO.File]::WriteAllText($instrumented, (New-VeilCheckpointSource $source $kind $signal), [Text.UTF8Encoding]::new($false))
        Assert-VeilAdminPath $instrumented
        Assert-VeilAdminPath (Join-Path $testRoot 'package-common.ps1')
        $child = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            ('"' + $instrumented + '"'), '-Update', '-Package', ('"' + (Package-Path $versions[2]) + '"'), '-SignerThumbprint', $state.thumbprint)
        $stopAt = (Get-Date).AddMinutes(5)
        $interrupted = $false
        try {
            while (-not $child.HasExited -and (Get-Date) -lt $stopAt) {
                if ((Test-Path -LiteralPath $signal) -and
                    ([IO.File]::ReadAllText($signal) -ceq $kind)) {
                    Stop-Process -Id $child.Id -Force
                    $interrupted = $true
                    break
                }
                Start-Sleep -Milliseconds 100
            }
        } finally {
            if (-not $child.HasExited) { Stop-Process -Id $child.Id -Force }
            if (-not $child.WaitForExit(15000)) { throw 'Test process exit not observed.' }
        }
        if (-not $interrupted) { throw "Checkpoint $kind not reached. Preserve evidence; no interruption PASS recorded." }
        if (-not (Test-Path -LiteralPath $backup)) { throw 'Durable registration backup missing.' }
        $pin = (Get-Content -LiteralPath (Join-Path $data 'broker.json') -Raw | ConvertFrom-Json).clientSha256
        Assert-VeilUpdateBoundary $kind (Test-Path -LiteralPath $previous) (Test-Path -LiteralPath $bin) `
            (Test-Path -LiteralPath $staging) (Test-Path -LiteralPath $committed) $pin $oldPin $newPin
        $recover = Invoke-Maintenance @('-RecoverInterruptedUpdate', '-SignerThumbprint', $state.thumbprint)
        $expected = if ($kind -eq 'post-commit') { $versions[2] } else { $versions[1] }
        $expectedPin = if ($kind -eq 'post-commit') { $newPin } else { $oldPin }
        $installed = Read-VeilPackage $bin $state.thumbprint
        $service = Get-CimInstance Win32_Service -Filter "Name='Veil.VolumeBroker'"
        $clean = @(@($journal,$previous,$staging,$failed) | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0
        $pin = (Get-Content -LiteralPath (Join-Path $data 'broker.json') -Raw | ConvertFrom-Json).clientSha256
        $ok = $recover.code -eq 0 -and $installed.version -eq $expected -and $service.State -eq 'Running' -and
            $service.StartMode -eq 'Auto' -and $clean -and $pin -eq $expectedPin -and
            (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash -eq $expectedPin -and
            (Registration-Fingerprint) -eq $before -and (Get-FileHash -LiteralPath $state.image -Algorithm SHA256).Hash -eq $vhdx -and
            -not (Get-DiskImage -ImagePath $state.image).Attached
        Save-Result "package-interrupt-$kind" ($(if ($ok) { 'PASS' } else { 'FAIL' })) "authenticated $($installed.version), expected $expected; service $($service.State); journal cleared=$clean; exact pin and VHDX checked"
        Assert-VeilPackageEvidence @(Read-Results) @("package-interrupt-$kind")
    }
    return
}

if ($Stage -eq 'Remove') {
    Assert-VeilPackageUnrecorded @(Read-Results) @('package-remove')
    $broker = Get-Content (Join-Path $data 'broker.json') -Raw
    $result = Invoke-Maintenance @('-Remove', '-SignerThumbprint', $state.thumbprint)
    $ok = $result.code -eq 0 -and -not (Test-Path $bin) -and -not (Get-Service Veil.VolumeBroker -ErrorAction SilentlyContinue) -and
        (Get-Content (Join-Path $data 'broker.json') -Raw) -eq $broker -and (Test-Path (Join-Path $data 'client.json')) -and (Test-Path $state.image)
    Save-Result 'package-remove' ($(if ($ok) { 'PASS' } else { 'FAIL' })) 'service and binaries removed; registration, recovery protector ID and VHDX kept'
    $zip = Join-Path $Work 'PACKAGE-RESULTS.ZIP'
    $summary = Join-Path $Work 'package-summary.json'
    $gaps = @(Get-VeilPackageEvidenceGaps @(Read-Results) $script:VeilPackageRequiredChecks)
    $outcome = if ($gaps.Count) { 'INCOMPLETE' } else { 'PASS' }
    [pscustomobject]@{ status = $outcome; required = $script:VeilPackageRequiredChecks; gaps = $gaps;
        scope = 'Disposable VM package trial; manual checks are operator observations, not release approval' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summary -Encoding UTF8
    if (Test-Path -LiteralPath $zip) { throw 'Package evidence archive already exists.' }
    Compress-Archive -Path @($resultsFile, $summary) -DestinationPath $zip
    Write-Host "Package results: $zip"
    Assert-VeilPackageEvidence @(Read-Results) $script:VeilPackageRequiredChecks
}
