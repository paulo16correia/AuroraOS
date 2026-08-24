using System.Runtime.InteropServices;
using System.Security.Cryptography;

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

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(key);
        }

        return key;
    }
}
