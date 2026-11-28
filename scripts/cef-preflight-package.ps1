# SPDX-License-Identifier: MPL-2.0
# Separate non-installable VM validation package. No service or registry writes.
. (Join-Path $PSScriptRoot 'cef-candidate-evidence.ps1')

function Assert-VeilPreflightDesktopMarker([string]$Root) {
    $dll = Join-Path $Root 'Desktop\Veil.Desktop.dll'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductName -cne 'VEIL CEF Preflight Validation Only') {
        throw 'Expected separate CefPreflight build marker.'
    }
}

function Assert-VeilPreflightBrokerMarker([string]$Root,[switch]$Diagnostic) {
    $dll = Join-Path $Root 'Broker\Veil.VolumeBroker.dll'
    $marker = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductName
    if ($Diagnostic) {
        if ($marker -cne 'VEIL CEF Broker Diagnostic Only') { throw 'Expected separate CefDiagnostic broker build marker.' }
    } elseif ($marker -ceq 'VEIL CEF Broker Diagnostic Only') {
        throw 'Diagnostic broker refused in ordinary preflight package.'
    }
}

function Get-VeilPreflightPackageFiles([string]$Root,[string]$CefEvidence,[string]$Thumbprint,[string]$Commit,[switch]$Diagnostic) {
    $entries = @(Get-VeilTreeFiles $Root -Restricted -WithRelativePath)
    $folders = @(Get-ChildItem -LiteralPath $Root -Force)
    if ($folders.Count -ne 3 -or @($folders | Where-Object { -not $_.PSIsContainer -or $_.Name -cnotin @('Desktop','Broker','Cef') }).Count -ne 0) {
        throw 'Expected exactly Desktop, Broker and Cef payload directories.'
    }
    $runtime = (Get-VeilCefVerifiedRuntimePinPlan (Join-Path $Root 'Cef') $CefEvidence $Thumbprint $Commit) | ConvertFrom-Json
    $seen = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $entries) {
        $name = $record.Relative
        if ($name.StartsWith('Cef/',[StringComparison]::Ordinal)) {
            # CEF-specific path and extension checks were already applied.
        } else {
            Assert-VeilRelativeFile $name
            if ($record.File.Extension -eq '.exe' -and $name -cnotin @('Desktop/Veil.Desktop.exe','Desktop/veil-vault.exe','Broker/Veil.VolumeBroker.exe')) {
                throw 'Unexpected executable in preflight payload.'
            }
            if ($name -iin @('Broker/cef-system-baseline.json','Broker/cef-system-baseline.sig')) {
                if (-not $Diagnostic -or $name -cnotin @('Broker/cef-system-baseline.json','Broker/cef-system-baseline.sig')) {
                    throw 'Unexpected diagnostic baseline input.'
                }
                if ($name -ceq 'Broker/cef-system-baseline.json' -and ($record.File.Length -lt 1 -or $record.File.Length -gt 1MB)) {
                    throw 'Diagnostic baseline payload size refused.'
                }
                if ($name -ceq 'Broker/cef-system-baseline.sig' -and $record.File.Length -notin @(384,512)) {
                    throw 'Diagnostic detached signature size refused.'
                }
            } elseif ($record.File.Extension -notin @('.exe','.dll','.json','.txt','.md') -and $record.File.Name -cnotin @('LICENSE','NOTICE')) {
                throw 'Unexpected preflight payload file type.'
            }
        }
        if ($record.File.Length -le 0 -or $seen.ContainsKey($name) -or $seen.Count -ge 8192) { throw 'Invalid preflight file inventory.' }
        $seen.Add($name,[pscustomobject]@{path=$name; bytes=$record.File.Length; sha256=(Get-FileHash -LiteralPath $record.File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
    foreach ($required in @('Desktop/Veil.Desktop.exe','Desktop/Veil.Desktop.dll','Desktop/Veil.Desktop.deps.json','Desktop/Veil.Desktop.runtimeconfig.json',
        'Broker/Veil.VolumeBroker.exe','Broker/Veil.VolumeBroker.dll','Broker/Veil.VolumeBroker.deps.json','Broker/Veil.VolumeBroker.runtimeconfig.json')) {
        if (-not $seen.ContainsKey($required) -or $seen[$required].path -cne $required) { throw 'Required preflight file missing.' }
    }
    if ($Diagnostic) {
        foreach ($required in @('Broker/cef-system-baseline.json','Broker/cef-system-baseline.sig')) {
            if (-not $seen.ContainsKey($required) -or $seen[$required].path -cne $required) {
                throw 'Required diagnostic baseline input missing.'
            }
        }
    }
    foreach ($pin in $runtime.files) {
        $name = 'Cef/' + $pin.path
        if (-not $seen.ContainsKey($name) -or $seen[$name].path -cne $name -or
            $seen[$name].bytes -ne $pin.bytes -or $seen[$name].sha256 -ne $pin.sha256) { throw 'CEF pin changed during inventory.' }
    }
    if (@($seen.Values | Where-Object { $_.path.StartsWith('Cef/',[StringComparison]::Ordinal) }).Count -ne $runtime.files.Count) {
        throw 'CEF inventory changed.'
    }
    foreach ($image in @('Desktop/Veil.Desktop.exe','Desktop/Veil.Desktop.dll','Broker/Veil.VolumeBroker.exe','Broker/Veil.VolumeBroker.dll')) {
        Assert-VeilSignedImage (Join-Path $Root $image) $Thumbprint
    }
    if ($seen.ContainsKey('Desktop/veil-vault.exe')) { Assert-VeilSignedImage (Join-Path $Root 'Desktop/veil-vault.exe') $Thumbprint }
    Assert-VeilPreflightDesktopMarker $Root
    Assert-VeilPreflightBrokerMarker $Root -Diagnostic:$Diagnostic
    return @($seen.Values | Sort-Object path)
}

function Write-VeilPreflightPackageEvidence([string]$Root,[string]$CefEvidence,[string]$Evidence,[string]$Commit,
    [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,[switch]$Diagnostic) {
    Assert-VeilCefEvidenceLocation $Root $Evidence
    Assert-VeilCefEvidenceLocation $CefEvidence $Evidence
    if (@(Get-ChildItem -LiteralPath $Evidence -Force).Count -ne 0 -or -not $Certificate.HasPrivateKey) { throw 'Expected fresh evidence and private signing key.' }
    $files = @(Get-VeilPreflightPackageFiles $Root $CefEvidence $Certificate.Thumbprint $Commit -Diagnostic:$Diagnostic)
    $kind = if ($Diagnostic) { 'cef-diagnostic-vm-package' } else { 'cef-preflight-vm-package' }
    $configuration = if ($Diagnostic) { 'CefDiagnostic' } else { 'CefPreflight' }
    $manifest = [ordered]@{kind=$kind;format=1;architecture='win-x64';configuration=$configuration;sourceCommit=$Commit;installable=$false;startupEnabled=$false;files=$files}
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 6))
    if ($bytes.Length -gt 4MB) { throw 'Preflight manifest oversized.' }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($Certificate)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $cms.ComputeSignature($signer)
    foreach ($record in @(@{Name='preflight-package.json';Bytes=$bytes},@{Name='preflight-package.p7s';Bytes=$cms.Encode()})) {
        $stream = [IO.File]::Open((Join-Path $Evidence $record.Name),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($record.Bytes,0,$record.Bytes.Length) } finally { $stream.Dispose() }
    }
}

function Read-VeilPreflightPackageEvidence([string]$Root,[string]$CefEvidence,[string]$Evidence,[string]$Thumbprint,[string]$Commit,[switch]$Diagnostic) {
    Assert-VeilCefEvidenceLocation $Root $Evidence
    Assert-VeilCefEvidenceLocation $CefEvidence $Evidence
    if (@(Get-ChildItem -LiteralPath $Evidence -Force).Count -ne 2) { throw 'Unexpected package evidence entries.' }
    $json = Join-Path $Evidence 'preflight-package.json'
    $sig = Join-Path $Evidence 'preflight-package.p7s'
    foreach ($path in @($json,$sig)) {
        Assert-VeilAdminPath $path
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -or $item.Length -le 0 -or $item.Length -gt 4MB) { throw 'Invalid package evidence file.' }
    }
    $bytes = [IO.File]::ReadAllBytes($json)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes),$true)
    $cms.Decode([IO.File]::ReadAllBytes($sig))
    if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].DigestAlgorithm.Value -ne '2.16.840.1.101.3.4.2.1') { throw 'Invalid package CMS signer.' }
    $cms.CheckSignature($false)
    if ($cms.SignerInfos[0].Certificate.Thumbprint -ne $Thumbprint) { throw 'Package signer mismatch.' }
    $manifest = [Text.UTF8Encoding]::new($false,$true).GetString($bytes) | ConvertFrom-Json
    $kind = if ($Diagnostic) { 'cef-diagnostic-vm-package' } else { 'cef-preflight-vm-package' }
    $configuration = if ($Diagnostic) { 'CefDiagnostic' } else { 'CefPreflight' }
    if ($manifest.kind -cne $kind -or $manifest.format -isnot [int] -or $manifest.format -ne 1 -or
        $manifest.configuration -cne $configuration -or $manifest.architecture -cne 'win-x64' -or $manifest.sourceCommit -cne $Commit -or
        $manifest.installable -isnot [bool] -or $manifest.installable -or $manifest.startupEnabled -isnot [bool] -or $manifest.startupEnabled -or
        $manifest.files -isnot [array] -or @($manifest.PSObject.Properties).Count -ne 8) { throw 'Unsupported preflight package evidence.' }
    $actual = @(Get-VeilPreflightPackageFiles $Root $CefEvidence $Thumbprint $Commit -Diagnostic:$Diagnostic)
    if ($actual.Count -ne $manifest.files.Count) { throw 'Preflight package inventory count mismatch.' }
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        if ($entry.path -isnot [string] -or $entry.sha256 -isnot [string] -or $entry.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
            ($entry.bytes -isnot [int] -and $entry.bytes -isnot [long]) -or $entry.bytes -le 0 -or $expected.ContainsKey($entry.path)) {
            throw 'Invalid preflight package entry.'
        }
        $expected.Add($entry.path,$entry)
    }
    foreach ($file in $actual) {
        if (-not $expected.ContainsKey($file.path)) { throw 'Unexpected preflight package file.' }
        $entry = $expected[$file.path]
        if ($entry.path -cne $file.path -or $entry.bytes -ne $file.bytes -or $entry.sha256 -cne $file.sha256) { throw 'Preflight package bytes changed.' }
    }
    return $manifest
}
