// SPDX-License-Identifier: MPL-2.0
// Write-VeilPackageManifest and Read-VeilPackage (scripts/package-common.ps1) in Windows PowerShell 5.1:
// a synthetic package tree is signed with an in-memory certificate (no certificate store is touched), then
// one change per case must be refused. Authenticode trust and administrator-owned ACLs need an elevated VM
// with a trusted test certificate, so those checks (and the manifest certificate chain) are relaxed here and
// run unmodified in tests/vm/guest-package.ps1.
//   node --test tests/install/package-manifest.test.mjs
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';

const run = promisify(execFile);
const common = resolve('scripts/package-common.ps1');

const script = (root) => `
$ErrorActionPreference = 'Stop'
# A Windows PowerShell 5.1 child launched from pwsh may inherit a module search path
# that does not expose the inbox Utility module (Get-FileHash is a module function).
# Load the native module explicitly; do not replace or mock package hashing.
Import-Module (Join-Path $PSHOME 'Modules\\Microsoft.PowerShell.Utility\\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
$null = Get-Command Get-FileHash -ErrorAction Stop
# Loaded with one line changed: the manifest signature is verified without chain trust (no host trust
# store change). The unmodified chain check runs in tests/vm/guest-package.ps1.
$source = [IO.File]::ReadAllText('${common.replace(/'/g, "''")}')
if (-not $source.Contains('$cms.CheckSignature($false)')) { throw 'package-common.ps1 changed: update this test.' }
. ([scriptblock]::Create($source.Replace('$cms.CheckSignature($false)', '$cms.CheckSignature($true)')))
# Replaced for this host test only (see the file header).
function Assert-VeilAdminPath([string]$Path) { }
function Assert-VeilSignedImage([string]$Path, [string]$Thumbprint) { }
$rsa = [Security.Cryptography.RSA]::Create(2048)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL host manifest test', $rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$cert = $request.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now.AddHours(1))
$other = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VEIL other signer', [Security.Cryptography.RSA]::Create(2048),
    [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1).CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now.AddHours(1))
$commit = 'a' * 40
function New-Tree([string]$Path) {
    foreach ($f in 'Desktop/Veil.Desktop.exe', 'Desktop/Veil.Desktop.dll', 'Desktop/veil-vault.exe', 'Desktop/runtimes/win-x64/native/WebView2Loader.dll',
                   'Broker/Veil.VolumeBroker.exe', 'Broker/Veil.VolumeBroker.dll') {
        $target = Join-Path $Path $f
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        [IO.File]::WriteAllText($target, "synthetic $f")
    }
}
function Edit-SignedTestManifest([string]$Root, [scriptblock]$Change) {
    $path = Join-Path $Root 'manifest.json'
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    & $Change $manifest
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllBytes($path, $bytes)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes), $true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $cms.ComputeSignature($signer)
    [IO.File]::WriteAllBytes((Join-Path $Root 'manifest.p7s'), $cms.Encode())
}
$cases = [ordered]@{
    'valid'           = @{ change = {}; expect = $null }
    'relative-root'   = @{ change = {}; expect = $null; relative = $true }
    'trailing-root'   = @{ change = {}; expect = $null; trailing = $true }
    'unsafe-name'     = @{ change = { param($r) [IO.File]::WriteAllText((Join-Path $r 'Desktop/invalid name.dll'), 'x') }; expect = 'Invalid package relative path' }
    'modified-file'   = @{ change = { param($r) [IO.File]::AppendAllText((Join-Path $r 'Desktop/Veil.Desktop.dll'), 'x') }; expect = 'hash or length' }
    'extra-file'      = @{ change = { param($r) [IO.File]::WriteAllText((Join-Path $r 'Desktop/extra.dll'), 'x') }; expect = 'Unexpected package file' }
    'missing-file'    = @{ change = { param($r) Remove-Item (Join-Path $r 'Broker/Veil.VolumeBroker.dll') }; expect = 'missing' }
    'edited-manifest' = @{ change = { param($r) $m = Join-Path $r 'manifest.json'; [IO.File]::WriteAllText($m, ([IO.File]::ReadAllText($m) -replace 'alpha\\.1', 'alpha.9')) }; expect = '.' }
    'wrong-signer'    = @{ change = {}; thumbprint = $other.Thumbprint; expect = 'signer mismatch' }
    'other-signature' = @{ change = { param($r) Remove-Item (Join-Path $r 'manifest.p7s'), (Join-Path $r 'manifest.json'); Write-VeilPackageManifest $r '1.0.0-alpha.1' $commit $other }; expect = 'signer mismatch' }
    'bad-version'     = @{ change = { param($r) Remove-Item (Join-Path $r 'manifest.p7s'), (Join-Path $r 'manifest.json'); Write-VeilPackageManifest $r '1.0.0' $commit $cert }; expect = 'Stable releases' }
    'signed-duplicate-path' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files = @($m.files) + @($m.files[0]) } }; expect = 'Invalid or duplicate manifest entry' }
    'signed-unsafe-path' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files[0].path = 'Desktop/../outside.dll' } }; expect = 'Unsafe package path' }
    'signed-missing-required' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files = @($m.files | Where-Object path -ne 'Desktop/veil-vault.exe') } }; expect = 'Required binary missing' }
    'signed-boolean-length' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files[0].bytes = $true } }; expect = 'Invalid or duplicate manifest entry' }
    'signed-fractional-length' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files[0].bytes = 0.5 } }; expect = 'Invalid or duplicate manifest entry' }
    'signed-bad-hash' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.files[0].sha256 = 'not-a-hash' } }; expect = 'Invalid or duplicate manifest entry' }
    'signed-profile-format' = @{ change = { param($r) Edit-SignedTestManifest $r { param($m) $m.profileFormat = 2 } }; expect = 'Unsupported package contract' }
    'bad-path'        = @{ change = { param($r) Assert-VeilRelativeFile 'Desktop/CON.txt' }; expect = 'Unsafe package path' }
}
$results = [ordered]@{}
foreach ($name in $cases.Keys) {
    $root = Join-Path '${root.replace(/'/g, "''")}' $name
    New-Tree $root
    $case = $cases[$name]
    # Alternate root spellings must yield the same manifest names. The hosted runner also
    # supplies an 8.3 TEMP alias on some images, which exercises the original failure.
    $manifestRoot = $root
    if ($case.trailing) { $manifestRoot += [IO.Path]::DirectorySeparatorChar }
    if ($case.relative) { Push-Location (Split-Path -Parent $root); $manifestRoot = '.\\' + (Split-Path -Leaf $root) }
    try {
        Write-VeilPackageManifest $manifestRoot '1.0.0-alpha.1' $commit $cert
        $written = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
        $names = @($written.files | ForEach-Object path | Sort-Object)
        $expectedNames = @('Broker/Veil.VolumeBroker.dll','Broker/Veil.VolumeBroker.exe','Desktop/Veil.Desktop.dll','Desktop/Veil.Desktop.exe','Desktop/veil-vault.exe','Desktop/runtimes/win-x64/native/WebView2Loader.dll' | Sort-Object)
        if (($names -join '|') -cne ($expectedNames -join '|')) { throw 'Incorrect relative manifest names.' }
        [void](Read-VeilPackage $manifestRoot $cert.Thumbprint)
        if ($name -eq 'valid') {
            $destination = Join-Path (Split-Path -Parent $root) 'copy-validation'
            [void][IO.Directory]::CreateDirectory($destination)
            Copy-VeilPackageFiles $manifestRoot $destination
            [void](Read-VeilPackage $destination $cert.Thumbprint)
            $refused = $false
            try { Copy-VeilPackageFiles $manifestRoot $destination } catch {
                $refused = $_.Exception.Message -match 'Expected empty non-redirected staging directory'
            }
            if (-not $refused) { throw 'Nonempty staging directory was not refused.' }
        }
    } finally { if ($case.relative) { Pop-Location } }
    $message = $null
    try { & $case.change $root } catch { $message = $_.Exception.Message }
    $thumbprint = if ($case.thumbprint) { $case.thumbprint } else { $cert.Thumbprint }
    if (-not $message) {
        try { $manifest = Read-VeilPackage $root $thumbprint; if ($manifest.version -ne '1.0.0-alpha.1') { $message = 'wrong version' } } catch { $message = $_.Exception.Message }
    }
    $results[$name] = if ($null -eq $case.expect) { if ($message) { "FAIL: $message" } else { 'ok' } }
        elseif ($message -and $message -match $case.expect) { 'ok' } else { "FAIL: $message" }
}
$results | ConvertTo-Json`;

test('package manifests are exact, authenticated and versioned', { skip: process.platform !== 'win32' }, async () => {
    const root = await mkdtemp(join(tmpdir(), 'veil-manifest-'));
    try {
        const { stdout } = await run('powershell', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script(root)],
            { maxBuffer: 4 * 1024 * 1024 });
        const results = JSON.parse(stdout);
        assert.equal(Object.keys(results).length, 19);
        for (const [name, result] of Object.entries(results)) assert.equal(result, 'ok', name);
    } finally {
        await rm(root, { recursive: true, force: true });
    }
});
