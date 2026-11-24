using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

/// <summary>
/// User security policy saved in the profile state (ADR-0007). Missing values mean the safe default:
/// Microsoft SmartScreen on. <see cref="Recovered"/> marks a value that could not be read and was
/// replaced by the defaults (never by "off").
/// </summary>
[JsonConverter(typeof(SecuritySettingsConverter))]
internal sealed record SecuritySettings(bool SmartScreen = true)
{
    internal static readonly SecuritySettings Default = new();
    [JsonIgnore] internal bool Recovered { get; init; }
}

/// <summary>Reads leniently: a damaged security value falls back to the safe defaults instead of "off".</summary>
internal sealed class SecuritySettingsConverter : JsonConverter<SecuritySettings>
{
    public override SecuritySettings? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return SecuritySettings.Default with { Recovered = true };
        }
        bool smartScreen = true, damaged = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string? name = reader.GetString();
            reader.Read();
            if (name == nameof(SecuritySettings.SmartScreen))
            {
                if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) smartScreen = reader.GetBoolean();
                else { damaged = true; reader.Skip(); }
            }
            else reader.Skip(); // settings of a newer version are ignored, not fatal
        }
        return new SecuritySettings(damaged || smartScreen) { Recovered = damaged };
    }

    public override void Write(Utf8JsonWriter writer, SecuritySettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean(nameof(SecuritySettings.SmartScreen), value.SmartScreen);
        writer.WriteEndObject();
    }
}

/// <summary>What VEIL could actually do with the saved SmartScreen choice in this run.</summary>
internal enum SmartScreenState
{
    /// <summary>No browser view has been configured yet.</summary>
    Pending,
    /// <summary>Every open view reports the saved value.</summary>
    Applied,
    /// <summary>At least one view rejected or did not keep the value (old runtime, closed view, engine error).</summary>
    Failed,
}

/// <summary>
/// Security Manager (ADR-0007): owns the user's security policy for one profile, applies it to every
/// WebView of the shared engine, and reports saved vs applied state. It is also where future VEIL
/// threat engines are registered (<see cref="ThreatProtection"/>). Nothing here sends data anywhere
/// or records addresses.
/// </summary>
internal sealed class SecurityManager
{
    private readonly Func<IEnumerable<CoreWebView2>> views;

    internal SecurityManager(SecuritySettings? saved, Func<IEnumerable<CoreWebView2>> views)
    {
        Saved = saved ?? SecuritySettings.Default;
        this.views = views;
    }

    /// <summary>The policy in the profile state (what the user chose).</summary>
    internal SecuritySettings Saved { get; private set; }
    internal SmartScreenState State { get; private set; } = SmartScreenState.Pending;
    /// <summary>Exception type of the last apply failure, for diagnostics (never contains page data).</summary>
    internal string? LastFailure { get; private set; }
    internal ThreatProtection Threats { get; } = new();
    /// <summary>Signed local threat list state (ADR-0008).</summary>
    internal ThreatListStatus ThreatList { get; private set; } = ThreatListStatus.None;

    /// <summary>Loads <c>&lt;folder&gt;/threat-list.json</c> and registers the local engine when a list exists
    /// (a damaged list is registered too, so every check reports Error instead of silently passing).</summary>
    internal void LoadThreatList(string folder)
    {
        var (engine, status) = LocalThreatList.Load(folder, LocalThreatList.TrustedKeys(), DateTimeOffset.UtcNow);
        ThreatList = status;
        if (engine is not null && !Threats.Register(engine))
            ThreatList = status with { Valid = false, Detail = "이 빌드에서 사용할 수 없음" };
    }

    /// <summary>Status suffix for the VEIL list; empty when there is no list or it is in use.</summary>
    internal string ThreatListSuffix() => ThreatList is { Present: true, Valid: false } ? $" · 위협 목록 오류({ThreatList.Detail})" : "";

    /// <summary>
    /// Applies the saved policy to a new view. Must run before its first navigation: a view that never
    /// sets the value turns SmartScreen back on for the whole user data folder.
    /// </summary>
    internal bool Apply(CoreWebView2 view)
    {
        bool ok = Set(view, Saved.SmartScreen);
        if (!ok) State = SmartScreenState.Failed;
        else if (State == SmartScreenState.Pending) State = SmartScreenState.Applied;
        return ok;
    }

    /// <summary>
    /// Saves a new policy, then applies it to every open view (the engine uses it from the next
    /// navigation or download). If saving fails nothing changes; if applying fails the state says so.
    /// </summary>
    internal void Change(SecuritySettings chosen, Action<SecuritySettings> persist)
    {
        persist(chosen); // throws on failure: the old policy stays saved and applied
        Saved = chosen with { Recovered = false };
        bool all = true;
        foreach (var view in views().ToList()) all &= Set(view, Saved.SmartScreen);
        State = all ? SmartScreenState.Applied : SmartScreenState.Failed;
    }

    /// <summary>Re-reads every open view; Failed when any view disagrees with the saved value.</summary>
    internal SmartScreenState Verify()
    {
        bool any = false;
        foreach (var view in views().ToList())
        {
            any = true;
            bool? actual = Read(view);
            if (actual != Saved.SmartScreen) { State = SmartScreenState.Failed; return State; }
        }
        if (any && State != SmartScreenState.Failed) State = SmartScreenState.Applied;
        return State;
    }

    internal static bool? Read(CoreWebView2 view)
    {
        try { return view.Settings.IsReputationCheckingRequired; }
        catch (Exception) { return null; }
    }

    private bool Set(CoreWebView2 view, bool value)
    {
        try
        {
            view.Settings.IsReputationCheckingRequired = value;
            if (view.Settings.IsReputationCheckingRequired == value) return true;
            LastFailure = "value not kept";
        }
        catch (Exception error) // runtime without the API, closed view, COM failure
        {
            LastFailure = error.GetType().Name;
        }
        return false;
    }

    /// <summary>Status text. "Applied" means VEIL set the value; Windows policy can still turn SmartScreen off.</summary>
    internal string Describe() => State switch
    {
        SmartScreenState.Failed => $"SmartScreen 설정을 적용하지 못했습니다 ({LastFailure}). 저장된 설정: {(Saved.SmartScreen ? "켜짐" : "꺼짐")}",
        SmartScreenState.Pending => $"SmartScreen {(Saved.SmartScreen ? "켜짐" : "꺼짐")} (저장됨, 아직 적용할 화면 없음)",
        _ when Saved.SmartScreen => "SmartScreen 켜짐 (VEIL 적용됨 · Windows 설정이 끄면 꺼질 수 있음)",
        _ => "SmartScreen 꺼짐 (VEIL 적용됨)",
    };
}

/// <summary>Result of one threat check. Unknown: the engine had no information. Error: the check failed.</summary>
internal enum ThreatVerdict { Malicious, NotDetected, Unknown, Error }

/// <summary>Outcome with a short engine-supplied reason. Never carries the checked address.</summary>
internal sealed record ThreatResult(ThreatVerdict Verdict, string Engine, string Reason);

/// <summary>
/// Extension point for VEIL's own protection engines (ADR-0007). 1.0 ships no engine: no detection,
/// database, feed or external API. Engines run locally and must not send the address anywhere.
/// </summary>
internal interface IThreatEngine
{
    string Name { get; }
    /// <summary>True only for engines intended for production; test engines return false.</summary>
    bool ProductionReady { get; }
    ValueTask<ThreatResult> CheckAsync(Uri address, CancellationToken cancel);
    /// <summary>Checks a downloaded file by SHA-256 (lowercase hex). Engines without file data answer Unknown.</summary>
    ValueTask<ThreatResult> CheckFileAsync(string sha256, CancellationToken cancel) =>
        new(new ThreatResult(ThreatVerdict.Unknown, Name, "no file check"));
}

/// <summary>
/// Registry of threat engines. Independent of SmartScreen: a result never turns SmartScreen off and
/// SmartScreen decisions are never reported as VEIL verdicts.
/// </summary>
internal sealed class ThreatProtection
{
    private readonly List<IThreatEngine> engines = [];

    /// <summary>1.1: production builds accept production-ready engines (the signed local list, ADR-0008).</summary>
    internal static bool ProductionEnginesEnabled => true;

    internal IReadOnlyList<IThreatEngine> Engines => engines;

    /// <summary>Registers an engine. Release builds refuse engines not marked production-ready, and while
    /// <see cref="ProductionEnginesEnabled"/> is false they refuse every engine.</summary>
    internal bool Register(IThreatEngine engine)
    {
#if DEBUG
        engines.Add(engine);
        return true;
#else
        if (!ProductionEnginesEnabled || !engine.ProductionReady) return false;
        engines.Add(engine);
        return true;
#endif
    }

    /// <summary>
    /// Combined verdict: Malicious if any engine says so; otherwise Error if any engine failed;
    /// otherwise NotDetected if an engine positively checked it; otherwise Unknown (no engines included).
    /// </summary>
    internal Task<ThreatResult> CheckAsync(Uri address, TimeSpan timeout, CancellationToken cancel = default) =>
        CombineAsync((engine, token) => engine.CheckAsync(address, token), timeout, cancel);

    /// <summary>Same combination rule for a downloaded file's SHA-256.</summary>
    internal Task<ThreatResult> CheckFileAsync(string sha256, TimeSpan timeout, CancellationToken cancel = default) =>
        CombineAsync((engine, token) => engine.CheckFileAsync(sha256, token), timeout, cancel);

    private async Task<ThreatResult> CombineAsync(Func<IThreatEngine, CancellationToken, ValueTask<ThreatResult>> check, TimeSpan timeout, CancellationToken cancel)
    {
        if (engines.Count == 0) return new ThreatResult(ThreatVerdict.Unknown, "none", "no VEIL engine enabled");
        var results = new List<ThreatResult>();
        foreach (var engine in engines)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(timeout);
            try
            {
                var pending = check(engine, limit.Token);
                // Synchronous engines (the local list) complete without a thread switch, so navigation can decide inline.
                results.Add(pending.IsCompletedSuccessfully ? pending.Result : await pending.AsTask().WaitAsync(limit.Token));
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                results.Add(new ThreatResult(ThreatVerdict.Error, engine.Name, "timeout"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                results.Add(new ThreatResult(ThreatVerdict.Error, engine.Name, error.GetType().Name));
            }
        }
        return results.FirstOrDefault(r => r.Verdict == ThreatVerdict.Malicious)
            ?? results.FirstOrDefault(r => r.Verdict == ThreatVerdict.Error)
            ?? results.FirstOrDefault(r => r.Verdict == ThreatVerdict.NotDetected)
            ?? results[0];
    }
}

#if DEBUG
/// <summary>
/// Test-only engine (Debug builds only, not compiled into Release). Answers from a fixed host table;
/// every other host is Unknown, so it never declares arbitrary addresses safe.
/// </summary>
internal sealed class MockThreatEngine(IReadOnlyDictionary<string, ThreatVerdict> hosts, TimeSpan? delay = null) : IThreatEngine
{
    public string Name => "mock";
    public bool ProductionReady => false;

    public async ValueTask<ThreatResult> CheckAsync(Uri address, CancellationToken cancel)
    {
        if (delay is { } wait) await Task.Delay(wait, cancel);
        if (!hosts.TryGetValue(address.IdnHost, out var verdict)) return new ThreatResult(ThreatVerdict.Unknown, Name, "not in table");
        if (verdict == ThreatVerdict.Error) throw new InvalidOperationException("mock failure");
        return new ThreatResult(verdict, Name, "table");
    }
}
#endif
