using System.Text.Json;

namespace Veil.Desktop;

/// <summary>Only the top-level visible, unambiguous same-origin login form. No submit listener or web-message bridge.</summary>
internal static class LoginFormPolicy
{
    private const string Selector = """
        const visible = el => {
            if (!el.isConnected || el.matches(':disabled') || el.readOnly ||
                el.closest('[hidden], [inert], [aria-hidden="true"]')) return false;
            const rect = el.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return false;
            for (let node = el; node; node = node.parentElement) {
                const style = getComputedStyle(node);
                if (style.visibility !== 'visible' || style.display === 'none' ||
                    Number(style.opacity) === 0 || style.contentVisibility === 'hidden') return false;
            }
            return true;
        };
        const hasToken = (el, token) => el.autocomplete.toLowerCase().split(/\s+/).includes(token);
        const passwords = [...document.querySelectorAll('input[type="password"]')];
        if (passwords.length !== 1 || !visible(passwords[0])) return null;
        const pass = passwords[0], form = pass.form;
        if (!form || hasToken(pass, 'new-password') || hasToken(pass, 'one-time-code') ||
            new URL(form.action, location.href).origin !== location.origin ||
            [...form.elements].some(el => el.hasAttribute('formaction') &&
                new URL(el.getAttribute('formaction'), location.href).origin !== location.origin)) return null;
        const users = [...form.elements].filter(el => el instanceof HTMLInputElement && visible(el) &&
            ['text', 'email', 'tel'].includes(el.type) && !hasToken(el, 'one-time-code'));
        const explicit = users.filter(el => hasToken(el, 'username'));
        const user = explicit.length === 1 ? explicit[0] : explicit.length === 0 && users.length === 1 ? users[0] : null;
        if (!user) return null;
        """;

    private static string Script(string origin, string page, string action) => $$"""
        (() => {
            try {
                if (window !== window.top || location.origin !== {{JsonSerializer.Serialize(origin)}} ||
                    location.href !== new URL({{JsonSerializer.Serialize(page)}}).href ||
                    document.visibilityState !== 'visible') return null;
                {{Selector}}
                {{action}}
            } catch { return null; }
        })()
        """;

    internal static string Probe(string origin, string page) => Script(origin, page, "return true;");
    internal static string Read(string origin, string page) => Script(origin, page, """
        if (!user.value.trim() || user.value.length > 512 || !pass.value || pass.value.length > 2048) return null;
        return { UserName: user.value, Password: pass.value };
        """);
    internal static string Fill(SavedLogin login, string page) => Script(login.Origin, page, $"""
        if (pass.value || (user.value && user.value !== {{JsonSerializer.Serialize(login.UserName)}})) return null;
        const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
        set.call(user, {{JsonSerializer.Serialize(login.UserName)}});
        set.call(pass, {{JsonSerializer.Serialize(login.Password)}});
        for (const el of [user, pass]) {
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
        }
        return true;
        """);

    internal sealed record FormValues(string UserName, string Password)
    {
        public override string ToString() => "[Login form redacted]";
    }

    internal static SavedLogin? Decode(string origin, string json)
    {
        if (SitePermissionPolicy.Origin(origin) != origin || json.Length > 32768) return null;
        var values = JsonSerializer.Deserialize<FormValues>(json);
        if (values is null || values.UserName is not { Length: > 0 and <= 512 } ||
            string.IsNullOrWhiteSpace(values.UserName) || values.UserName.Any(char.IsControl) ||
            values.Password is not { Length: > 0 and <= 2048 }) return null;
        return new SavedLogin(origin, values.UserName, values.Password);
    }
}
