using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Aurora.Adapters.Persistence;

/// <summary>
/// Loads, or creates on first use, the 32-byte key that signs the audit chain (docs/adr/0005).
/// </summary>
/// <remarks>
/// The key deliberately lives outside the SQLite file: the whole point is that write access to the
/// database must not be enough to forge a consistent chain.
/// <para>
/// Honest limitation: a file on the same disk only raises the bar. An attacker who can read
/// arbitrary files as this user gets the key and can forge freely. Real separation means an OS
/// keystore (DPAPI/Keychain) or an HSM, and is deferred — but the interface here takes raw key
/// bytes, so swapping the source touches only this class.
/// </para>
/// </remarks>
public static class AuditKeyFile
{
    public const int KeyLengthBytes = 32;

    public static byte[] LoadOrCreate(string path)
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
                // Refuse to silently regenerate: a wrong-length key means every existing record
                // would fail verification, and overwriting it would destroy the evidence.
                throw new InvalidOperationException(
                    $"Audit key at '{path}' is {existing.Length} bytes; expected {KeyLengthBytes}.");
            }

            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(KeyLengthBytes);

        // Create with owner-only permissions before any bytes land on disk.
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
