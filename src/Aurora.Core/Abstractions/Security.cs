using System.Text.Json;
using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static readonly SchemaValidationResult Valid = new(true, []);
}

/// <summary>Validates an input document against a capability's JSON Schema.</summary>
public interface ISchemaValidator
{
    SchemaValidationResult Validate(JsonElement schema, JsonElement input);
}

/// <summary>Fail-closed authorization. Evaluated with the concrete input, immediately before the effect.</summary>
public interface IPolicyEngine
{
    PolicyDecision Evaluate(CapabilityDescriptor capability, JsonElement input, Principal principal);

    /// <summary>
    /// Identifies the current rule set. Consent sessions are bound to it, so changing the rules
    /// must change this string — otherwise a grant issued under the old policy would survive a
    /// tightening of the new one (docs/adr/0010).
    /// </summary>
    string Version => "v1";
}

/// <summary>
/// Consent gate. LOW auto-grants. A capability explicitly marked
/// <see cref="CapabilityDescriptor.ApprovalRequired"/> is gated by a persisted, one-time approval
/// scoped to the exact action + input (It.2, first increment — see <see cref="IApprovalStore"/>).
/// Anything else at MEDIUM+ has no consent path yet and stays refused.
/// </summary>
public interface IConsentGate
{
    Task<ConsentOutcome> EvaluateAsync(
        CapabilityDescriptor capability, JsonElement input, string scopeHash, Principal principal, CancellationToken ct);
}

/// <summary>Untrusted NL→action proposer (It.1+). Returns null when it cannot resolve.</summary>
public interface IReasoner
{
    ValueTask<ReasonerProposal?> ProposeAsync(string objective, IReadOnlyList<CapabilityDescriptor> catalog, CancellationToken ct);
}
