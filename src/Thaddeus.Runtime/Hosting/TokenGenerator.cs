using System.Security.Cryptography;

namespace Thaddeus.Runtime.Hosting;

/// <summary>
/// Generates the per-start bearer token used for loopback HTTP and WebSocket auth.
/// 256 bits of cryptographic randomness, encoded as base64url for header friendliness.
/// </summary>
public static class TokenGenerator
{
    /// <summary>Generates a fresh 256-bit base64url token.</summary>
    public static string NewToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Base64UrlEncode(buffer);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
