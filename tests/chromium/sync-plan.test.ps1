# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot '..\..\scripts\chromium\Sync-Source.ps1'
$pin=Get-Content -Raw (Join-Path $PSScriptRoot '..\..\chromium\candidate.json') | ConvertFrom-Json
$workspace=Join-Path $env:RUNNER_TEMP ('veil-plan-'+[Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $workspace) { throw 'Test path already exists.' }
$report=& $script -Workspace $workspace | Out-String | ConvertFrom-Json
if ($report.status -ne 'PLAN_ONLY' -or $report.chromiumRevision -cne $pin.revision -or $report.depotToolsRevision -cne $pin.depotToolsRevision -or $report.sourceSync -ne 'NOT RUN' -or $report.build -ne 'NOT RUN' -or (Test-Path -LiteralPath $workspace)) { throw 'Plan contract failed.' }
$rejected=$false
try { & $script -Workspace $workspace -DepotToolsRevision ('0'*40) | Out-Null } catch { $rejected=$_.Exception.Message -like '*does not match candidate manifest*' }
if (-not $rejected -or (Test-Path -LiteralPath $workspace)) { throw 'Override not rejected without side effects.' }
Write-Output 'PASS: plan-only, pinned depot_tools, mismatch refused; no checkout or build.'
