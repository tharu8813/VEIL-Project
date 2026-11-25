// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

/// <summary>Classifies submitted address-bar text without any network access.</summary>
internal static class AddressInputPolicy
{
    internal static bool HasUnsafeCharacters(string input) =>
        input.Any(char.IsControl) || input.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);

    private static bool WebAddress(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.Host.Length != 0 && uri.UserInfo.Length == 0;

    /// <summary>True means address-like input (including rejected addresses); false means ordinary search text.</summary>
    internal static bool TryResolve(string input, out Uri? result)
    {
        result = null;
        if (HasUnsafeCharacters(input) || input.Contains('\\') || input.StartsWith("//")) return true;
        if (input.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            if (!input.Any(char.IsWhiteSpace) &&
                (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                Uri.TryCreate(input, UriKind.Absolute, out var uri) && WebAddress(uri))
                result = uri;
            return true;
        }
        if (input.Contains("://") || input.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!input.Any(char.IsWhiteSpace))
        {
            string authority = input.Split('/', '?', '#')[0];
            if (authority.Contains('@') || authority.StartsWith('[') ||
                authority.Contains('.') || authority.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                authority.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate("https://" + input, UriKind.Absolute, out var candidate) && WebAddress(candidate))
                    result = candidate.IsLoopback ? new UriBuilder(candidate)
                    { Scheme = "http", Port = candidate.IsDefaultPort ? -1 : candidate.Port }.Uri : candidate;
                return true;
            }
            if (input.Contains(':') || input.StartsWith('/')) return true;
        }
        int colon = input.IndexOf(':');
        // A scheme followed by a non-space is not a search, even if later text contains spaces.
        return colon > 0 && Uri.CheckSchemeName(input[..colon]) &&
            (colon + 1 == input.Length || !char.IsWhiteSpace(input[colon + 1]));
    }
}
