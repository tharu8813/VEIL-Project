# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$InventoryPath
)
$ErrorActionPreference = 'Stop'
$root = Get-Item -LiteralPath $CandidateRoot -Force
if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Invalid candidate root.'
}
$inventory = [IO.Path]::GetFullPath($InventoryPath)
$rootPrefix = $root.FullName.TrimEnd('\') + '\'
if ($inventory.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $inventory)) {
    throw 'Inventory must be a new file outside the candidate directory.'
}
$pending = [Collections.Generic.Stack[object]]::new()
$pending.Push(@{ FullName=$root.FullName; Relative='' })
$seen = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
while ($pending.Count -gt 0) {
    $current = $pending.Pop()
    foreach ($item in Get-ChildItem -LiteralPath $current.FullName -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Candidate link refused.' }
        if ($item.Name -notmatch '\A[A-Za-z0-9_.-]+\z' -or $item.Name.EndsWith('.') -or
            $item.Name.Split('.')[0] -match '\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z') {
            throw 'Invalid candidate entry name.'
        }
        $relative = if ($current.Relative) { $current.Relative + '/' + $item.Name } else { $item.Name }
        if ($relative.Length -gt 240 -or $relative -match '(^|/)(veil-cef-smoke|veil-cef-wpf|Veil\.Cef\.WpfSmoke|veil-cef-.*-test)([./-]|$)') {
            throw 'Development fixture refused in product candidate.'
        }
        if ($item.PSIsContainer) {
            $pending.Push(@{ FullName=$item.FullName; Relative=$relative })
            continue
        }
        if ($item.Extension -eq '.exe' -and $relative -cne 'veil-cef-product.exe') {
            throw 'Unexpected executable in product candidate.'
        }
        if ($item.Extension -notin @('.exe','.dll','.pak','.dat','.bin','.json','.txt')) {
            throw 'Unexpected file type in product candidate.'
        }
        if ($item.Length -le 0 -or $seen.ContainsKey($relative)) { throw 'Empty or duplicate candidate file.' }
        $seen.Add($relative,[pscustomobject]@{
            path=$relative; bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        })
    }
}
foreach ($required in @('veil-cef-product.exe','veil-cef-product.dll','libcef.dll','icudtl.dat')) {
    if (-not $seen.ContainsKey($required)) { throw 'Required candidate binary or resource missing.' }
}
# A development inventory must not be confused with the signed package manifest.
$result = [ordered]@{ kind='cef-build-candidate-only'; installable=$false; startupEnabled=$false; files=@($seen.Values | Sort-Object path) }
$json = $result | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($inventory,$json,[Text.UTF8Encoding]::new($false))
Write-Host "PASS: isolated CEF candidate inventory ($($seen.Count) files). NOT a signed package."
