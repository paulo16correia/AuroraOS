using System.Security.Cryptography;
using System.Text;

namespace Aurora.Adapters.Vault;

/// <summary>Ciphertext plus the values needed to open it again.</summary>
public sealed record SealedSecret(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

/// <summary>
/// Encrypts secrets at rest with AES-256-GCM (RFC 09 rule 2).
/// </summary>
/// <remarks>
/// AES-GCM from the BCL, deliberately, so this runs identically on Windows, macOS and Linux.
/// DPAPI and Keychain would each be stronger on their own platform and neither is portable.
/// <para>
/// The secret's own id is used as associated data, so a ciphertext cannot be moved from one row to
/// another: swapping the blobs makes authentication fail rather than silently handing a tool the
/// wrong credential.
/// </para>
/// </remarks>
public sealed class AesGcmSecretProtector
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("AES-GCM is not available on this platform.");
        }

        _key = key;
    }

    public SealedSecret Protect(string secretReferenceId, ReadOnlySpan<char> value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value.ToArray());
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagBytes];

            using var aes = new AesGcm(_key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Associated(secretReferenceId));

            return new SealedSecret(nonce, ciphertext, tag);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    /// <summary>Returns the value in a caller-owned buffer, which the caller must clear.</summary>
    public char[] Unprotect(string secretReferenceId, SealedSecret sealedSecret)
    {
        var plaintext = new byte[sealedSecret.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(
                sealedSecret.Nonce, sealedSecret.Ciphertext, sealedSecret.Tag, plaintext,
                Associated(secretReferenceId));

            return Encoding.UTF8.GetChars(plaintext);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private static byte[] Associated(string secretReferenceId) => Encoding.UTF8.GetBytes(secretReferenceId);
}
