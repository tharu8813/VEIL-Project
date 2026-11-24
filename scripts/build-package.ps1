# SPDX-License-Identifier: MPL-2.0
#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SignerThumbprint,
    [Parameter(Mandatory)][string]$SignTool,
    [Parameter(Mandatory)][ValidatePattern('^https://')][string]$TimestampUrl
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'package-common.ps1')
[void](Get-VeilVersion $Version)
if ((Get-VeilVersion $Version).Major -ne 0) { throw 'Only internal 0.x packages may be built before release qualification.' }
$repo = Split-Path $PSScriptRoot -Parent
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $out) { throw 'Use a new output directory.' }
$cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SignerThumbprint"
if (-not $cert.HasPrivateKey) { throw 'Signing certificate private key unavailable.' }
Push-Location $repo
try {
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Unable to identify source commit.' }
    if (@(git status --porcelain).Count -ne 0 -or $LASTEXITCODE -ne 0) { throw 'Build from a clean committed source tree.' }
    cargo build --locked --release --manifest-path crates/veil-security/Cargo.toml --bin veil-vault
    if ($LASTEXITCODE -ne 0) { throw 'Rust build failed.' }
    [void][IO.Directory]::CreateDirectory($out)
    foreach ($project in @(
        @{ File='apps/desktop/Veil.Desktop.csproj'; Folder='Desktop' },
        @{ File='apps/broker/Veil.VolumeBroker/Veil.VolumeBroker.csproj'; Folder='Broker' }
    )) {
        dotnet restore $project.File --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
        dotnet publish $project.File -c Release --no-restore --self-contained false -o (Join-Path $out $project.Folder) "-p:Version=$Version" "-p:FileVersion=$Version.0" "-p:InformationalVersion=$Version+$commit"
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    }
    Copy-Item -LiteralPath 'crates/veil-security/target/release/veil-vault.exe' -Destination (Join-Path $out 'Desktop\veil-vault.exe') -Force
    foreach ($image in @('Desktop\Veil.Desktop.exe','Desktop\veil-vault.exe','Broker\Veil.VolumeBroker.exe')) {
        $path = Join-Path $out $image
        & $SignTool sign /sha1 $SignerThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $path
        if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
        Assert-VeilSignedImage $path $SignerThumbprint
    }
    foreach ($notice in @('LICENSE','NOTICE')) {
        Copy-Item -LiteralPath $notice -Destination (Join-Path $out "Desktop\$notice")
    }
    $files = @(Get-VeilTreeFiles $out | ForEach-Object {
        $name = $_.FullName.Substring($out.TrimEnd('\').Length+1).Replace('\','/')
        Assert-VeilRelativeFile $name
        @{path=$name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    } | Sort-Object { $_.path })
    $manifest = @{format=1; channel='internal'; architecture='win-x64'; version=$Version; sourceCommit=$commit; profileFormat=1; files=$files}
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 6))
    [IO.File]::WriteAllBytes((Join-Path $out 'manifest.json'),$bytes)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $cms.ComputeSignature($signer)
    [IO.File]::WriteAllBytes((Join-Path $out 'manifest.p7s'),$cms.Encode())
    Write-Host 'Internal package built. Release approval and runtime verification are still required.'
}
finally { Pop-Location }
