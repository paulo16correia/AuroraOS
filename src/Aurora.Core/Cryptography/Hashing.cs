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
}
