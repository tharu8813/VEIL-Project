using System.Text.Json;

namespace Veil.VolumeBroker;

/// <summary>Records fixed event names, results and process IDs only, never request bytes.
/// Diagnostic sink failures must not interrupt the broker's volume operations or relock retries.</summary>
internal sealed class BrokerLog(string? path, bool console = true)
{
    private readonly object gate = new();

    internal void Write(string name, object? fields = null)
    {
        if (!console && path is null) return;
        string line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, @event = name, fields });
        lock (gate)
        {
            if (console)
            {
                try { Console.Out.WriteLine(line); Console.Out.Flush(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            if (path is not null)
            {
                try { File.AppendAllText(path, line + Environment.NewLine); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        }
    }
}
