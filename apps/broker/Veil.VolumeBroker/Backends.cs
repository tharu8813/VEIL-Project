using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Veil.VolumeBroker;

internal sealed record BackendStatus(bool Attached, string? VolumeGuid, bool FullyEncrypted, bool ProtectionOn,
    bool Unlocked, bool ProtectorPresent, bool ReadyToUnlock = false);

internal enum LockResult { Locked, HandlesOpen, Failed }

/// <summary>Volume operations on the single registered image. No method accepts a caller-supplied path.</summary>
internal interface IBitLockerBackend
{
    BackendStatus Status();
    void Attach();
    void Detach();
    /// <returns>False when the secret was rejected.</returns>
    bool Unlock(ReadOnlySpan<char> secret);
    /// <summary>Never forces a dismount.</summary>
    LockResult Lock();
}

/// <summary>Development stand-in with in-memory state. It encrypts nothing.</summary>
internal sealed class FakeBitLockerBackend(BrokerConfig config) : IBitLockerBackend
{
    private readonly FakeBackendConfig fake = config.Fake!;
    private bool attached, unlocked;

    public BackendStatus Status() => new(attached, attached ? fake.ReportedVolumeGuid ?? config.VolumeGuid : null,
        attached && unlocked, attached && unlocked && fake.ReportedProtectionOnAfterUnlock,
        attached && unlocked, attached && fake.ReportedProtectorPresent,
        attached && !unlocked && fake.ReportedProtectorPresent);

    public void Attach() { attached = true; unlocked = false; }
    public void Detach() { attached = false; unlocked = false; }

    public bool Unlock(ReadOnlySpan<char> secret)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetMaxByteCount(secret.Length)];
        try
        {
            int count = Encoding.UTF8.GetBytes(secret, bytes);
            byte[] expected = Convert.FromHexString(fake.SecretSha256);
            bool match = CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, count)), expected);
            if (match)
            {
                unlocked = true;
                if (fake.ThrowAfterUnlock) throw new InvalidOperationException("Synthetic lost unlock result.");
            }
            return match;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public LockResult Lock()
    {
        if (File.Exists(fake.HandleMarker)) return LockResult.HandlesOpen;
        unlocked = false;
        return LockResult.Locked;
    }
}

/// <summary>Windows Storage and BitLocker WMI through a child PowerShell. Requires an elevated broker.
/// Scripts contain only registered configuration values; the unlock secret goes over stdin.
/// Not exercised on the development host: run only in the isolated VM.</summary>
internal sealed class WindowsBitLockerBackend(BrokerConfig config) : IBitLockerBackend
{
    private const string Volumes = "Get-CimInstance -Namespace root/cimv2/Security/MicrosoftVolumeEncryption " +
        "-ClassName Win32_EncryptableVolume";

    private string Image => Quote(config.ImagePath);

    public BackendStatus Status()
    {
        string json = Run($$"""
            $ErrorActionPreference = 'Stop'
            $image = Get-DiskImage -ImagePath {{Image}}
            if (-not $image.Attached) { '{"attached":false}'; exit 0 }
            $guid = ($image | Get-Disk | Get-Partition).AccessPaths | Where-Object { $_ -like '\\?\Volume{*' } | Select-Object -First 1
            $v = {{Volumes}} | Where-Object DeviceID -eq $guid
            if (-not $v) { throw 'BitLocker status is unavailable for the attached volume.' }
            $lock = $v | Invoke-CimMethod -MethodName GetLockStatus
            if ($lock.ReturnValue -ne 0 -or $lock.LockStatus -notin @(0, 1)) { throw 'BitLocker lock state is unavailable.' }
            $protection = $v | Invoke-CimMethod -MethodName GetProtectionStatus
            if ($lock.LockStatus -eq 1) {
                if ($protection.ReturnValue -ne 0 -and $protection.ReturnValue -ne 2150694912) { throw 'BitLocker protection state is unavailable.' }
                $candidate = $protection.ReturnValue -eq 2150694912 -or $protection.ProtectionStatus -in @(1, 2)
                @{ attached = $true; volumeGuid = $guid; lock = 1; readyToUnlock = $candidate } | ConvertTo-Json -Compress; exit 0
            }
            $conversion = $v | Invoke-CimMethod -MethodName GetConversionStatus
            $ids = $v | Invoke-CimMethod -MethodName GetKeyProtectors -Arguments @{ KeyProtectorType = [uint32]{{(config.RecoveryProtectorId is null ? 0 : 8)}} }
            if ($protection.ReturnValue -ne 0 -or $conversion.ReturnValue -ne 0 -or $ids.ReturnValue -ne 0) { throw 'BitLocker protection verification failed.' }
            # Keep lock status available even if the auto-unlock query fails:
            # unknown policy is never a valid protection attestation.
            $autoUnlockDisabled = $false
            try {
                $automatic = $v | Invoke-CimMethod -MethodName IsAutoUnlockEnabled
                $autoUnlockDisabled = ($automatic.ReturnValue -eq 0 -and
                    $automatic.IsAutoUnlockEnabled -is [bool] -and -not $automatic.IsAutoUnlockEnabled)
            }
            catch { $autoUnlockDisabled = $false }
            $recovery = @()
            if ({{(config.RecoveryProtectorId is not null ? "$true" : "$false")}}) {
                try {
                    $numeric = $v | Invoke-CimMethod -MethodName GetKeyProtectors -Arguments @{ KeyProtectorType = [uint32]2 }
                    if ($numeric.ReturnValue -eq 0) { $recovery = @($numeric.VolumeKeyProtectorID) }
                }
                catch { $recovery = @() }
            }
            @{ attached = $true; volumeGuid = $guid; conversion = $conversion.ConversionStatus; protection = $protection.ProtectionStatus; autoUnlockDisabled = $autoUnlockDisabled; lock = 0; protectors = @($ids.VolumeKeyProtectorID); recoveryProtectors = $recovery } | ConvertTo-Json -Compress
            """, null);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("attached").GetBoolean()) return new(false, null, false, false, false, false);
        string? guid = root.TryGetProperty("volumeGuid", out var g) ? g.GetString() : null;
        int Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : -1;
        bool protector = root.TryGetProperty("protectors", out var ids) && ids.ValueKind == JsonValueKind.Array &&
            ids.EnumerateArray().Any(id => SameProtector(id, config.ProtectorId));
        if (config.RecoveryProtectorId is { } recovery)
            protector &= ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() == 1 &&
                root.TryGetProperty("recoveryProtectors", out var numeric) && numeric.ValueKind == JsonValueKind.Array &&
                numeric.EnumerateArray().Any(id => SameProtector(id, recovery));
        // ProtectionOn is an attestation of the runtime protection policy, not
        // just the raw WMI flag. Missing/failed auto-unlock evidence fails closed.
        bool autoUnlockDisabled = root.TryGetProperty("autoUnlockDisabled", out var automatic) &&
            automatic.ValueKind == JsonValueKind.True;
        // ConversionStatus 1 = FullyEncrypted, ProtectionStatus 1 = On, LockStatus 0 = Unlocked.
        return new(true, guid, Number("conversion") == 1, Number("protection") == 1 && autoUnlockDisabled, Number("lock") == 0, protector,
            root.TryGetProperty("readyToUnlock", out var ready) && ready.GetBoolean());
    }

    private static bool SameProtector(JsonElement id, string registered) =>
        id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var actual) &&
        Guid.TryParse(registered, out var expected) && actual == expected;

    public void Attach()
    {
        if (config.Development)
            Run($"$ErrorActionPreference = 'Stop'; Mount-DiskImage -ImagePath {Image} | Out-Null", null);
        else
            Native.AttachRegisteredImage(config.ImagePath, config.UserSid);
    }

    public void Detach() => Run($"$ErrorActionPreference = 'Stop'; Dismount-DiskImage -ImagePath {Image} | Out-Null", null);

    public bool Unlock(ReadOnlySpan<char> secret)
    {
        string result = Run($$"""
            $ErrorActionPreference = 'Stop'
            $secret = [Console]::In.ReadLine()
            try { $r = {{Volumes}} | Where-Object DeviceID -eq {{Quote(config.VolumeGuid)}} | Invoke-CimMethod -MethodName UnlockWithPassphrase -Arguments @{ Passphrase = $secret } }
            finally { $secret = $null }
            $r.ReturnValue
            """, secret);
        return result.Trim() == "0";
    }

    public LockResult Lock()
    {
        string result = Run($$"""
            $ErrorActionPreference = 'Stop'
            ({{Volumes}} | Where-Object DeviceID -eq {{Quote(config.VolumeGuid)}} | Invoke-CimMethod -MethodName Lock -Arguments @{ ForceDismount = $false }).ReturnValue
            """, null).Trim();
        // Without ForceDismount, open files make Lock fail. The exact return codes are not mapped yet
        // (VM verification pending), so every non-zero result is a refused lock.
        return result == "0" ? LockResult.Locked : LockResult.Failed;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Run(string script, ReadOnlySpan<char> stdin)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        if (!stdin.IsEmpty) process.StandardInput.WriteLine(stdin);
        process.StandardInput.Close();
        // Start both drains before waiting; ReadToEnd() followed by WaitForExit() could
        // deadlock if PowerShell filled stderr, bypassing the old 60-second timeout.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Volume operation timed out.");
        }
        // Never expose stderr: it might contain script parameters or an unlock secret.
        if (process.ExitCode != 0) throw new InvalidOperationException($"Volume operation failed ({process.ExitCode}).");
        return stdout.GetAwaiter().GetResult();
    }
}
