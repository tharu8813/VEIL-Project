using System.IO;
using System.Security.Cryptography;

namespace Veil.Desktop;

// Restricted data URL decoder. Never navigates to or fetches the supplied address.
internal static class InlineDownloadPolicy
{
    internal const int MaxBytes = 1024 * 1024;
    internal const int MaxUriChars = 3 * MaxBytes + 256;
#if DEBUG
    internal static bool Enabled => true;
#else
    internal static bool Enabled => false;
#endif
    internal static bool SourceAllowed(Uri uri) => uri.IsAbsoluteUri && uri.UserInfo.Length == 0 &&
        (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback));
    private static bool Token(char c) => char.IsAsciiLetterOrDigit(c) || c is '!' or '$' or '&' or '+' or '-' or '.' or '^' or '_' or '|';
    private static InvalidDataException Invalid() => new("Unsupported or oversized data URL.");
    internal static byte[] Decode(string raw)
    {
        if (raw.Length > MaxUriChars || !raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        int comma = raw.IndexOf(',');
        if (comma < 5 || comma > 255) throw Invalid();
        string[] fields = raw[5..comma].Split(';');
        string mime = fields[0];
        if (mime.Length != 0)
        {
            int slash = mime.IndexOf('/');
            if (slash <= 0 || slash == mime.Length - 1 || !mime[..slash].All(Token) || !mime[(slash + 1)..].All(Token)) throw Invalid();
        }
        bool base64 = false, charset = false;
        for (int i = 1; i < fields.Length; i++)
        {
            if (i == fields.Length - 1 && fields[i].Equals("base64", StringComparison.OrdinalIgnoreCase)) base64 = true;
            else if (!charset && (fields[i].Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase) ||
                fields[i].Equals("charset=us-ascii", StringComparison.OrdinalIgnoreCase))) charset = true;
            else throw Invalid();
        }
        var payload = raw.AsSpan(comma + 1);
        if (base64)
        {
            if (payload.Length % 4 != 0 || payload.Length > ((MaxBytes + 2) / 3) * 4) throw Invalid();
            int padding = payload.EndsWith("==", StringComparison.Ordinal) ? 2 : payload.EndsWith("=", StringComparison.Ordinal) ? 1 : 0;
            int length = payload.Length / 4 * 3 - padding;
            if (length < 0 || length > MaxBytes) throw Invalid();
            for (int i = 0; i < payload.Length - padding; i++)
                if (!char.IsAsciiLetterOrDigit(payload[i]) && payload[i] is not ('+' or '/')) throw Invalid();
            byte[] decoded = new byte[length];
            if (Convert.TryFromBase64Chars(payload, decoded, out int written) && written == length) return decoded;
            CryptographicOperations.ZeroMemory(decoded);
            throw Invalid();
        }
        int size = PercentDecode(payload, default, false);
        byte[] bytes = new byte[size];
        try { PercentDecode(payload, bytes, true); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static int PercentDecode(ReadOnlySpan<char> source, Span<byte> output, bool write)
    {
        int count = 0;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            byte value;
            if (c == '%')
            {
                if (i + 2 >= source.Length || Hex(source[i + 1]) is not int hi || Hex(source[i + 2]) is not int lo) throw Invalid();
                value = (byte)((hi << 4) | lo);
                i += 2;
            }
            else
            {
                if (c < 0x21 || c > 0x7e || c == '#') throw Invalid();
                value = (byte)c;
            }
            if (count >= MaxBytes) throw Invalid();
            if (write) output[count] = value;
            count++;
        }
        return count;
    }
    private static int? Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => null,
    };
}
