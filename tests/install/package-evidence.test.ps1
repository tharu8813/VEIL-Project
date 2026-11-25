# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\vm\package-evidence.ps1')
function Check([bool]$Passed, [string]$Name) {
    if (-not $Passed) { throw $Name }
    Write-Host "PASS $Name"
}
function Refused([scriptblock]$Action) {
    try { & $Action; return $false } catch { return $true }
}
Check ($script:VeilPackageRequiredChecks.Count -eq 29) 'all required package checks enumerated'
$good = @($script:VeilPackageRequiredChecks | ForEach-Object {
    [pscustomobject]@{ id = $_; status = 'PASS'; source = $(if ($_ -cin $script:VeilPackageManualChecks) { 'operator' } else { 'automatic' }) }
})
Check (@(Get-VeilPackageEvidenceGaps $good $script:VeilPackageRequiredChecks).Count -eq 0) 'complete inventory'
Assert-VeilPackageEvidence $good $script:VeilPackageRequiredChecks
foreach ($record in $good) {
    $id = $record.id
    $missing = @($good | Where-Object id -cne $id)
    Check (@(Get-VeilPackageEvidenceGaps $missing @($id))[0].status -ceq 'MISSING') "missing $id"
    Check (Refused { Assert-VeilPackageEvidence $missing @($id) }) "missing cannot pass $id"
}
$duplicate = $good + @($good[0])
Check (@(Get-VeilPackageEvidenceGaps $duplicate @($good[0].id))[0].status -ceq 'DUPLICATE') 'duplicate result cannot conceal evidence'
Check (Refused { Assert-VeilPackageUnrecorded $good @($good[0].id) }) 're-record refused'
$fake = @([pscustomobject]@{ id = 'package-sign'; status = 'PASS'; source = 'operator' })
Check (@(Get-VeilPackageEvidenceGaps $fake @('package-sign'))[0].status -ceq 'WRONG SOURCE') 'operator cannot certify automatic check'
$fake = @([pscustomobject]@{ id = 'installed-unlock'; status = 'PASS'; source = 'automatic' })
Check (@(Get-VeilPackageEvidenceGaps $fake @('installed-unlock'))[0].status -ceq 'WRONG SOURCE') 'automatic result cannot replace operator observation'
foreach ($status in 'FAIL', 'NOT RUN', 'OBSERVED') {
    $bad = @([pscustomobject]@{ id = 'package-sign'; status = $status; source = 'automatic' })
    Check (Refused { Assert-VeilPackageEvidence $bad @('package-sign') }) "$status cannot pass"
}
