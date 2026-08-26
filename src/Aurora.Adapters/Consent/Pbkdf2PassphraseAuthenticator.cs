using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Consent;

/// <summary>Tuning for the passphrase KDF and lockout policy (docs/adr/0011).</summary>
public sealed record PassphraseOptions(int Iterations = 600_000, int FailuresBeforeLockout = 5, int MaxLockoutMinutes = 15)
{
    public static readonly PassphraseOptions Default = new();
}

/// <summary>
/// File-backed operator passphrase, verified with PBKDF2-HMAC-SHA256 (docs/adr/0011).
/// </summary>
/// <remarks>
/// PBKDF2 rather than Argon2 because PBKDF2 is in the BCL and Argon2 would mean a new NuGet
/// dependency; design 0001 requires a supply-chain verdict before any package is added. Argon2 is
/// the better KDF and is recorded as deferred, not dismissed.
/// <para>
/// The verifier lives in its own owner-only file rather than in SQLite, for the same reason the
/// audit key does: an attacker with write access to the database could otherwise replace the
/// verifier with a hash of a passphrase they know and then approve their own requests.
/// </para>
/// </remarks>
public sealed class Pbkdf2PassphraseAuthenticator : IPassphraseAuthenticator
{
    private sealed record State(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("algorithm")] string Algorithm,
        [property: JsonPropertyName("iterations")] int Iterations,
        [property: JsonPropertyName("salt")] string Salt,
        [property: JsonPropertyName("verifier")] string Verifier,
        [property: JsonPropertyName("failedAttempts")] int FailedAttempts,
        [property: JsonPropertyName("lockedUntilUtc")] string? LockedUntilUtc);

    private const string Algorithm = "PBKDF2-HMAC-SHA256";
    private const int SaltBytes = 16;
    private const int VerifierBytes = 32;

    private readonly string _path;
    private readonly IClock _clock;
    private readonly PassphraseOptions _options;
    private readonly object _sync = new();

    public Pbkdf2PassphraseAuthenticator(string path, IClock clock, PassphraseOptions options)
    {
        _path = path;
        _clock = clock;
        _options = options;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public bool IsEnrolled
    {
        get
        {
            lock (_sync)
            {
                return Read() is not null;
            }
        }
    }

    public void Enroll(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 8)
        {
            throw new ArgumentException("Passphrase must be at least 8 characters.", nameof(passphrase));
        }

        lock (_sync)
        {
            if (Read() is not null)
            {
                throw new InvalidOperationException(
                    "A passphrase is already enrolled; revoke it before enrolling another.");
            }

            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var verifier = Derive(passphrase, salt, _options.Iterations);

            Write(new State(
                1, Algorithm, _options.Iterations,
                Convert.ToBase64String(salt), Convert.ToBase64String(verifier),
                0, null));
        }
    }

    public PassphraseCheck Verify(string? passphrase)
    {
        lock (_sync)
        {
            State? state = Read();
            if (state is null)
            {
                return new PassphraseCheck(PassphraseOutcome.NotEnrolled);
            }

            var now = _clock.UtcNow;
            if (state.LockedUntilUtc is { } lockedText
                && DateTimeOffset.TryParse(
                    lockedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lockedUntil)
                && lockedUntil > now)
            {
                // Refuse without even hashing: throttling that still does the work is not throttling.
                return new PassphraseCheck(PassphraseOutcome.LockedOut, lockedUntil);
            }

            if (string.IsNullOrEmpty(passphrase))
            {
                return Fail(state, now);
            }

            var candidate = Derive(passphrase, Convert.FromBase64String(state.Salt), state.Iterations);
            var expected = Convert.FromBase64String(state.Verifier);

            if (!CryptographicOperations.FixedTimeEquals(candidate, expected))
            {
                return Fail(state, now);
            }

            Write(state with { FailedAttempts = 0, LockedUntilUtc = null });
            return new PassphraseCheck(PassphraseOutcome.Verified);
        }
    }

    public void Revoke()
    {
        lock (_sync)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }

    /// <summary>Counts the failure and locks out once the threshold is crossed, backing off exponentially.</summary>
    private PassphraseCheck Fail(State state, DateTimeOffset now)
    {
        var failures = state.FailedAttempts + 1;

        if (failures < _options.FailuresBeforeLockout)
        {
            Write(state with { FailedAttempts = failures });
            return new PassphraseCheck(PassphraseOutcome.Rejected);
        }

        var over = failures - _options.FailuresBeforeLockout;
        var minutes = Math.Min(Math.Pow(2, over), _options.MaxLockoutMinutes);
        var until = now.AddMinutes(minutes);

        Write(state with
        {
            FailedAttempts = failures,
            LockedUntilUtc = until.ToString("O", CultureInfo.InvariantCulture),
        });

        return new PassphraseCheck(PassphraseOutcome.LockedOut, until);
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, VerifierBytes);

    private State? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
        }
        catch (JsonException)
        {
            // A corrupt file must not silently disable the guard: treat it as enrolled-but-unusable
            // by refusing every attempt rather than reporting NotEnrolled.
            return new State(1, Algorithm, _options.Iterations, Convert.ToBase64String(new byte[SaltBytes]),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(VerifierBytes)), 0, null);
        }
    }

    private void Write(State state)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(_path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(state));
    }
}
