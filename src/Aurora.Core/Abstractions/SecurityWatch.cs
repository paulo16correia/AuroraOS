namespace Aurora.Core.Abstractions;

/// <summary>
/// Notices attempts to get authority that was not granted, and raises incidents for them
/// (RFC 09 rule 5).
/// </summary>
/// <remarks>
/// The two event types Aurora declared and nobody raised. Both exist because the refusal on its own
/// is not enough: a request that is denied is handled, and a request that is denied fifty times is
/// somebody working at it. The first is a decision; the second is an incident.
/// <para>
/// Deliberately narrow. This does not decide anything and cannot allow anything — every caller has
/// already refused before it reaches here. All it does is count, and open an incident when the
/// counting means something.
/// </para>
/// </remarks>
public interface ISecurityWatch
{
    /// <summary>
    /// One credential that did not verify.
    /// </summary>
    /// <param name="source">
    /// What is being tried against — a surface name, never the credential and never anything from
    /// the request. An incident record that quoted the token somebody guessed would be an incident
    /// record holding a near-miss of the real one.
    /// </param>
    Task AuthenticationFailedAsync(string source, CancellationToken ct);

    /// <summary>
    /// One attempt to use authority that was not granted.
    /// </summary>
    /// <param name="resourceRef">
    /// What was affected, in the form containment understands — <c>plugin/{id}</c>, or empty when
    /// the answer is "this instance".
    /// </param>
    Task PrivilegeEscalationAsync(
        string actor, string resourceRef, string detail, CancellationToken ct);
}
