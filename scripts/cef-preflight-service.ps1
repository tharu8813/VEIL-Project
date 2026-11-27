# SPDX-License-Identifier: MPL-2.0
# Disabled service registration only; no start, stop, deletion or CEF activation.
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
