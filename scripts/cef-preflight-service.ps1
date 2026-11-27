# SPDX-License-Identifier: MPL-2.0
# Disabled registration and isolated service lifecycle probe; no CEF activation.
. (Join-Path $PSScriptRoot 'cef-preflight-publication.ps1')

$script:VeilPreflightServiceName = 'Veil.VolumeBroker'
# SYSTEM and Administrators manage the service; authenticated users may
# query configuration/status and read its descriptor, never start or stop it.
$script:VeilPreflightServiceDacl = 'D:P(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCLORC;;;AU)'

function Assert-VeilDisabledPreflightService($Service,[string]$Command) {
    if ($null -eq $Service -or @($Service).Count -ne 1 -or
        $Service.Name -cne $script:VeilPreflightServiceName -or $Service.PathName -cne $Command -or
        $Service.StartName -ne 'LocalSystem' -or $Service.StartMode -ne 'Disabled' -or
        $Service.State -ne 'Stopped' -or $Service.ProcessId -ne 0 -or $Service.ServiceType -ne 'Own Process') {
        throw 'Expected the exact disabled, stopped LocalSystem broker service.'
    }
}

function Assert-VeilPreflightServiceDacl([string]$Sddl) {
    $expected = [Security.AccessControl.RawSecurityDescriptor]::new($script:VeilPreflightServiceDacl)
    $actual = [Security.AccessControl.RawSecurityDescriptor]::new($Sddl)
    $section = [Security.AccessControl.AccessControlSections]::Access
    if ($actual.GetSddlForm($section) -cne $expected.GetSddlForm($section)) {
        throw 'Unexpected broker service access control.'
    }
}

function Register-VeilDisabledPreflightService([string]$JournalRoot,[string]$Command,
    [scriptblock]$VerifyPublication,[scriptblock]$ReadService,[scriptblock]$CreateDisabled,
    [scriptblock]$SetDacl,[scriptblock]$ReadDacl) {
    if (Test-Path -LiteralPath $JournalRoot) { throw 'Existing or interrupted service registration must be preserved.' }
    if ($null -ne (& $ReadService)) { throw 'Existing broker service refused.' }
    $publication = & $VerifyPublication
    if ($publication.state -cne 'files-published-only' -or $publication.startupEnabled -isnot [bool] -or
        $publication.startupEnabled -or $publication.serviceCreated -isnot [bool] -or $publication.serviceCreated -or
        $publication.sourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or $publication.intentSha256 -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Fully verified files-published-only state required.'
    }
    Assert-VeilAdminPath (Split-Path -Parent $JournalRoot)
    New-VeilRestrictedDirectory $JournalRoot
    $intent = [ordered]@{
        kind='cef-preflight-service';format=1;state='registration-intent'
        sourceCommit=$publication.sourceCommit;publicationIntentSha256=$publication.intentSha256
        serviceName=$script:VeilPreflightServiceName;command=$Command;startupEnabled=$false
    }
    $intentHash = Write-VeilPreflightDraftFile (Join-Path $JournalRoot '01-service-intent.json') $intent
    Assert-VeilPreflightPublicationEqual (& $VerifyPublication) $publication
    if ($null -ne (& $ReadService)) { throw 'Broker service appeared during registration.' }
    # Create disabled from the outset, then restrict SCM access and re-query.
    # On any failure preserve the intent and existing SCM record. Never
    # adopt or alter a service with an unexpected identity.
    & $CreateDisabled $Command
    Assert-VeilDisabledPreflightService (& $ReadService) $Command
    & $SetDacl $script:VeilPreflightServiceDacl
    Assert-VeilDisabledPreflightService (& $ReadService) $Command
    Assert-VeilPreflightServiceDacl (& $ReadDacl)
    Assert-VeilPreflightPublicationEqual (& $VerifyPublication) $publication
    Assert-VeilDisabledPreflightService (& $ReadService) $Command
    Assert-VeilPreflightServiceDacl (& $ReadDacl)
    $final = [ordered]@{
        kind='cef-preflight-service';format=1;state='registered-disabled'
        sourceCommit=$publication.sourceCommit;intentSha256=$intentHash
        serviceCreated=$true;startupEnabled=$false
    }
    [void](Write-VeilPreflightDraftFile (Join-Path $JournalRoot '02-service-registered.json') $final)
    return $final
}


function Read-VeilPreflightServiceRegistration([string]$JournalRoot,[string]$Command,
    [scriptblock]$VerifyPublication,[scriptblock]$ReadService,[scriptblock]$ReadDacl) {
    Assert-VeilAdminPath $JournalRoot
    $items = @(Get-ChildItem -LiteralPath $JournalRoot -Force)
    if ($items.Count -ne 2 -or @($items | Where-Object {
        $_.PSIsContainer -or $_.Name -cnotin @('01-service-intent.json','02-service-registered.json')
    }).Count -ne 0) { throw 'Incomplete service registration; preserve state.' }
    Assert-VeilDisabledPreflightService (& $ReadService) $Command
    Assert-VeilPreflightServiceDacl (& $ReadDacl)
    $publication = & $VerifyPublication
    if ($publication.state -cne 'files-published-only' -or $publication.startupEnabled -isnot [bool] -or
        $publication.startupEnabled -or $publication.serviceCreated -isnot [bool] -or $publication.serviceCreated -or
        $publication.sourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or $publication.intentSha256 -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Fully verified file publication required.'
    }
    $intent = [ordered]@{
        kind='cef-preflight-service';format=1;state='registration-intent'
        sourceCommit=$publication.sourceCommit;publicationIntentSha256=$publication.intentSha256
        serviceName=$script:VeilPreflightServiceName;command=$Command;startupEnabled=$false
    }
    $intentHash = Assert-VeilPreflightDraftBytes (Read-VeilPreflightDraftBytes (Join-Path $JournalRoot '01-service-intent.json')) $intent
    $final = [ordered]@{
        kind='cef-preflight-service';format=1;state='registered-disabled'
        sourceCommit=$publication.sourceCommit;intentSha256=$intentHash
        serviceCreated=$true;startupEnabled=$false
    }
    $finalHash = Assert-VeilPreflightDraftBytes (Read-VeilPreflightDraftBytes (Join-Path $JournalRoot '02-service-registered.json')) $final
    Assert-VeilDisabledPreflightService (& $ReadService) $Command
    Assert-VeilPreflightServiceDacl (& $ReadDacl)
    return [pscustomobject]@{state='registered-disabled';sourceCommit=$publication.sourceCommit;registrationSha256=$finalHash;startupEnabled=$false}
}

function Assert-VeilPreflightServiceIdentity($Service,[string]$Command) {
    if ($null -eq $Service -or @($Service).Count -ne 1 -or
        $Service.Name -cne $script:VeilPreflightServiceName -or $Service.PathName -cne $Command -or
        $Service.StartName -ne 'LocalSystem' -or $Service.ServiceType -ne 'Own Process') {
        throw 'Unexpected service identity; do not modify it.'
    }
}

function Invoke-VeilPreflightServiceProbe([string]$ProbeRoot,[string]$Command,
    [scriptblock]$VerifyRegistration,[scriptblock]$ReadService,[scriptblock]$ReadDacl,
    [scriptblock]$SetManual,[scriptblock]$Start,[scriptblock]$Disable,[scriptblock]$Stop,[scriptblock]$RequireDetached,
    [scriptblock]$Exercise) {
    if (Test-Path -LiteralPath $ProbeRoot) { throw 'Existing or interrupted probe must be preserved.' }
    $registrationEvidence = & $VerifyRegistration
    if ($registrationEvidence.state -cne 'registered-disabled' -or $registrationEvidence.startupEnabled -isnot [bool] -or
        $registrationEvidence.startupEnabled -or $registrationEvidence.sourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or
        $registrationEvidence.registrationSha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Verified disabled registration required.' }
    & $RequireDetached
    Assert-VeilAdminPath (Split-Path -Parent $ProbeRoot)
    New-VeilRestrictedDirectory $ProbeRoot
    $intent = [ordered]@{
        kind='cef-preflight-service-probe';format=1;state='probe-intent'
        sourceCommit=$registrationEvidence.sourceCommit;registrationSha256=$registrationEvidence.registrationSha256
        startupEnabled=$false
    }
    $intentHash = Write-VeilPreflightDraftFile (Join-Path $ProbeRoot '01-probe-intent.json') $intent
    Assert-VeilPreflightPublicationEqual (& $VerifyRegistration) $registrationEvidence
    & $RequireDetached
    # Once mutation is attempted, always try to disable and stop the exact
    # service, including when the start call itself reports an error.
    try {
        & $SetManual
        $current = & $ReadService
        Assert-VeilPreflightServiceIdentity $current $Command
        Assert-VeilPreflightServiceDacl (& $ReadDacl)
        if ($current.StartMode -ne 'Manual' -or $current.State -ne 'Stopped' -or $current.ProcessId -ne 0) {
            throw 'Expected manual stopped broker immediately before start.'
        }
        & $RequireDetached
        & $Start
        $running = & $ReadService
        Assert-VeilPreflightServiceIdentity $running $Command
        Assert-VeilPreflightServiceDacl (& $ReadDacl)
        if ($running.State -ne 'Running' -or $running.StartMode -ne 'Manual' -or $running.ProcessId -le 0) {
            throw 'Broker did not reach expected running state.'
        }
        $runningPid = [int]$running.ProcessId
        & $RequireDetached
        [void](Write-VeilPreflightDraftFile (Join-Path $ProbeRoot '02-running-observed.json') ([ordered]@{
            state='running-observed';intentSha256=$intentHash;startupEnabled=$false
        }))
        if ($null -ne $Exercise) {
            $observation = & $Exercise
            if ($observation.kind -cne 'standard-user-exit-observation' -or
                $observation.exitCode -isnot [int] -or $observation.exitCode -ne 29 -or
                $observation.processId -isnot [int] -or $observation.processId -le 0 -or
                $observation.sessionId -isnot [int] -or $observation.sessionId -le 0 -or
                $observation.tokenVerified -isnot [bool] -or -not $observation.tokenVerified) {
                throw 'Protected preflight lacks verified standard-user exit observation.'
            }
            $after = & $ReadService
            Assert-VeilPreflightServiceIdentity $after $Command
            Assert-VeilPreflightServiceDacl (& $ReadDacl)
            if ($after.State -ne 'Running' -or $after.StartMode -ne 'Manual' -or $after.ProcessId -ne $runningPid) {
                throw 'Broker changed during protected preflight.'
            }
            & $RequireDetached
            $exerciseHash = Write-VeilPreflightDraftFile (Join-Path $ProbeRoot '02a-protected-preflight.json') ([ordered]@{
                state='protected-preflight-exit-observed';intentSha256=$intentHash;exitCode=29;startupEnabled=$false
                processId=$observation.processId;sessionId=$observation.sessionId;tokenVerified=$true;brokerProcessId=$runningPid
            })
        }
    }
    finally {
        $current = & $ReadService
        Assert-VeilPreflightServiceIdentity $current $Command
        Assert-VeilPreflightServiceDacl (& $ReadDacl)
        # Disable first to prevent another normal SCM start. Stop is graceful
        # only; never force-kill the broker or force-dismount a volume.
        & $Disable
        $current = & $ReadService
        Assert-VeilPreflightServiceIdentity $current $Command
        if ($current.StartMode -ne 'Disabled') { throw 'Could not disable broker.' }
        if ($current.State -ne 'Stopped') { & $Stop }
        Assert-VeilDisabledPreflightService (& $ReadService) $Command
        Assert-VeilPreflightServiceDacl (& $ReadDacl)
        & $RequireDetached
    }
    Assert-VeilPreflightPublicationEqual (& $VerifyRegistration) $registrationEvidence
    $final = [ordered]@{
        kind='cef-preflight-service-probe';format=1;state='running-then-stopped'
        intentSha256=$intentHash;sourceCommit=$registrationEvidence.sourceCommit;startupEnabled=$false
    }
    if ($null -ne $Exercise) {
        $final.Add('protectedObservationSha256',$exerciseHash)
    }
    [void](Write-VeilPreflightDraftFile (Join-Path $ProbeRoot '03-stopped-verified.json') $final)
    return $final
}


function Assert-VeilCompletedServiceProbe([string]$ProbeRoot,$RegistrationEvidence) {
    Assert-VeilAdminPath $ProbeRoot
    $items = @(Get-ChildItem -LiteralPath $ProbeRoot -Force)
    if ($items.Count -ne 3 -or @($items | Where-Object {
        $_.PSIsContainer -or $_.Name -cnotin @('01-probe-intent.json','02-running-observed.json','03-stopped-verified.json')
    }).Count -ne 0) { throw 'Complete isolated service probe required.' }
    $intent = [ordered]@{
        kind='cef-preflight-service-probe';format=1;state='probe-intent'
        sourceCommit=$RegistrationEvidence.sourceCommit;registrationSha256=$RegistrationEvidence.registrationSha256;startupEnabled=$false
    }
    $digest = Assert-VeilPreflightDraftBytes (Read-VeilPreflightDraftBytes (Join-Path $ProbeRoot '01-probe-intent.json')) $intent
    [void](Assert-VeilPreflightDraftBytes (Read-VeilPreflightDraftBytes (Join-Path $ProbeRoot '02-running-observed.json')) ([ordered]@{
        state='running-observed';intentSha256=$digest;startupEnabled=$false
    }))
    [void](Assert-VeilPreflightDraftBytes (Read-VeilPreflightDraftBytes (Join-Path $ProbeRoot '03-stopped-verified.json')) ([ordered]@{
        kind='cef-preflight-service-probe';format=1;state='running-then-stopped'
        intentSha256=$digest;sourceCommit=$RegistrationEvidence.sourceCommit;startupEnabled=$false
    }))
}


# A completed protected session is distinct from the service-only probe.
# Reconstruct every byte and bind the observation to the final cleanup marker.
# This inspects trusted administrator records; it cannot prove historical
# runtime behavior independently of the administrator who collected them.
function Read-VeilCompletedProtectedSession([string]$ProbeRoot,$RegistrationEvidence) {
    if ($RegistrationEvidence.state -cne 'registered-disabled' -or
        $RegistrationEvidence.startupEnabled -isnot [bool] -or $RegistrationEvidence.startupEnabled -or
        $RegistrationEvidence.sourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or
        $RegistrationEvidence.registrationSha256 -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Expected freshly verified disabled registration.'
    }
    Assert-VeilAdminPath $ProbeRoot
    $names = @('01-probe-intent.json','02-running-observed.json','02a-protected-preflight.json','03-stopped-verified.json')
    $items = @(Get-ChildItem -LiteralPath $ProbeRoot -Force)
    if ($items.Count -ne 4 -or @($items | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $names }).Count -ne 0) {
        throw 'Incomplete or unexpected protected session evidence.'
    }
    $bytes = @{}
    foreach ($name in $names) { $bytes[$name] = Read-VeilPreflightDraftBytes (Join-Path $ProbeRoot $name) }
    $intent = [ordered]@{
        kind='cef-preflight-service-probe';format=1;state='probe-intent'
        sourceCommit=$RegistrationEvidence.sourceCommit;registrationSha256=$RegistrationEvidence.registrationSha256;startupEnabled=$false
    }
    $digest = Assert-VeilPreflightDraftBytes $bytes['01-probe-intent.json'] $intent
    [void](Assert-VeilPreflightDraftBytes $bytes['02-running-observed.json'] ([ordered]@{
        state='running-observed';intentSha256=$digest;startupEnabled=$false
    }))
    $observation = [Text.UTF8Encoding]::new($false,$true).GetString($bytes['02a-protected-preflight.json']) | ConvertFrom-Json
    foreach ($value in @($observation.processId,$observation.sessionId,$observation.brokerProcessId)) {
        if ($value -isnot [int] -or $value -le 0) { throw 'Invalid process/session observation.' }
    }
    if ($observation.processId -eq $observation.brokerProcessId -or $observation.exitCode -isnot [int] -or
        $observation.exitCode -ne 29 -or $observation.tokenVerified -isnot [bool] -or -not $observation.tokenVerified) {
        throw 'Invalid standard-user observation.'
    }
    $observationHash = Assert-VeilPreflightDraftBytes $bytes['02a-protected-preflight.json'] ([ordered]@{
        state='protected-preflight-exit-observed';intentSha256=$digest;exitCode=29;startupEnabled=$false
        processId=$observation.processId;sessionId=$observation.sessionId;tokenVerified=$true;brokerProcessId=$observation.brokerProcessId
    })
    $finalHash = Assert-VeilPreflightDraftBytes $bytes['03-stopped-verified.json'] ([ordered]@{
        kind='cef-preflight-service-probe';format=1;state='running-then-stopped'
        intentSha256=$digest;sourceCommit=$RegistrationEvidence.sourceCommit;startupEnabled=$false
        protectedObservationSha256=$observationHash
    })
    foreach ($name in $names) {
        if ([Convert]::ToBase64String((Read-VeilPreflightDraftBytes (Join-Path $ProbeRoot $name))) -cne
            [Convert]::ToBase64String($bytes[$name])) { throw 'Protected session evidence changed.' }
    }
    return [pscustomobject]@{
        state='verified-protected-preflight-records';sourceCommit=$RegistrationEvidence.sourceCommit
        registrationSha256=$RegistrationEvidence.registrationSha256;sessionSha256=$finalHash
        startupEnabled=$false
    }
}
