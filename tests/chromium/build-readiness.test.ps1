# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\..\scripts\chromium\Get-BuildReadiness.ps1'
$parent = [IO.Path]::GetTempPath().TrimEnd('\')
$workspace = Join-Path $parent ('veil-readiness-' + [Guid]::NewGuid().ToString('N'))
function Read-Report([string]$path) {
    & $script -Workspace $path | Out-String | ConvertFrom-Json
}
if (Test-Path -LiteralPath $workspace) { throw 'Fixture unexpectedly exists.' }
$r = Read-Report $workspace
if ($r.status -ne 'OBSERVATION_ONLY' -or $r.sourceSyncApproved -ne $false -or $r.revisionCompatibility -ne 'NOT RUN' -or $r.chromiumBuild -ne 'NOT RUN' -or $r.sandbox -ne 'NOT RUN') { throw 'Observation contract changed.' }
if ($r.workspacePathExists -or (Test-Path -LiteralPath $workspace)) { throw 'Readiness must not create a workspace.' }
$r = Read-Report (Join-Path $parent 'veil readiness spaces')
if ($r.workspacePathShapeAccepted -or $r.sourceSyncPreflightBlockers -notcontains 'WORKSPACE_PATH_SHAPE') { throw 'Whitespace path blocker missing.' }
$r = Read-Report $parent
if (-not $r.workspacePathExists -or $r.sourceSyncPreflightBlockers -notcontains 'WORKSPACE_ALREADY_EXISTS') { throw 'Existing workspace blocker missing.' }
$r = Read-Report (Join-Path $workspace 'child')
if ($r.workspaceParentExists -or $r.sourceSyncPreflightBlockers -notcontains 'PARENT_MISSING_OR_INVALID' -or (Test-Path -LiteralPath $workspace)) { throw 'Missing parent blocker or unexpected write.' }
$r = Read-Report ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')))
if ($r.workspaceSeparateFromVeil -or $r.sourceSyncPreflightBlockers -notcontains 'WORKSPACE_OVERLAPS_VEIL') { throw 'VEIL checkout overlap blocker missing.' }
Write-Output 'PASS: read-only readiness, path shape, existing root, missing parent, VEIL overlap; no sync approval.'
