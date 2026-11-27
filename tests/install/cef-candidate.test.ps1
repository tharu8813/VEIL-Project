# SPDX-License-Identifier: MPL-2.0
$ErrorActionPreference = 'Stop'
$checker = Join-Path $PSScriptRoot '..\..\scripts\check-cef-candidate.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ('veil-cef-candidate-test-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'candidate'
[void][IO.Directory]::CreateDirectory($root)
try {
    foreach ($name in @('veil-cef-product.exe','veil-cef-product.dll','libcef.dll','icudtl.dat')) {
        [IO.File]::WriteAllText((Join-Path $root $name),'synthetic')
    }
    $inventory = Join-Path $work 'inventory.json'
    & $checker -CandidateRoot $root -InventoryPath $inventory
    $result = Get-Content -LiteralPath $inventory -Raw | ConvertFrom-Json
    if ($result.kind -cne 'cef-build-candidate-only' -or $result.installable -ne $false -or
        $result.startupEnabled -ne $false -or @($result.files).Count -ne 4) {
        throw 'Invalid candidate inventory.'
    }
    function Reject([string]$Name) {
        $path = Join-Path $root $Name
        [IO.File]::WriteAllText($path,'synthetic')
        $rejected = $false
        try { & $checker -CandidateRoot $root -InventoryPath (Join-Path $work 'bad.json') }
        catch { $rejected = $true }
        finally { Remove-Item -LiteralPath $path -Force }
        if (-not $rejected -or (Test-Path -LiteralPath (Join-Path $work 'bad.json'))) {
            throw 'Unexpected candidate accepted.'
        }
    }
    Reject 'veil-cef-smoke.dll'
    Reject 'veil-cef-wpf.dll'
    Reject 'Veil.Cef.WpfSmoke.dll'
    Reject 'veil-cef-registration-test.exe'
    Reject 'other.exe'
    Reject 'other.ps1'
    Remove-Item -LiteralPath (Join-Path $root 'libcef.dll')
    $rejected=$false
    try { & $checker -CandidateRoot $root -InventoryPath (Join-Path $work 'missing.json') }
    catch { $rejected=$true }
    if (-not $rejected) { throw 'Missing runtime accepted.' }
    Write-Host 'PASS: synthetic CEF candidate inventory refusals'
}
finally { Remove-Item -LiteralPath $work -Recurse -Force }
