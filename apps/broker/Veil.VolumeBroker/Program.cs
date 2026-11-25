using System.Security.Principal;
using Veil.VolumeBroker;

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
