namespace Aurora.Core.Abstractions;

/// <summary>Result of checking an operator passphrase.</summary>
public enum PassphraseOutcome
{
    /// <summary>No passphrase has been enrolled, so this deployment does not require one.</summary>
    NotEnrolled,

    /// <summary>The passphrase matched.</summary>
    Verified,

    /// <summary>The passphrase did not match.</summary>
    Rejected,

    /// <summary>Too many recent failures; attempts are refused until the lockout expires.</summary>
    LockedOut,
}

public sealed record PassphraseCheck(PassphraseOutcome Outcome, DateTimeOffset? LockedUntilUtc = null);

/// <summary>
/// Verifies the operator passphrase that turns an approval into a human act (docs/adr/0011).
/// </summary>
/// <remarks>
/// The problem this solves: <c>aurora_approve</c> is an MCP tool, so the agent can call it. Without
/// a secret the agent does not hold, an untrusted reasoner can approve its own request, and the
/// whole approval gate becomes decoration. Design 0001 assigned that job to a trusted desktop
/// dialog with a passphrase; the passphrase half works without any window.
/// </remarks>
public interface IPassphraseAuthenticator
{
    /// <summary>Whether a passphrase has been enrolled. When false, approvals proceed unguarded.</summary>
    bool IsEnrolled { get; }

    /// <summary>
    /// Enrolls a passphrase. Refuses when one already exists: replacing it is a revoke followed by
    /// a fresh enrollment, so overwriting can never happen by accident.
    /// </summary>
    void Enroll(string passphrase);

    /// <summary>Checks a candidate, counting failures and applying lockout.</summary>
    PassphraseCheck Verify(string? passphrase);

    /// <summary>Removes the enrollment. Approvals then proceed unguarded again.</summary>
    void Revoke();
}
