# Sourced by the elevated installer. No recovery password is accepted on the command line or emitted to its caller.

function Invoke-VeilBitLocker($Volume, [string]$Method, [hashtable]$Arguments = @{}) {
    $parameters = @{ InputObject = $Volume; MethodName = $Method; ErrorAction = 'Stop'; Verbose = $false; Debug = $false }
    if ($Arguments.Count -gt 0) { $parameters.Arguments = $Arguments }
    try {
        $result = Invoke-CimMethod @parameters
    }
    catch { throw "BitLocker could not complete $Method." }
    if ($null -eq $result.ReturnValue) { throw "BitLocker did not return a result for $Method." }
    if ($result.ReturnValue -ne 0) {
        throw ('BitLocker refused {0} (0x{1:X8}).' -f $Method, [uint32]$result.ReturnValue)
    }
    return $result
}

function Invoke-VeilUnlock($Volume, [string]$Method, [string]$Parameter, [securestring]$Secret) {
    if ($null -eq $Secret -or $Secret.Length -eq 0) { throw 'A secret must be entered.' }
    $buffer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
    $arguments = @{}
    try {
        $arguments[$Parameter] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($buffer)
        Invoke-VeilBitLocker $Volume $Method $arguments | Out-Null
    }
    finally {
        $arguments.Clear()
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($buffer)
    }
}

function Assert-VeilUnlockedProtection($Volume) {
    $locked = Invoke-VeilBitLocker $Volume GetLockStatus
    $conversion = Invoke-VeilBitLocker $Volume GetConversionStatus
    $protection = Invoke-VeilBitLocker $Volume GetProtectionStatus
    $automatic = Invoke-VeilBitLocker $Volume IsAutoUnlockEnabled
    if ($locked.LockStatus -ne 0 -or $conversion.ConversionStatus -ne 1 -or
        $protection.ProtectionStatus -ne 1 -or $automatic.IsAutoUnlockEnabled -ne $false) {
        throw 'The volume must be unlocked, fully encrypted, protected and without automatic unlock.'
    }
}

function Set-VeilRecoveryVolumeLocked($Volume) {
    $status = Invoke-VeilBitLocker $Volume GetLockStatus
    if ($status.LockStatus -eq 0) {
        Invoke-VeilBitLocker $Volume Lock @{ ForceDismount = $false } | Out-Null
        $status = Invoke-VeilBitLocker $Volume GetLockStatus
    }
    if ($status.LockStatus -ne 1) { throw 'Could not verify the recovery volume lock. Check it before continuing.' }
}

function Invoke-VeilDetachedVolume([string]$ImagePath, [string]$VolumePath, [scriptblock]$Action) {
    if ((Get-DiskImage -ImagePath $ImagePath).Attached) { throw 'The recovery image must start detached.' }
    $volume = $null
    try {
        Mount-DiskImage -ImagePath $ImagePath -NoDriveLetter -ErrorAction Stop | Out-Null
        $paths = (Get-DiskImage -ImagePath $ImagePath | Get-Disk | Get-Partition).AccessPaths
        if ($VolumePath -notin $paths) { throw 'The specified volume does not belong to the mounted VHDX.' }
        $volumes = @(Get-CimInstance -Namespace 'root/cimv2/Security/MicrosoftVolumeEncryption' `
            -ClassName Win32_EncryptableVolume | Where-Object DeviceID -eq $VolumePath)
        if ($volumes.Count -ne 1) { throw 'The registered BitLocker volume could not be identified.' }
        $volume = $volumes[0]
        if ((Invoke-VeilBitLocker $volume GetLockStatus).LockStatus -ne 1) {
            throw 'The volume must start locked to verify a recovery credential.'
        }
        return (& $Action $volume)
    }
    finally {
        if ((Get-DiskImage -ImagePath $ImagePath).Attached) {
            if ($null -ne $volume) { Set-VeilRecoveryVolumeLocked $volume }
            Dismount-DiskImage -ImagePath $ImagePath -ErrorAction Stop | Out-Null
            if ((Get-DiskImage -ImagePath $ImagePath).Attached) { throw 'The recovery image remains attached.' }
        }
    }
}

function Get-VeilNumericalPassword($Volume, [guid]$RecoveryId) {
    $result = Invoke-VeilBitLocker $Volume GetKeyProtectorNumericalPassword @{
        VolumeKeyProtectorID = $RecoveryId.ToString('B')
    }
    if ($result.NumericalPassword -notmatch '^[0-9]{6}(-[0-9]{6}){7}$') {
        throw 'The recovery protector did not return a numerical password.'
    }
    return $result.NumericalPassword
}

function Show-VeilRecoveryBackup([guid]$RecoveryId, [string]$NumericalPassword) {
    Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
    [System.Windows.MessageBox]::Show(
        "Keep this BitLocker recovery password outside this PC before continuing.`n`nProtector ID: $RecoveryId`n$NumericalPassword",
        'VEIL BitLocker recovery backup', 'OK', 'Warning') | Out-Null
}

function Confirm-VeilRecoveryPassword($Volume, [guid]$RecoveryId) {
    if ((Invoke-VeilBitLocker $Volume GetLockStatus).LockStatus -ne 1) {
        throw 'Recovery verification requires a locked volume.'
    }
    $secret = Read-Host 'Enter the 48-digit BitLocker recovery password from your external backup' -AsSecureString
    try {
        Invoke-VeilUnlock $Volume UnlockWithNumericalPassword NumericalPassword $secret
        Assert-VeilUnlockedProtection $Volume
        $expected = Get-VeilNumericalPassword $Volume $RecoveryId
        $buffer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        try {
            $entered = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($buffer)
            if ($entered.Replace('-', '').Replace(' ', '') -cne $expected.Replace('-', '')) {
                throw 'The backup unlocked the volume but does not match its registered recovery protector.'
            }
        }
        finally {
            $entered = $null
            $expected = $null
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($buffer)
        }
    }
    finally { if ($null -ne $secret) { $secret.Dispose() } }
}

function Register-VeilRecoveryBackup([string]$ImagePath, [string]$VolumePath, [guid]$ProtectorId,
    [guid]$PreferredRecoveryId = [guid]::Empty) {
    Invoke-VeilDetachedVolume $ImagePath $VolumePath {
        param($volume)
        $password = Read-Host 'Enter the current BitLocker volume passphrase' -AsSecureString
        try { Invoke-VeilUnlock $volume UnlockWithPassphrase Passphrase $password }
        finally { if ($null -ne $password) { $password.Dispose() } }
        Assert-VeilUnlockedProtection $volume
        $primary = Invoke-VeilBitLocker $volume GetKeyProtectors @{ KeyProtectorType = [uint32]8 }
        $passphraseIds = @($primary.VolumeKeyProtectorID | Where-Object { $_ })
        if ($passphraseIds.Count -ne 1 -or $passphraseIds[0] -ne $ProtectorId.ToString('B')) {
            throw 'The selected volume must have exactly one passphrase protector matching the registration.'
        }

        $recovery = Invoke-VeilBitLocker $volume GetKeyProtectors @{ KeyProtectorType = [uint32]2 }
        if ($PreferredRecoveryId -ne [guid]::Empty) {
            $id = $PreferredRecoveryId.ToString('B')
            if ($id -notin $recovery.VolumeKeyProtectorID) {
                throw 'The selected recovery protector is not on the registered volume.'
            }
        }
        else { $id = $recovery.VolumeKeyProtectorID | Select-Object -First 1 }
        if (-not $id) {
            $id = (Invoke-VeilBitLocker $volume ProtectKeyWithNumericalPassword @{
                FriendlyName = 'VEIL offline recovery'
            }).VolumeKeyProtectorID
        }
        $recoveryId = [guid]::Empty
        if (-not [guid]::TryParse($id, [ref]$recoveryId) -or $recoveryId -eq [guid]::Empty) {
            throw 'No valid BitLocker recovery protector ID was returned.'
        }
        Show-VeilRecoveryBackup $recoveryId (Get-VeilNumericalPassword $volume $recoveryId)
        Set-VeilRecoveryVolumeLocked $volume
        Confirm-VeilRecoveryPassword $volume $recoveryId
        return $recoveryId
    }
}

function Test-VeilRecoveryBackup([string]$ImagePath, [string]$VolumePath, [guid]$RecoveryId) {
    if ($RecoveryId -eq [guid]::Empty) { throw 'The installation has no verified recovery protector.' }
    Invoke-VeilDetachedVolume $ImagePath $VolumePath {
        param($volume)
        Confirm-VeilRecoveryPassword $volume $RecoveryId
    }
}
