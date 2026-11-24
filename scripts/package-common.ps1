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

function Get-VeilTreeFiles([string]$Root, [switch]$Restricted) {
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid tree root.' }
    if ($Restricted) { Assert-VeilAdminPath $Root }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($rootItem.FullName)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package/tree contains a link.' }
            if ($Restricted) { Assert-VeilAdminPath $item.FullName }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) } else { $item }
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

function Get-VeilVersion([string]$Version) {
    # First maintenance implementation accepts numeric stable-format internal versions only.
    # No prerelease precedence or downgrade is inferred from Windows file-version metadata.
    if ($Version -notmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') { throw 'Expected numeric MAJOR.MINOR.PATCH.' }
    $value = [version]$Version
    if ($value.Major -gt 65535 -or $value.Minor -gt 65535 -or $value.Build -gt 65535) { throw 'Version exceeds Windows version range.' }
    return $value
}

function Assert-VeilSignedImage([string]$Path,[string]$Thumbprint) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) { throw 'VEIL image signature mismatch.' }
}

function Read-VeilPackage([string]$Root,[string]$Thumbprint) {
    # Called on an administrator-owned staged copy, never execute package-supplied scripts.
    $rootItem = Get-Item -LiteralPath $Root -Force
    $files = @(Get-VeilTreeFiles $rootItem.FullName -Restricted)
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
            $entry.bytes -isnot [ValueType] -or $entry.bytes -lt 0) { throw 'Invalid or duplicate manifest entry.' }
        $expected.Add($name,$entry)
    }
    foreach ($required in @('Desktop/Veil.Desktop.exe','Desktop/Veil.Desktop.dll','Desktop/veil-vault.exe',
        'Broker/Veil.VolumeBroker.exe','Broker/Veil.VolumeBroker.dll')) {
        if (-not $expected.ContainsKey($required)) { throw 'Required binary missing from manifest.' }
    }
    $actual = @{}
    foreach ($file in $files) {
        $name = $file.FullName.Substring($rootItem.FullName.TrimEnd('\').Length + 1).Replace('\','/')
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
