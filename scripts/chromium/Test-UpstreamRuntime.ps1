# SPDX-License-Identifier: MPL-2.0
# C1 synthetic local runtime smoke. Not a sandbox or privacy test.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Workspace,
    [Parameter(Mandatory=$true)][string]$Receipt,
    [ValidateSet('Debug','Release')][string]$Configuration='Debug',
    [switch]$Execute
)
$ErrorActionPreference='Stop'
if ($Workspace -notmatch '^[A-Za-z]:\\[A-Za-z0-9_.\\-]+$') { throw 'Use an absolute dedicated local Windows path without spaces.' }
$root=[IO.Path]::GetFullPath($Workspace).TrimEnd('\')
if ($root -eq [IO.Path]::GetPathRoot($root+'\').TrimEnd('\')) { throw 'Drive root is not a workspace.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
if ($root.Equals($repo,[StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($repo+'\',[StringComparison]::OrdinalIgnoreCase) -or $repo.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Workspace must be separate from VEIL checkout.' }
$receiptPath=[IO.Path]::GetFullPath($Receipt)
$expectedReceiptDir=Split-Path -Parent $receiptPath
if ((Split-Path -Leaf $receiptPath) -cne 'receipt.json' -or (Split-Path -Parent $expectedReceiptDir) -ine $root -or (Split-Path -Leaf $expectedReceiptDir) -notmatch "^veil-build-$Configuration-[a-f0-9]{32}$") { throw 'Expected build receipt in the dedicated workspace.' }
$pinJson=& node (Join-Path $PSScriptRoot 'verify-candidate.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Candidate validation failed.' }
$pin=$pinJson | ConvertFrom-Json
$exe=Join-Path $root "chromium\src\out\Veil$Configuration\chrome.exe"
$plan=[ordered]@{status='PLAN_ONLY'; configuration=$Configuration; executable=$exe; receipt=$receiptPath; chromiumRevision=$pin.revision; runtime='NOT RUN'; sandbox='NOT RUN'; siteIsolation='NOT RUN'; releaseApproved=$false}
if (-not $Execute) { $plan | ConvertTo-Json; return }

function Assert-OrdinaryPath([string]$Path,[bool]$IsDirectory) {
    $item=Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -ne $IsDirectory) { throw 'Unexpected path type.' }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point refused.' }
    if ($IsDirectory) { $parent=$item.Parent } else { $parent=$item.Directory }
    for ($p=$parent; $null -ne $p; $p=$p.Parent) {
        if (($p.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point refused.' }
    }
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal=[Security.Principal.WindowsPrincipal]::new($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Do not run Chromium smoke elevated.' }
} finally { $identity.Dispose() }
if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'Native Windows x64 process required.' }
Assert-OrdinaryPath $root $true
Assert-OrdinaryPath $receiptPath $false
Assert-OrdinaryPath $exe $false
$receiptData=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receiptData.status -cne 'BUILD_OUTPUT_OBSERVED' -or $receiptData.configuration -cne $Configuration -or $receiptData.chromiumRevision -cne $pin.revision -or $receiptData.depotToolsRevision -cne $pin.depotToolsRevision -or $receiptData.executable -ine $exe -or $receiptData.build -cne 'EXIT_0' -or $receiptData.releaseApproved -isnot [bool] -or $receiptData.releaseApproved -ne $false -or $receiptData.executableSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Build receipt does not match expected candidate.' }
$hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
if ($hash -ine $receiptData.executableSha256) { throw 'Executable hash mismatch.' }
# A new directory, synthetic data only. No reuse of any real browser profile.
$run=Join-Path $root ('veil-smoke-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $run
$profile=Join-Path $run 'profile'
$fixture=Join-Path $run 'fixture.html'
$marker=[Guid]::NewGuid().ToString('N')
$a=$marker.Substring(0,16); $b=$marker.Substring(16)
$html='<!doctype html><html><head><title>C1 synthetic smoke</title></head><body><p id="result">NOT_EXECUTED</p><script>document.getElementById("result").textContent="'+$a+'"+"'+$b+'";</script></body></html>'
[IO.File]::WriteAllText($fixture,$html)
$url=([Uri]::new($fixture)).AbsoluteUri
$stdout=Join-Path $run 'stdout.log'
$stderr=Join-Path $run 'stderr.log'
$flags=@('--headless','--dump-dom','--no-first-run','--no-default-browser-check',"--user-data-dir=$profile",$url)
$process=$null
try {
    $process=Start-Process -FilePath $exe -ArgumentList $flags -WorkingDirectory (Split-Path -Parent $exe) -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    if (-not $process.WaitForExit(60000)) {
        if (-not $process.HasExited) {
            & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F | Out-Null
        }
        throw "Chromium smoke timed out; preserve logs in $run"
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Chromium smoke exited $($process.ExitCode); see $run" }
    $dom=Get-Content -LiteralPath $stdout -Raw
    if ($dom -notmatch ('<p id="result">'+[regex]::Escape($marker)+'</p>')) { throw 'Expected JavaScript-rendered marker missing.' }
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ine $hash) { throw 'Executable changed during smoke.' }
    $evidence=[ordered]@{status='SYNTHETIC_DOM_OBSERVED'; chromiumRevision=$pin.revision; depotToolsRevision=$pin.depotToolsRevision; configuration=$Configuration; executableSha256=$hash; buildReceiptSha256=(Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash; runtime='DOM_AND_JS_OBSERVED'; network='NOT VERIFIED'; processCleanup='NOT VERIFIED'; sandbox='NOT RUN'; siteIsolation='NOT RUN'; releaseApproved=$false; logs=$run}
    $json=$evidence | ConvertTo-Json
    $json | Set-Content -LiteralPath (Join-Path $run 'smoke.json') -Encoding UTF8
    $json
} finally { if ($null -ne $process) { $process.Dispose() } }
