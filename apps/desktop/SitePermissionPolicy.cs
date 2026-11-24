using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

internal sealed record SitePermissionRule(string Origin, string Kind, string Mode);

/// <summary>Profile-owned allowlist; all unknown, malformed and unsupported requests deny.</summary>
internal static class SitePermissionPolicy
{
    internal const string Block = "block";
    internal const string Ask = "ask";
    internal const string Allow = "allow";

    internal static readonly (string Key, string Label)[] Kinds =
    [
        ("camera", "카메라"),
        ("microphone", "마이크"),
        ("location", "위치"),
        ("notifications", "알림"),
    ];

    internal static string? Kind(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => "camera",
        CoreWebView2PermissionKind.Microphone => "microphone",
        CoreWebView2PermissionKind.Geolocation => "location",
        CoreWebView2PermissionKind.Notifications => "notifications",
        _ => null,
    };

    internal static string? Origin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 || uri.UserInfo.Length != 0)
            return null;
        try
        {
            return new UriBuilder(Uri.UriSchemeHttps, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port)
                .Uri.GetLeftPart(UriPartial.Authority);
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    internal static bool ValidRules(List<SitePermissionRule>? rules)
    {
        if (rules is null) return true;
        var seen = new HashSet<(string, string)>();
        return rules.All(rule => rule is not null && rule.Origin is not null &&
            Origin(rule.Origin) == rule.Origin && Kinds.Any(kind => kind.Key == rule.Kind) &&
            rule.Mode is Ask or Allow && seen.Add((rule.Origin, rule.Kind)));
    }

    internal static string ModeFor(List<SitePermissionRule>? rules, string origin, string kind) =>
        rules?.FirstOrDefault(rule => rule.Origin == origin && rule.Kind == kind)?.Mode switch
        {
            Ask => Ask,
            Allow => Allow,
            _ => Block,
        };
}
