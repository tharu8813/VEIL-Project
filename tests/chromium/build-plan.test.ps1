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

# Real small Git checkout exercises the same read-only helper as Assert-Pins.
. (Join-Path $PSScriptRoot '..\..\scripts\chromium\Assert-BuildIndex.ps1')
$fixture=Join-Path $env:RUNNER_TEMP ('veil-build-index-'+[Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $fixture) { throw 'Index fixture already exists.' }
$null=New-Item -ItemType Directory -Path $fixture
function Invoke-FixtureGit([string[]]$Arguments) {
    & git -C $fixture @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git command failed.' }
}
try {
    Invoke-FixtureGit @('init','-q')
    $file=Join-Path $fixture 'ordinary file.txt'
    [IO.File]::WriteAllText($file,'original')
    Invoke-FixtureGit @('add','--','ordinary file.txt')
    Invoke-FixtureGit @('-c','user.name=VEIL Fixture','-c','user.email=fixture@example.invalid','commit','-qm','fixture')
    Assert-VeilOrdinaryBuildIndex -Checkout $fixture
    foreach ($flag in @('assume-unchanged','skip-worktree')) {
        Invoke-FixtureGit @('update-index',("--"+$flag),'--','ordinary file.txt')
        [IO.File]::WriteAllText($file,'changed')
        $status=@(& git -C $fixture status --porcelain --untracked-files=no)
        if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'Hidden modification fixture not hidden.' }
        $before=@(& git -C $fixture ls-files -v)
        $rejected=$false
        try { Assert-VeilOrdinaryBuildIndex -Checkout $fixture } catch {
            $rejected=$_.Exception.Message -like '*Non-ordinary build index entry refused*'
        }
        $after=@(& git -C $fixture ls-files -v)
        if (-not $rejected -or ($before -join [char]10) -cne ($after -join [char]10) -or
            [IO.File]::ReadAllText($file) -cne 'changed') { throw "Failed read-only refusal: $flag" }
        Invoke-FixtureGit @('update-index',("--no-"+$flag),'--','ordinary file.txt')
        [IO.File]::WriteAllText($file,'original')
        Assert-VeilOrdinaryBuildIndex -Checkout $fixture
    }
    Write-Output 'PASS: ordinary Git index accepted, hidden assume-unchanged and skip-worktree modifications refused without mutation.'
} finally {
    # Only the random fixture created above; never an actual source checkout.
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
