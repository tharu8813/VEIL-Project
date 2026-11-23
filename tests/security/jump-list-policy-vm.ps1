<#
SEC-004 diagnostic only. Run ONLY as an ordinary user in the disposable offline VirtualBox VM.
Example (one line):
powershell -NoProfile -ExecutionPolicy Bypass -File tests/security/jump-list-policy-vm.ps1 -Run -Preflight .veil-dev/phase34-preflight.json -DesktopDll C:\VEIL-Batch\desktop\Veil.Desktop.dll

Temporarily changes HKCU Explorer policies; restores them in finally. A killed process may
skip finally; use -RestoreOnly -RecoveryJson <restore.json> before continuing.
NOT_OBSERVED is not a security PASS: shell settings may require logout or Explorer restart.
#>
param(
    [switch]$Run,
    [switch]$RestoreOnly,
    [string]$Preflight,
    [string]$DesktopDll,
    [string]$RecoveryJson
)
$ErrorActionPreference = 'Stop'
if ([bool]$Run -eq [bool]$RestoreOnly) { throw 'Choose exactly one of -Run and -RestoreOnly.' }
. (Join-Path $PSScriptRoot '..\phase3\phase34-guest-identity.ps1')
$guest = Get-VeilPhase34Guest
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if (-not $guest.virtualBox -or -not $guest.noDefaultRoute -or $guest.elevated) {
    throw 'Only a standard-user session in an offline VirtualBox test VM may run this experiment.'
}
$policies = @(
    @{ name = 'Start_TrackDocs'; key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'; candidate = 0 },
    @{ name = 'NoRecentDocsHistory'; key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; candidate = 1 }
)
function Read-PolicyState($definition) {
    $keyPresent = Test-Path -LiteralPath $definition.key
    $item = if ($keyPresent) { Get-Item -LiteralPath $definition.key } else { $null }
    $present = ($null -ne $item) -and ($item.GetValueNames() -contains $definition.name)
    if ($present -and $item.GetValueKind($definition.name).ToString() -ne 'DWord') {
        throw "Refusing to replace a non-DWORD policy: $($definition.name)"
    }
    @{
        name = $definition.name
        keyPresent = [bool]$keyPresent
        present = [bool]$present
        value = if ($present) { [int]$item.GetValue($definition.name) } else { $null }
    }
}
function Restore-Policies($saved) {
    foreach ($definition in $policies) {
        $old = @($saved | Where-Object { $_.name -eq $definition.name })
        if ($old.Count -ne 1 -or $old[0].present -isnot [bool] -or $old[0].keyPresent -isnot [bool]) {
            throw "Invalid recovery state for $($definition.name)"
        }
        if ($old[0].present) {
            if (-not (Test-Path -LiteralPath $definition.key)) { New-Item -Path $definition.key -Force | Out-Null }
            New-ItemProperty -LiteralPath $definition.key -Name $definition.name -PropertyType DWord -Value ([int]$old[0].value) -Force | Out-Null
        }
        elseif (Test-Path -LiteralPath $definition.key) {
            Remove-ItemProperty -LiteralPath $definition.key -Name $definition.name -ErrorAction SilentlyContinue
        }
        if (-not $old[0].keyPresent -and (Test-Path -LiteralPath $definition.key)) {
            $key = Get-Item -LiteralPath $definition.key
            if ($key.GetValueNames().Count -eq 0 -and $key.GetSubKeyNames().Count -eq 0) {
                Remove-Item -LiteralPath $definition.key -Force
            }
        }
    }
    foreach ($definition in $policies) {
        $old = @($saved | Where-Object { $_.name -eq $definition.name })[0]
        $now = Read-PolicyState $definition
        if ($now.present -ne $old.present -or ($old.present -and $now.value -ne $old.value)) {
            throw "Registry restoration was not verified: $($definition.name)"
        }
    }
}
function Set-Candidate($name) {
    $match = @($policies | Where-Object { $_.name -eq $name })
    if ($match.Count -ne 1) { throw "Unknown candidate: $name" }
    $definition = $match[0]
    if (-not (Test-Path -LiteralPath $definition.key)) { New-Item -Path $definition.key -Force | Out-Null }
    New-ItemProperty -LiteralPath $definition.key -Name $name -Value $definition.candidate -PropertyType DWord -Force | Out-Null
    if ((Read-PolicyState $definition).value -ne $definition.candidate) { throw "Cannot apply candidate: $name" }
}
if ($RestoreOnly) {
    if (-not $RecoveryJson -or -not (Test-Path -LiteralPath $RecoveryJson -PathType Leaf)) {
        throw 'Supply -RecoveryJson from this VM and Windows user.'
    }
    $journal = Get-Content -LiteralPath $RecoveryJson -Raw | ConvertFrom-Json
    if ($journal.schema -ne 1 -or $journal.guestUuid -ne $guest.uuid -or $journal.userSid -ne $currentSid) { throw 'Recovery file does not belong to this VM and Windows user.' }
    Restore-Policies $journal.original
    Write-Host 'RESTORED: original HKCU Jump List policy values verified.'
    return
}
if (-not $Preflight -or -not $DesktopDll) { throw '-Run requires -Preflight and -DesktopDll.' }
$pre = Get-Content -LiteralPath $Preflight -Raw | ConvertFrom-Json
if ($pre.schema -ne 1 -or $pre.guest.uuid -ne $guest.uuid -or
    $pre.guest.boot -ne $guest.boot -or -not $pre.guest.elevated) {
    throw 'Preflight proof does not match this offline guest boot.'
}
$primary = @($pre.volumes | Where-Object { $_.role -eq 'primary' })
if ($primary.Count -ne 1 -or $primary[0].drive -notmatch '^[A-Z]:$' -or
    $primary[0].drive -eq $guest.systemDrive -or
    -not $primary[0].parent.StartsWith($primary[0].drive + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Only the preflight-registered protected test volume can hold the browsing profile.'
}
$actualVolume = (& "$env:SystemRoot\System32\mountvol.exe" ($primary[0].drive + '\') /L | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $actualVolume -ne $primary[0].volumeId) {
    throw 'Mounted lab volume does not match the preflight volume GUID.'
}
$profileRoot = Get-Item -LiteralPath $primary[0].parent -ErrorAction Stop
if (($profileRoot.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Refusing redirected profile parent.' }
if (-not [IO.Path]::IsPathRooted($DesktopDll) -or -not (Test-Path -LiteralPath $DesktopDll -PathType Leaf)) {
    throw 'Provide an absolute path to an existing Debug VEIL desktop DLL.'
}
$node = (Get-Command node.exe -ErrorAction Stop).Source
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runner = Join-Path $repo 'tests\security\browse-session.mjs'
$evidence = Join-Path $repo ('.veil-dev\jumplist-policy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$original = @($policies | ForEach-Object { Read-PolicyState $_ })
$journalPath = Join-Path $evidence 'restore.json'
@{ schema = 1; guestUuid = $guest.uuid; userSid = $currentSid; original = $original } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $journalPath -Encoding UTF8
$variants = @(
    @{ id = 'baseline'; settings = @() },
    @{ id = 'track-docs-off'; settings = @('Start_TrackDocs') },
    @{ id = 'recent-policy-on'; settings = @('NoRecentDocsHistory') },
    @{ id = 'both'; settings = @('Start_TrackDocs', 'NoRecentDocsHistory') }
)
$oldDesktop = [Environment]::GetEnvironmentVariable('VEIL_DESKTOP_DLL', 'Process')
$results = @()
try {
    [Environment]::SetEnvironmentVariable('VEIL_DESKTOP_DLL', $DesktopDll, 'Process')
    foreach ($variant in $variants) {
        try {
            Restore-Policies $original
            foreach ($name in $variant.settings) { Set-Candidate $name }
            $out = Join-Path $evidence ($variant.id + '.out.txt')
            $err = Join-Path $evidence ($variant.id + '.err.txt')
            $arguments = ('"{0}" --profile-parent "{1}" --runs 1 --assert-clean' -f $runner, $primary[0].parent)
            $proc = Start-Process -FilePath $node -ArgumentList $arguments -NoNewWindow -PassThru -Wait -RedirectStandardOutput $out -RedirectStandardError $err
            $log = Get-Content -LiteralPath $out -Raw
            $outcome = if ($proc.ExitCode -eq 1 -and $log.Contains('SYNTHETIC MARKER FOUND')) {
                'LEAK_OBSERVED'
            }
            elseif ($proc.ExitCode -eq 0 -and $log.Contains('NO SYNTHETIC MARKER IN CHANGED JUMP LISTS')) {
                'NOT_OBSERVED'
            }
            else { 'INCONCLUSIVE' }
            $results += @{ variant = $variant.id; outcome = $outcome; exitCode = $proc.ExitCode }
            Write-Host "$($variant.id): $outcome"
        }
        catch {
            $results += @{ variant = $variant.id; outcome = 'ERROR'; message = $_.Exception.Message }
            Write-Warning "$($variant.id): ERROR ($($_.Exception.Message))"
        }
        finally { Restore-Policies $original }
    }
}
finally {
    [Environment]::SetEnvironmentVariable('VEIL_DESKTOP_DLL', $oldDesktop, 'Process')
    Restore-Policies $original
    @{ schema = 1; scope = 'Offline VM same-session policy A/B: not release validation';
       guestUuid = $guest.uuid; results = $results; restoreJournal = $journalPath } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
    Write-Host "Evidence: $evidence"
    Write-Host 'Original HKCU values restored. Confirm before discarding the VM snapshot.'
}
if (@($results | Where-Object { $_.outcome -in @('ERROR', 'INCONCLUSIVE') }).Count -gt 0) { exit 2 }
