using System.Security.Principal;
using Veil.VolumeBroker;

#if VEIL_CEF_DIAGNOSTIC_IPC
// Explicit administrator-only, create-only trust maintenance. No private
// keys, arbitrary file paths, service start, IPC or enrollment fallback.
if (args.Length > 0 && args[0] == "--cef-enroll-diagnostic-trust")
{
    try
    {
        if (args is not [_, "--disposable-vm", var baselineSpki, var baselineSha256,
            var payloadSha256, var sequenceText, var approvalSpki, var approvalSha256])
            throw new ArgumentException("Diagnostic trust enrollment arguments refused.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Elevated administrator required.");
        static byte[] DecodeKey(string value)
        {
            if (value.Length is 0 or > 5464) throw new ArgumentException("Public key length refused.");
            byte[] key = Convert.FromBase64String(value);
            if (key.Length is 0 or > 4096) throw new ArgumentException("Public key length refused.");
            return key;
        }
        static byte[] DecodeDigest(string value)
        {
            if (value.Length != 64) throw new ArgumentException("Independent digest required.");
            return Convert.FromHexString(value);
        }
        if (!long.TryParse(sequenceText, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long sequence) || sequence <= 0)
            throw new ArgumentException("Independent exact sequence required.");
        var (payload, signature) = CefSignedSystemModuleBaseline.ReadInstalledDiagnosticInputs();
        CefSystemBaselineTrustRegistry.InstallDiagnosticTrust(payload, signature,
            DecodeKey(baselineSpki), DecodeDigest(baselineSha256), DecodeDigest(payloadSha256), sequence,
            DecodeKey(approvalSpki), DecodeDigest(approvalSha256));
        Console.WriteLine("Diagnostic trust enrolled and verified. Service startup NOT RUN.");
        return 0;
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        Console.Error.WriteLine($"Diagnostic trust enrollment refused or unconfirmed: {error.GetType().Name}. Preserve state for review.");
        return 1;
    }
}
#endif

// The service always reads the production registration. Console modes retain their existing behavior.
if (args is ["--service"])
    return BrokerService.Run();

//   Veil.VolumeBroker.exe                     production: %ProgramData%\VEIL\broker.json, admin-only ACL, elevated
//   Veil.VolumeBroker.exe --config <path>     production rules with another admin-only file
//   Veil.VolumeBroker.exe --dev-config <path> development: no ACL check, config must say development=true
BrokerConfig config;
try
{
    config = args switch
    {
        [] => BrokerConfig.Load(BrokerConfig.DefaultPath, development: false),
        ["--config", var path] => BrokerConfig.Load(path, development: false),
        ["--dev-config", var path] => BrokerConfig.Load(path, development: true),
        _ => throw new ArgumentException("Usage: [--config <path> | --dev-config <path>]"),
    };
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
    System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Configuration refused: {error.GetType().Name}");
    return 3;
}

using (var identity = WindowsIdentity.GetCurrent())
{
    bool elevated = identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    if (config.Backend == "bitlocker" && !elevated)
    {
        Console.Error.WriteLine("The BitLocker backend requires an elevated broker.");
        return 4;
    }
}

var log = new BrokerLog(config.LogPath);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
_ = Task.Run(() => { Console.In.ReadToEnd(); stop.Cancel(); });
try
{
    await new Broker(config, config.Backend == "fake" ? new FakeBitLockerBackend(config) : new WindowsBitLockerBackend(config), log)
        .RunAsync(stop.Token);
}
catch (System.ComponentModel.Win32Exception error)
{
    // ERROR_ACCESS_DENIED / ERROR_PIPE_BUSY when another process already owns the pipe name.
    log.Write("pipe-failed", new { code = error.NativeErrorCode });
    return 5;
}
log.Write("stopped");
return 0;
