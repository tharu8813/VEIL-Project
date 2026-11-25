# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot '..\..\scripts\chromium\Build-Upstream.ps1'
$pin=Get-Content -Raw (Join-Path $PSScriptRoot '..\..\chromium\candidate.json') | ConvertFrom-Json
$workspace=Join-Path $env:RUNNER_TEMP ('veil-build-plan-'+[Guid]::NewGuid().ToString('N'))
foreach ($config in @('Debug','Release')) {
    $plan=& $script -Workspace $workspace -Configuration $config | Out-String | ConvertFrom-Json
    if ($plan.status -ne 'PLAN_ONLY' -or $plan.configuration -ne $config -or $plan.chromiumRevision -cne $pin.revision -or $plan.depotToolsRevision -cne $pin.depotToolsRevision -or $plan.argsSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $plan.output -notlike "*Veil$config" -or $plan.build -ne 'NOT RUN' -or $plan.runtime -ne 'NOT RUN' -or $plan.sandbox -ne 'NOT RUN' -or (Test-Path -LiteralPath $workspace)) { throw 'Build plan contract failed.' }
}
$rejected=$false
try { & $script -Workspace $workspace -Execute | Out-Null } catch { $rejected=$_.Exception.Message -like '*explicit upstream hook/dependency review approval*' }
if (-not $rejected) { throw 'Missing hook approval not rejected.' }
$rejected=$false
try { & $script -Workspace $workspace -ApproveHooks | Out-Null } catch { $rejected=$_.Exception.Message -like '*requires Execute*' }
if (-not $rejected -or (Test-Path -LiteralPath $workspace)) { throw 'Approval without Execute was not refused without writes.' }
Write-Output 'PASS: Debug/Release read-only plans and hook authorization refusal; no build executed.'
