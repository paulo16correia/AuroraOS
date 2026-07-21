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
}

/// <summary>Consent gate. It.0: LOW auto-grants; ≥MEDIUM is refused (real sessions arrive in It.2).</summary>
public interface IConsentGate
{
    ConsentOutcome Evaluate(CapabilityDescriptor capability, Principal principal);
}

/// <summary>Untrusted NL→action proposer (It.1+). Returns null when it cannot resolve.</summary>
public interface IReasoner
{
    ValueTask<ReasonerProposal?> ProposeAsync(string objective, IReadOnlyList<CapabilityDescriptor> catalog, CancellationToken ct);
}
