# SPDX-License-Identifier: MPL-2.0
# Only stages a non-installable candidate. No service, installed tree,
# registry, broker configuration or user profile mutations.
. (Join-Path $PSScriptRoot 'cef-candidate-evidence.ps1')

function Copy-VeilCefVerifiedCandidate {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Evidence,
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$Thumbprint,
        [Parameter(Mandatory)][string]$ExpectedCommit
    )
    $stagePath = [IO.Path]::GetFullPath($Stage).TrimEnd('\')
    $parent = Split-Path -Parent $stagePath
    $parentItem = Get-Item -LiteralPath $parent -Force
    if (-not $parentItem.PSIsContainer) { throw 'Invalid staging parent.' }
    Assert-VeilAdminPath $parent
    foreach ($inputRoot in @($Source,$Evidence)) {
        $inputPath = (Get-Item -LiteralPath $inputRoot -Force).FullName.TrimEnd('\')
        if ($inputPath -eq $stagePath -or
            $stagePath.StartsWith($inputPath + '\',[StringComparison]::OrdinalIgnoreCase) -or
            $inputPath.StartsWith($stagePath + '\',[StringComparison]::OrdinalIgnoreCase)) {
            throw 'Candidate inputs must be separate from staging.'
        }
    }
    if (Test-Path -LiteralPath $stagePath) { throw 'Staging already exists; preserve it for administrator inspection.' }
    $manifest = Read-VeilCefCandidateEvidence $Source $Evidence $Thumbprint $ExpectedCommit
    # Only create the directory after the complete signed source passes.
    # Failure after creation preserves partial staging, never auto-deletes
    # potentially ambiguous evidence or touches the existing installation.
    New-VeilRestrictedDirectory $stagePath
    $payload = Join-Path $stagePath 'candidate'
    $proof = Join-Path $stagePath 'evidence'
    New-VeilRestrictedDirectory $payload
    New-VeilRestrictedDirectory $proof
    # Enumerate actual source names again, not unchecked manifest paths.
    $files = @(Get-VeilCefRestrictedFiles $Source)
    foreach ($file in $files) {
        $relative = $file.path.Replace('/','\')
        $target = Join-Path $payload $relative
        $directory = Split-Path -Parent $target
        # All components are enumerated, constrained names from the inventory.
        [void][IO.Directory]::CreateDirectory($directory)
        Assert-VeilAdminPath $directory
        [IO.File]::Copy((Join-Path $Source $relative),$target,$false)
    }
    foreach ($name in @('cef-candidate.json','cef-candidate.p7s')) {
        [IO.File]::Copy((Join-Path $Evidence $name),(Join-Path $proof $name),$false)
    }
    # Revalidate the copied signature and bytes. Bind this copy to the exact
    # original signed manifest, not merely another signature by the same key.
    $copied = Read-VeilCefCandidateEvidence $payload $proof $Thumbprint $ExpectedCommit
    if (($manifest | ConvertTo-Json -Depth 8 -Compress) -cne ($copied | ConvertTo-Json -Depth 8 -Compress)) {
        throw 'Signed candidate manifest changed during staging.'
    }
    [void](Read-VeilCefCandidateEvidence $Source $Evidence $Thumbprint $ExpectedCommit)
    return $copied
}
