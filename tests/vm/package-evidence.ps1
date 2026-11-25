# SPDX-License-Identifier: MPL-2.0
# Pure evidence inventory for the disposable VM package test. No installation or trust changes.
$script:VeilPackageSignChecks = @('package-sign', 'package-tamper-valid', 'package-tamper-modified-file',
    'package-tamper-extra-file', 'package-tamper-missing-file', 'package-tamper-edited-manifest',
    'package-tamper-wrong-signer', 'package-tamper-unsigned-binary')
$script:VeilPackageManualChecks = @('installed-unlock', 'installed-relock', 'installed-refuses')
$script:VeilPackageInstallChecks = @('package-install') + $script:VeilPackageManualChecks
$script:VeilPackageUpdateChecks = @('package-update', 'package-same-version', 'package-lower-version',
    'package-registration-kept', 'package-interrupt-pre-commit', 'package-interrupt-post-commit')
$script:VeilPackageRequiredChecks = $script:VeilPackageSignChecks + $script:VeilPackageInstallChecks +
    $script:VeilPackageUpdateChecks + @('package-remove')

function Get-VeilPackageEvidenceGaps([object[]]$Records, [string[]]$Required) {
    foreach ($id in $Required) {
        $matches = @($Records | Where-Object { $_.id -ceq $id })
        $source = if ($id -cin $script:VeilPackageManualChecks) { 'operator' } else { 'automatic' }
        $status = if ($matches.Count -eq 0) { 'MISSING' }
            elseif ($matches.Count -ne 1) { 'DUPLICATE' }
            elseif ($matches[0].source -cne $source) { 'WRONG SOURCE' }
            elseif ($matches[0].status -cne 'PASS') { 'NOT PASS' }
            else { $null }
        if ($status) { [pscustomobject]@{ id = $id; status = $status } }
    }
}

function Assert-VeilPackageEvidence([object[]]$Records, [string[]]$Required) {
    $gaps = @(Get-VeilPackageEvidenceGaps $Records $Required)
    if ($gaps.Count) {
        throw ('Package evidence incomplete: ' + (($gaps | ForEach-Object { $_.id + '=' + $_.status }) -join ', '))
    }
}

function Assert-VeilPackageUnrecorded([object[]]$Records, [string[]]$Ids) {
    foreach ($id in $Ids) {
        if (@($Records | Where-Object { $_.id -ceq $id }).Count) {
            throw "Package check $id already recorded. Preserve evidence and use a fresh disposable VM snapshot."
        }
    }
}
