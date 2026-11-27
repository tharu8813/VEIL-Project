# SPDX-License-Identifier: MPL-2.0
# Detached signed evidence for a non-launchable candidate, NOT an installer.
. (Join-Path $PSScriptRoot 'package-common.ps1')
. (Join-Path $PSScriptRoot 'cef-candidate-common.ps1')

function Get-VeilCefRestrictedFiles([string]$Root) {
    # All entries including directories must meet the existing admin ACL policy.
    Assert-VeilAdminPath $Root
    [void]@(Get-VeilTreeFiles $Root -Restricted)
    return @(Get-VeilCefCandidateFiles $Root)
}

function Assert-VeilCefEvidenceLocation([string]$Root,[string]$Evidence) {
    $rootPath = (Get-Item -LiteralPath $Root -Force).FullName.TrimEnd('\')
    $evidencePath = (Get-Item -LiteralPath $Evidence -Force).FullName.TrimEnd('\')
    if ($evidencePath -eq $rootPath -or $evidencePath.StartsWith($rootPath + '\',[StringComparison]::OrdinalIgnoreCase) -or
        $rootPath.StartsWith($evidencePath + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Candidate and evidence must be separate directories.'
    }
    Assert-VeilAdminPath $Evidence
}

function Assert-VeilCefCandidateSigner([string]$Root,[string]$Thumbprint) {
    foreach ($name in @('veil-cef-product.exe','veil-cef-product.dll')) {
        Assert-VeilSignedImage (Join-Path $Root $name) $Thumbprint
    }
}

function Write-VeilCefCandidateEvidence([string]$Root,[string]$Evidence,[string]$Commit,
    [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    Assert-VeilCefEvidenceLocation $Root $Evidence
    if (@(Get-ChildItem -LiteralPath $Evidence -Force).Count -ne 0) { throw 'Evidence directory must be empty.' }
    if ($Commit -cnotmatch '\A[0-9a-f]{40}\z' -or -not $Certificate.HasPrivateKey) { throw 'Invalid candidate signing inputs.' }
    Assert-VeilCefCandidateSigner $Root $Certificate.Thumbprint
    $files = @(Get-VeilCefRestrictedFiles $Root)
    $manifest = [ordered]@{
        kind='cef-signed-candidate-evidence'; format=1; architecture='win-x64'
        installable=$false; startupEnabled=$false; sourceCommit=$Commit
        cefArchiveSha256='1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a'
        files=$files
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 6))
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($Certificate)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $cms.ComputeSignature($signer)
    # CreateNew avoids silently overwriting an earlier signed evidence set.
    foreach ($record in @(@{Name='cef-candidate.json';Bytes=$bytes},@{Name='cef-candidate.p7s';Bytes=$cms.Encode()})) {
        $stream = [IO.File]::Open((Join-Path $Evidence $record.Name),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($record.Bytes,0,$record.Bytes.Length) } finally { $stream.Dispose() }
    }
}

function Read-VeilCefCandidateEvidence([string]$Root,[string]$Evidence,[string]$Thumbprint,[string]$ExpectedCommit) {
    if ($Thumbprint -notmatch '\A[0-9a-f]{40}\z' -or $ExpectedCommit -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Invalid independent trust input.' }
    Assert-VeilCefEvidenceLocation $Root $Evidence
    $manifestPath = Join-Path $Evidence 'cef-candidate.json'
    $signaturePath = Join-Path $Evidence 'cef-candidate.p7s'
    if (@(Get-ChildItem -LiteralPath $Evidence -Force).Count -ne 2) { throw 'Unexpected evidence entries.' }
    foreach ($path in @($manifestPath,$signaturePath)) {
        Assert-VeilAdminPath $path
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -or $item.Length -le 0 -or $item.Length -gt 4MB) { throw 'Invalid evidence file.' }
    }
    $bytes = [IO.File]::ReadAllBytes($manifestPath)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
    $cms.Decode([IO.File]::ReadAllBytes($signaturePath))
    if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].DigestAlgorithm.Value -ne '2.16.840.1.101.3.4.2.1') { throw 'Invalid CMS signer.' }
    $cms.CheckSignature($false)
    if ($cms.SignerInfos[0].Certificate.Thumbprint -ne $Thumbprint) { throw 'Candidate signer mismatch.' }
    $manifest = [Text.UTF8Encoding]::new($false,$true).GetString($bytes) | ConvertFrom-Json
    if ($manifest.kind -cne 'cef-signed-candidate-evidence' -or $manifest.format -isnot [int] -or $manifest.format -ne 1 -or
        $manifest.architecture -cne 'win-x64' -or $manifest.installable -isnot [bool] -or $manifest.installable -or
        $manifest.startupEnabled -isnot [bool] -or $manifest.startupEnabled -or
        $manifest.sourceCommit -cne $ExpectedCommit -or
        $manifest.cefArchiveSha256 -cne '1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a' -or
        $manifest.files -isnot [array]) { throw 'Unsupported candidate evidence contract.' }
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        if ($entry.path -isnot [string] -or $entry.sha256 -isnot [string] -or
            $entry.sha256 -notmatch '\A[0-9a-f]{64}\z' -or ($entry.bytes -isnot [int] -and $entry.bytes -isnot [long]) -or
            $entry.bytes -le 0 -or $expected.ContainsKey($entry.path)) { throw 'Invalid or duplicate candidate evidence entry.' }
        $expected.Add($entry.path,$entry)
    }
    $actual = @(Get-VeilCefRestrictedFiles $Root)
    if ($actual.Count -ne $expected.Count) { throw 'Candidate inventory count mismatch.' }
    foreach ($file in $actual) {
        if (-not $expected.ContainsKey($file.path)) { throw 'Unexpected candidate file.' }
        $entry = $expected[$file.path]
        if ($entry.path -cne $file.path -or $entry.bytes -ne $file.bytes -or $entry.sha256 -ne $file.sha256) {
            throw 'Candidate file hash, length or name mismatch.'
        }
    }
    Assert-VeilCefCandidateSigner $Root $Thumbprint
    return $manifest
}
