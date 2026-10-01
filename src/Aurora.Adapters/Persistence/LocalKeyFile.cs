using System.Security.Cryptography;
using Aurora.Adapters.Files;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// A 32-byte key held in an owner-only file, created on first use.
/// </summary>
/// <remarks>
/// Shared by the audit chain (docs/adr/0005) and the vault (docs/adr/0014). Both keep their key
/// outside the database for the same reason: write access to the database must not be enough to
/// forge a chain or read a secret.
/// <para>
/// Cross-platform by construction — no DPAPI, no Keychain. An OS keystore or HSM is the stronger
/// answer and is listed among RFC 09's future expansions; the interface here takes raw bytes, so
/// changing the source touches only this class.
/// </para>
/// <para>
/// "Owner-only" is a claim on every platform, not only where mode bits exist: see
/// <see cref="OwnerOnly"/>, which restricts the ACL on Windows rather than assuming the directory
/// carried the right one.
/// </para>
/// </remarks>
public static class LocalKeyFile
{
    public const int KeyLengthBytes = 32;

    public static byte[] LoadOrCreate(string path, string purpose)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            // Fail-closed on reload as well as on creation: a key file from an earlier run, or one
            // whose ACL has drifted, is re-restricted to the owner here, and if that cannot be
            // done the key is not handed back (F-3, docs/adr/0079). Reading a key from a file
            // Aurora can no longer prove is the owner's alone is the exact silent-continue this
            // closes.
            OwnerOnly.Require(path);

            var existing = File.ReadAllBytes(path);
            if (existing.Length != KeyLengthBytes)
            {
                // Regenerating would silently make every existing record unreadable or
                // unverifiable, destroying exactly what the key was protecting.
                throw new InvalidOperationException(
                    $"{purpose} key at '{path}' is {existing.Length} bytes; expected {KeyLengthBytes}.");
            }

            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(KeyLengthBytes);

        OwnerOnly.Write(path, FileMode.CreateNew, stream => stream.Write(key));

        return key;
    }
}
