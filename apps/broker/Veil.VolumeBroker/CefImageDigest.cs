// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;

namespace Veil.VolumeBroker;

/// <summary>Pure SHA-256 pin. A match is necessary, not executable admission.</summary>
internal sealed class CefImageDigest
{
    private readonly byte[] expected;

    internal CefImageDigest(string hex)
    {
        if (hex is not { Length: 64 } || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("A CEF executable SHA-256 pin is required.", nameof(hex));
        expected = Convert.FromHexString(hex);
    }

    internal bool Matches(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead || !image.CanSeek || image.Position != 0)
            throw new ArgumentException("Expected a readable image stream at its start.", nameof(image));
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(image), expected);
    }
}
