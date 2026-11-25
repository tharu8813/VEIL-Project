# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot '..\..\scripts\chromium\Test-UpstreamRuntime.ps1'
$root=Join-Path $env:RUNNER_TEMP ('veil-smoke-plan-'+[Guid]::NewGuid().ToString('N'))
foreach ($config in @('Debug','Release')) {
    $receipt=Join-Path $root ("veil-build-$config-"+('a'*32)+'\receipt.json')
    $p=& $script -Workspace $root -Receipt $receipt -Configuration $config | Out-String | ConvertFrom-Json
    if ($p.status -ne 'PLAN_ONLY' -or $p.configuration -ne $config -or $p.runtime -ne 'NOT RUN' -or $p.sandbox -ne 'NOT RUN' -or $p.siteIsolation -ne 'NOT RUN' -or $p.releaseApproved -ne $false -or $p.executable -notlike "*Veil$config\chrome.exe") { throw 'Smoke plan contract failed.' }
}
$rejected=$false
try { & $script -Workspace $root -Receipt (Join-Path $root 'receipt.json') | Out-Null } catch { $rejected=$_.Exception.Message -like '*Expected build receipt*' }
if (-not $rejected -or (Test-Path -LiteralPath $root)) { throw 'Invalid receipt path not refused without writes.' }
Write-Output 'PASS: runtime smoke plans and invalid receipt path refusal; browser not executed.'
