using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Veil.VolumeBroker;

internal sealed record BackendStatus(bool Attached, string? VolumeGuid, bool FullyEncrypted, bool ProtectionOn,
    bool Unlocked, bool ProtectorPresent);

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
        attached, attached, attached && unlocked, attached && fake.ReportedProtectorPresent);

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
            if (match) unlocked = true;
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
            if (-not $v) { @{ attached = $true; volumeGuid = $guid } | ConvertTo-Json -Compress; exit 0 }
            $conversion = ($v | Invoke-CimMethod -MethodName GetConversionStatus).ConversionStatus
            $protection = ($v | Invoke-CimMethod -MethodName GetProtectionStatus).ProtectionStatus
            $lock = ($v | Invoke-CimMethod -MethodName GetLockStatus).LockStatus
            $ids = ($v | Invoke-CimMethod -MethodName GetKeyProtectors -Arguments @{ KeyProtectorType = [uint32]0 }).VolumeKeyProtectorID
            @{ attached = $true; volumeGuid = $guid; conversion = $conversion; protection = $protection; lock = $lock; protectors = @($ids) } | ConvertTo-Json -Compress
            """, null);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("attached").GetBoolean()) return new(false, null, false, false, false, false);
        string? guid = root.TryGetProperty("volumeGuid", out var g) ? g.GetString() : null;
        int Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : -1;
        bool protector = root.TryGetProperty("protectors", out var ids) && ids.ValueKind == JsonValueKind.Array &&
            ids.EnumerateArray().Any(id => string.Equals(id.GetString(), config.ProtectorId, StringComparison.OrdinalIgnoreCase));
        // ConversionStatus 1 = FullyEncrypted, ProtectionStatus 1 = On, LockStatus 0 = Unlocked.
        return new(true, guid, Number("conversion") == 1, Number("protection") == 1, Number("lock") == 0, protector);
    }

    public void Attach() => Run($"$ErrorActionPreference = 'Stop'; Mount-DiskImage -ImagePath {Image} | Out-Null", null);

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
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000)) { process.Kill(true); throw new TimeoutException("Volume operation timed out."); }
        // Error text is discarded: it may echo arguments. Only the exit code is reported.
        if (process.ExitCode != 0) throw new InvalidOperationException($"Volume operation failed ({process.ExitCode}).");
        return output;
    }
}
