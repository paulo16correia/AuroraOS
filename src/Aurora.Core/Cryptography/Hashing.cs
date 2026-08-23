using System.Security.Cryptography;
using System.Text;

namespace Aurora.Core.Cryptography;

/// <summary>SHA-256 helpers producing lowercase hex digests.</summary>
public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(string utf8Text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(utf8Text));

    /// <summary>
    /// Keyed HMAC-SHA-256, lowercase hex. Used for the audit chain so that write access to the
    /// database file is not enough to forge a consistent chain (design/0005).
    /// </summary>
    public static string HmacSha256Hex(ReadOnlySpan<byte> key, string utf8Text)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(utf8Text), mac);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
