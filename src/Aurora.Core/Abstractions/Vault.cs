using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Raised when a lease is refused. The message never contains a secret value.</summary>
public sealed class VaultException : Exception
{
    public VaultException(string message) : base(message)
    {
    }
}

/// <summary>Receives a secret for the duration of one call.</summary>
public delegate TResult SecretUse<out TResult>(ReadOnlySpan<char> secret);

/// <summary>
/// A short-lived, single-use grant of one secret to one tool call (RFC 09).
/// </summary>
/// <remarks>
/// The value is never a property and is never returned: it is handed to a callback for the length
/// of one call and the buffer is cleared on dispose. RFC 040 requires that the value never enters
/// the domain, and the cheapest way to keep that true is to give callers no way to hold on to it.
/// <para>
/// <see cref="ToString"/> is overridden to a redacted form so the handle cannot leak a value into
/// a log line by accident (RFC 09 rule 2: redacted before any recording).
/// </para>
/// </remarks>
public sealed class EphemeralSecretHandle : IDisposable
{
    private char[]? _buffer;
    private int _length;

    public EphemeralSecretHandle(
        string leaseId, string secretReferenceId, ToolCallRef toolCall, DateTimeOffset expiresAtUtc, char[] value)
    {
        LeaseId = leaseId;
        SecretReferenceId = secretReferenceId;
        ToolCall = toolCall;
        ExpiresAtUtc = expiresAtUtc;
        _buffer = value;
        _length = value.Length;
    }

    public string LeaseId { get; }

    public string SecretReferenceId { get; }

    public ToolCallRef ToolCall { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsSpent { get; private set; }

    /// <summary>
    /// Hands the secret to <paramref name="use"/> exactly once. The handle is spent afterwards,
    /// so a leaked handle cannot be replayed later in the process.
    /// </summary>
    public TResult Use<TResult>(SecretUse<TResult> use)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);

        if (IsSpent)
        {
            throw new VaultException("This lease has already been used.");
        }

        IsSpent = true;
        return use(_buffer.AsSpan(0, _length));
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            Array.Clear(_buffer);
            _buffer = null;
            _length = 0;
        }
    }

    /// <summary>Redacted on purpose; a handle must not be able to print a secret.</summary>
    public override string ToString() => $"EphemeralSecretHandle({SecretReferenceId}, [redacted])";
}

/// <summary>
/// Stores and leases secrets without exposing them to the model (RFC 09, RFC 040).
/// </summary>
public interface IVault
{
    /// <summary>Registers a secret. The value is encrypted before it reaches storage.</summary>
    Task<SecretReference> PutAsync(
        string purpose, IReadOnlyList<string> allowedToolIds, string value,
        DateTimeOffset? rotationDueAtUtc, CancellationToken ct);

    /// <summary>The reference only; never the value.</summary>
    Task<SecretReference?> GetReferenceAsync(string secretReferenceId, CancellationToken ct);

    /// <summary>
    /// The reference filed under <paramref name="purpose"/>, if there is one.
    /// </summary>
    /// <remarks>
    /// Secrets are keyed by an opaque id, which suits a caller that stored one and kept the
    /// reference. It does not suit a caller that needs "whatever is on file for this plugin under
    /// this name" — a plugin declares the secrets it needs by name and cannot know an id Aurora
    /// generated. The purpose is that name, and it is unique.
    /// </remarks>
    Task<SecretReference?> FindByPurposeAsync(string purpose, CancellationToken ct);

    /// <summary>
    /// Leases a secret to one tool call. Refuses when the reference is unknown, revoked, expired,
    /// or not allowed for the calling tool.
    /// </summary>
    Task<EphemeralSecretHandle> LeaseAsync(
        string secretReferenceId, ToolCallRef toolCall, CancellationToken ct);

    /// <summary>Marks a secret revoked. Existing handles are already spent or short-lived.</summary>
    Task<bool> RevokeAsync(string secretReferenceId, CancellationToken ct);

    /// <summary>Replaces the value and returns the reference to ACTIVE.</summary>
    Task<SecretReference> RotateAsync(
        string secretReferenceId, string newValue, DateTimeOffset? rotationDueAtUtc, CancellationToken ct);

    /// <summary>References whose rotation is overdue, for the operator surface.</summary>
    Task<IReadOnlyList<SecretReference>> RotationOverdueAsync(CancellationToken ct);
}
