# SPDX-License-Identifier: MPL-2.0
# Shared maintenance functions. Dot-source only from a trusted administrator-owned script directory.
Add-Type -AssemblyName System.Security
$script:VeilTrusted = @('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

function Assert-VeilAdminPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point refused.' }
    $acl = Get-Acl -LiteralPath $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $script:VeilTrusted) { throw 'Untrusted owner.' }
    $rights = [Security.AccessControl.FileSystemRights]'Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $rights) -and
            $rule.IdentityReference.Value -notin $script:VeilTrusted) { throw 'Untrusted write access.' }
    }
}

function New-VeilRestrictedDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) { throw 'Destination already exists.' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetSecurityDescriptorSddlForm('O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)')
    # Create with the restricted ACL, not a subsequent permissive ACL window.
    [void][IO.Directory]::CreateDirectory($Path,$acl)
    Assert-VeilAdminPath $Path
}

function Get-VeilTreeFiles([string]$Root, [switch]$Restricted, [switch]$WithRelativePath) {
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid tree root.' }
    if ($Restricted) { Assert-VeilAdminPath $Root }
    # Build relative names from enumerated components, not the length of the caller's root.
    # Windows may expand an 8.3 short alias in child FullName while preserving it in Root.
    $pending = [Collections.Generic.Stack[object]]::new()
    $pending.Push(@{ FullName=$rootItem.FullName; Relative='' })
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $current.FullName -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package/tree contains a link.' }
            if ($Restricted) { Assert-VeilAdminPath $item.FullName }
            $relative = if ($current.Relative) { $current.Relative + '/' + $item.Name } else { $item.Name }
            if ($item.PSIsContainer) { $pending.Push(@{ FullName=$item.FullName; Relative=$relative }) }
            elseif ($WithRelativePath) { [pscustomobject]@{ File=$item; Relative=$relative } }
            else { $item }
        }
    }
}

function Assert-VeilRelativeFile([string]$Name) {
    if ($Name.Length -gt 240 -or $Name -notmatch '^(Desktop|Broker)/[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$') { throw 'Invalid package relative path.' }
    foreach ($part in $Name.Split('/')) {
        if ($part -in @('.','..') -or $part.EndsWith('.') -or
            $part.Split('.')[0] -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') { throw 'Unsafe package path.' }
    }
}

# Shared byte-copy operation. The production caller creates and owns restricted staging,
# validates the signed inventory after copying, and removes failed staging.
function Copy-VeilPackageFiles([string]$Source, [string]$Destination) {
    $targetRoot = Get-Item -LiteralPath $Destination -Force
    if (-not $targetRoot.PSIsContainer -or ($targetRoot.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        @(Get-ChildItem -LiteralPath $Destination -Force).Count -ne 0) { throw 'Expected empty non-redirected staging directory.' }
    foreach ($record in @(Get-VeilTreeFiles $Source -WithRelativePath)) {
        $relative = $record.Relative
        if ($relative -notin @('manifest.json','manifest.p7s')) { Assert-VeilRelativeFile $relative }
        $target = Join-Path $Destination ($relative.Replace('/','\'))
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        # Copy only bytes into staging, not source ACLs.
        [IO.File]::Copy($record.File.FullName,$target,$false)
    }
}

function Get-VeilVersion([string]$Version) {
    # ADR-0010: SemVer MAJOR.MINOR.PATCH with an optional alpha.N, beta.N or rc.N prerelease (N 1-99).
    # Returns a four-part version that is both the Windows file version and the update order:
    # alpha.N = N, beta.N = 100+N, rc.N = 200+N, release = 1000, so alpha.1 < beta.1 < rc.1 < release.
    if ($Version -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})(?:-(alpha|beta|rc)\.([1-9][0-9]?))?$') {
        throw 'Expected MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-(alpha|beta|rc).N with N 1-99.'
    }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
    if ($major -gt 65535 -or $minor -gt 65535 -or $patch -gt 65535) { throw 'Version exceeds Windows version range.' }
    $order = 1000
    if ($Matches[4]) { $order = @{ alpha = 0; beta = 100; rc = 200 }[$Matches[4]] + [int]$Matches[5] }
    return [version]::new($major, $minor, $patch, $order)
}

function Assert-VeilInternalVersion([string]$Version) {
    # Internal channel: 0.x developer packages or 1.x+ prereleases. A stable 1.x+ release needs separate
    # release qualification (ADR-0010) and is refused here.
    $value = Get-VeilVersion $Version
    if ($value.Major -ne 0 -and $value.Revision -eq 1000) { throw 'Stable releases are not enabled; use an alpha, beta or rc prerelease on the internal channel.' }
    return $value
}

function Write-VeilPackageManifest([string]$Root,[string]$Version,[string]$Commit,[Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    # Inventory every file (SHA-256 and length) and sign the manifest bytes with a detached CMS signature.
    # Shared by build-package.ps1 and the offline VM package test so both use one manifest format.
    [void](Assert-VeilInternalVersion $Version)
    $Root = (Get-Item -LiteralPath $Root -Force).FullName
    if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'Expected a 40-hex source commit.' }
    $files = @(Get-VeilTreeFiles $Root -WithRelativePath | ForEach-Object {
        $name = $_.Relative
        Assert-VeilRelativeFile $name
        @{path=$name; bytes=$_.File.Length; sha256=(Get-FileHash -LiteralPath $_.File.FullName -Algorithm SHA256).Hash}
    } | Sort-Object { $_.path })
    $manifest = @{format=1; channel='internal'; architecture='win-x64'; version=$Version; sourceCommit=$Commit; profileFormat=1; files=$files}
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 6))
    [IO.File]::WriteAllBytes((Join-Path $Root 'manifest.json'),$bytes)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($Certificate)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $cms.ComputeSignature($signer)
    [IO.File]::WriteAllBytes((Join-Path $Root 'manifest.p7s'),$cms.Encode())
}

function Assert-VeilSignedImage([string]$Path,[string]$Thumbprint) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) { throw 'VEIL image signature mismatch.' }
}

function Read-VeilPackage([string]$Root,[string]$Thumbprint) {
    # Called on an administrator-owned staged copy, never execute package-supplied scripts.
    $rootItem = Get-Item -LiteralPath $Root -Force
    $Root = $rootItem.FullName
    $files = @(Get-VeilTreeFiles $Root -Restricted -WithRelativePath)
    $manifestPath = Join-Path $Root 'manifest.json'
    $signaturePath = Join-Path $Root 'manifest.p7s'
    foreach ($path in @($manifestPath,$signaturePath)) {
        Assert-VeilAdminPath $path
        if ((Get-Item -LiteralPath $path).Length -gt 4MB) { throw 'Oversized manifest.' }
    }
    $bytes = [IO.File]::ReadAllBytes($manifestPath)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
    $cms.Decode([IO.File]::ReadAllBytes($signaturePath))
    if ($cms.SignerInfos.Count -ne 1) { throw 'Expected exactly one manifest signer.' }
    $cms.CheckSignature($false)
    if ($cms.SignerInfos[0].Certificate.Thumbprint -ne $Thumbprint) { throw 'Manifest signer mismatch.' }
    $manifest = [Text.UTF8Encoding]::new($false,$true).GetString($bytes) | ConvertFrom-Json
    if ($manifest.format -ne 1 -or $manifest.channel -cne 'internal' -or $manifest.architecture -cne 'win-x64' -or
        $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$' -or $manifest.profileFormat -ne 1) { throw 'Unsupported package contract.' }
    [void](Get-VeilVersion $manifest.version)
    $expected = @{}
    foreach ($entry in @($manifest.files)) {
        $name = [string]$entry.path
        Assert-VeilRelativeFile $name
        if ($expected.ContainsKey($name) -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            ($entry.bytes -isnot [int] -and $entry.bytes -isnot [long]) -or $entry.bytes -lt 0) { throw 'Invalid or duplicate manifest entry.' }
        $expected.Add($name,$entry)
    }
    foreach ($required in @('Desktop/Veil.Desktop.exe','Desktop/Veil.Desktop.dll','Desktop/veil-vault.exe',
        'Broker/Veil.VolumeBroker.exe','Broker/Veil.VolumeBroker.dll')) {
        if (-not $expected.ContainsKey($required)) { throw 'Required binary missing from manifest.' }
    }
    $actual = @{}
    foreach ($record in $files) {
        $file = $record.File
        $name = $record.Relative
        if ($name -in @('manifest.json','manifest.p7s')) { continue }
        Assert-VeilRelativeFile $name
        if ($actual.ContainsKey($name) -or -not $expected.ContainsKey($name)) { throw 'Unexpected package file.' }
        $entry = $expected[$name]
        if ($entry.bytes -ne $file.Length -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Package hash or length mismatch.' }
        $actual.Add($name,$true)
    }
    if ($actual.Count -ne $expected.Count) { throw 'Package file missing.' }
    foreach ($image in @('Desktop\Veil.Desktop.exe','Desktop\veil-vault.exe','Broker\Veil.VolumeBroker.exe')) {
        Assert-VeilSignedImage (Join-Path $Root $image) $Thumbprint
    }
    return $manifest
}
