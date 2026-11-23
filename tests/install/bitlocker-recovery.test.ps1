$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\bitlocker-recovery.ps1')

$image = 'C:\Program Files\Synthetic Test\test.vhdx'
$volumePath = '\\?\Volume{11111111-1111-1111-1111-111111111111}\'
$primary = [guid]'22222222-2222-2222-2222-222222222222'
$recovery = [guid]'33333333-3333-3333-3333-333333333333'
$normalPassword = 'synthetic-passphrase'
$recoveryPassword = '111111-111111-111111-111111-111111-111111-111111-111111'
$otherPassword = '222222-222222-222222-222222-222222-222222-222222-222222'

function Reset-FakeRecovery {
    $script:attached = $false
    $script:locked = $true
    $script:recoveryPresent = $false
    $script:extraPassphrase = $false
    $script:failLock = $false
    $script:reportOtherVolume = $false
    $script:prompts = 0
    $script:generated = 0
    $script:shown = 0
    $script:secretInputs = [Collections.Generic.Queue[string]]::new()
    $script:calls = [Collections.Generic.List[string]]::new()
}

function Get-DiskImage([string]$ImagePath) {
    if ($ImagePath -ne $script:image) { throw 'Unexpected test image' }
    [pscustomobject]@{ Attached = $script:attached }
}
function Get-Disk { process { $_ } }
function Get-Partition {
    process {
        $path = if ($script:reportOtherVolume) { 'X:\' } else { $script:volumePath }
        [pscustomobject]@{ AccessPaths = @($path) }
    }
}
function Mount-DiskImage {
    [CmdletBinding()]
    param([string]$ImagePath, [switch]$NoDriveLetter)
    if ($ImagePath -ne $script:image -or -not $NoDriveLetter -or $script:attached) { throw 'Unexpected test mount' }
    $script:attached = $true
}
function Dismount-DiskImage {
    [CmdletBinding()]
    param([string]$ImagePath)
    if ($ImagePath -ne $script:image -or -not $script:locked) { throw 'Unsafe test detach' }
    $script:attached = $false
}
function Get-CimInstance([string]$Namespace, [string]$ClassName) {
    if ($ClassName -ne 'Win32_EncryptableVolume') { throw 'Unexpected test volume query' }
    [pscustomobject]@{ DeviceID = $script:volumePath }
}
function Read-Host([string]$Prompt, [switch]$AsSecureString) {
    if (-not $AsSecureString) { throw 'Plaintext test input was requested' }
    $script:prompts++
    ConvertTo-SecureString $script:secretInputs.Dequeue() -AsPlainText -Force
}
function Show-VeilRecoveryBackup([guid]$RecoveryId, [string]$NumericalPassword) {
    if ($RecoveryId -ne $script:recovery -or $NumericalPassword -cne $script:recoveryPassword -or $script:locked) {
        throw 'Unexpected test recovery display'
    }
    $script:shown++
}
function Invoke-CimMethod {
    [CmdletBinding()]
    param($InputObject, [string]$MethodName, [hashtable]$Arguments)
    if ($InputObject.DeviceID -ne $script:volumePath -or -not $script:attached) { throw 'Unexpected target volume' }
    $script:calls.Add($MethodName)
    $result = @{ ReturnValue = 0 }
    switch ($MethodName) {
        'GetLockStatus' { $result.LockStatus = [int]$script:locked }
        'GetConversionStatus' { $result.ConversionStatus = 1 }
        'GetProtectionStatus' { $result.ProtectionStatus = 1 }
        'IsAutoUnlockEnabled' { $result.IsAutoUnlockEnabled = $false }
        'GetKeyProtectors' {
            if ($Arguments.KeyProtectorType -eq 8) {
                $result.VolumeKeyProtectorID = @($script:primary.ToString('B'))
                if ($script:extraPassphrase) { $result.VolumeKeyProtectorID += '{44444444-4444-4444-4444-444444444444}' }
            }
            elseif ($Arguments.KeyProtectorType -eq 2) {
                $result.VolumeKeyProtectorID = @()
                if ($script:recoveryPresent) { $result.VolumeKeyProtectorID = @($script:recovery.ToString('B')) }
            }
            else { throw 'Unexpected test protector type' }
        }
        'ProtectKeyWithNumericalPassword' {
            if ($script:locked -or $Arguments.ContainsKey('NumericalPassword')) { throw 'Unsafe test protector generation' }
            $script:recoveryPresent = $true
            $script:generated++
            $result.VolumeKeyProtectorID = $script:recovery.ToString('B')
        }
        'GetKeyProtectorNumericalPassword' {
            if ($script:locked -or $Arguments.VolumeKeyProtectorID -ne $script:recovery.ToString('B')) { throw 'Unexpected test protector lookup' }
            $result.NumericalPassword = $script:recoveryPassword
        }
        'UnlockWithPassphrase' {
            if ($Arguments.Passphrase -cne $script:normalPassword) { $result.ReturnValue = 1 }
            else { $script:locked = $false }
        }
        'UnlockWithNumericalPassword' {
            if (-not $script:locked) { throw 'Attempted to verify recovery against an already unlocked volume' }
            if ($Arguments.NumericalPassword -cnotin @($script:recoveryPassword, $script:otherPassword)) { $result.ReturnValue = 1 }
            else { $script:locked = $false }
        }
        'Lock' {
            if ($Arguments.ForceDismount -ne $false) { throw 'Forced test lock refused' }
            if ($script:failLock) { $result.ReturnValue = 1 }
            else { $script:locked = $true }
        }
        default { throw 'Unexpected test BitLocker method' }
    }
    return $result
}

function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Reason*") { throw }
        return
    }
    throw 'Expected recovery verification to fail.'
}

Reset-FakeRecovery
$secretInputs.Enqueue($normalPassword)
$secretInputs.Enqueue($recoveryPassword)
$confirmed = @(Register-VeilRecoveryBackup $image $volumePath $primary)
if ($confirmed.Count -ne 1 -or $confirmed[0] -ne $recovery -or $generated -ne 1 -or $shown -ne 1 -or $attached -or -not $locked) {
    throw 'Enrollment did not verify and return only the recovery protector ID.'
}
if ([string]::Join(',', $calls) -notmatch 'UnlockWithPassphrase.*ProtectKeyWithNumericalPassword.*Lock.*UnlockWithNumericalPassword.*Lock') {
    throw 'Unexpected enrollment and lock order.'
}
Write-Output 'PASS generated backup is verified against a locked volume before registration'

$secretInputs.Enqueue($normalPassword)
$secretInputs.Enqueue($recoveryPassword)
if ((Register-VeilRecoveryBackup $image $volumePath $primary) -ne $recovery -or $generated -ne 1 -or $attached) {
    throw 'Retry did not reuse the existing protector.'
}
Write-Output 'PASS enrollment retry preserves the existing recovery protector'

$secretInputs.Enqueue($normalPassword)
Assert-Rejected { Register-VeilRecoveryBackup $image $volumePath $primary $primary } 'selected recovery protector is not on'
if ($attached -or -not $locked -or $shown -ne 2 -or $generated -ne 1) { throw 'Unknown recovery ID was used.' }
Write-Output 'PASS enrollment refuses an explicitly selected protector from outside the recovery list'

$secretInputs.Enqueue($otherPassword)
Assert-Rejected { Test-VeilRecoveryBackup $image $volumePath $recovery } 'does not match'
if ($attached -or -not $locked) { throw 'Recovery test failed to clean up after an alternate protector.' }
$secretInputs.Enqueue('wrong backup')
Assert-Rejected { Test-VeilRecoveryBackup $image $volumePath $recovery } 'refused UnlockWithNumericalPassword'
if ($attached -or -not $locked) { throw 'Recovery test failed to clean up after an invalid password.' }
$secretInputs.Enqueue($recoveryPassword)
if (@(Test-VeilRecoveryBackup $image $volumePath $recovery).Count -ne 0 -or $attached -or -not $locked) {
    throw 'The recovery test did not finish locked and detached without outputting a secret.'
}
Write-Output 'PASS recovery drill rejects wrong and alternate passwords and performs cleanup'

Reset-FakeRecovery
$reportOtherVolume = $true
Assert-Rejected { Register-VeilRecoveryBackup $image $volumePath $primary } 'does not belong'
if ($prompts -ne 0 -or $attached -or $calls.Count -ne 0) { throw 'An unrelated test volume was used.' }
Write-Output 'PASS unregistered volume is refused before receiving credentials'

Reset-FakeRecovery
$extraPassphrase = $true
$secretInputs.Enqueue($normalPassword)
Assert-Rejected { Register-VeilRecoveryBackup $image $volumePath $primary } 'exactly one passphrase protector'
if ($attached -or -not $locked -or $generated -ne 0) { throw 'Ambiguous passphrase registration was not refused.' }
Write-Output 'PASS volume registration refuses ambiguous passphrase protectors'

Reset-FakeRecovery
$failLock = $true
$secretInputs.Enqueue($normalPassword)
Assert-Rejected { Register-VeilRecoveryBackup $image $volumePath $primary } 'refused Lock'
if (-not $attached -or $locked -or ($calls -contains 'UnlockWithNumericalPassword')) {
    throw 'Lock failure did not stop the test without forced detach.'
}
Write-Output 'PASS failure to lock blocks registration and leaves the image for safe cleanup'
