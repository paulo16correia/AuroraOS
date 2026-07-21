using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Tests.Support;

public sealed class FakeReasoner(ReasonerProposal? proposal) : IReasoner
{
    public ValueTask<ReasonerProposal?> ProposeAsync(
        string objective, IReadOnlyList<CapabilityDescriptor> catalog, CancellationToken ct) =>
        ValueTask.FromResult(proposal);
}

public sealed class FakeCapability(CapabilityDescriptor descriptor, Func<JsonElement, JsonElement> execute) : ICapability
{
    public CapabilityDescriptor Descriptor { get; } = descriptor;

    public int ExecuteCount { get; private set; }

    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        ExecuteCount++;
        return ValueTask.FromResult(execute(input));
    }

    public static CapabilityDescriptor LowReadOnly(string actionId, string schemaJson) =>
        new(actionId, actionId, "test capability",
            JsonDocument.Parse(schemaJson).RootElement.Clone(),
            Array.Empty<string>(), RiskLevel.Low, false);
}

public sealed class FakeRegistry(params ICapability[] capabilities) : ICapabilityRegistry
{
    private readonly Dictionary<string, ICapability> _map =
        capabilities.ToDictionary(c => c.Descriptor.ActionId, StringComparer.Ordinal);

    public IReadOnlyList<CapabilityDescriptor> List(string? query) =>
        _map.Values.Select(c => c.Descriptor).ToList();

    public bool TryGet(string actionId, [NotNullWhen(true)] out ICapability? capability) =>
        _map.TryGetValue(actionId, out capability);
}

public sealed class FakeValidator(bool isValid) : ISchemaValidator
{
    public SchemaValidationResult Validate(JsonElement schema, JsonElement input) =>
        isValid ? SchemaValidationResult.Valid : new SchemaValidationResult(false, ["invalid"]);
}

public sealed class FakePolicy(bool allow) : IPolicyEngine
{
    public PolicyDecision Evaluate(CapabilityDescriptor capability, JsonElement input, Principal principal) =>
        allow ? PolicyDecision.Allow("test.allow") : PolicyDecision.Deny("denied by test", "test.deny");
}

public sealed class FakeConsent(bool grant) : IConsentGate
{
    public ConsentOutcome Evaluate(CapabilityDescriptor capability, Principal principal) =>
        grant
            ? new ConsentOutcome(true, new ConsentInfo(ConsentDecision.AutoLow, "policy"))
            : new ConsentOutcome(false, new ConsentInfo(ConsentDecision.RequiresApproval, "session"));
}

public sealed class DirectExecutor : ICapabilityExecutor
{
    public ValueTask<JsonElement> ExecuteAsync(ICapability capability, JsonElement input, CancellationToken ct) =>
        capability.ExecuteAsync(input, ct);
}

public sealed class RecordingAuditStore : IAuditStore
{
    public List<string> Outcomes { get; } = [];

    public Task<string> AppendAsync(
        string principalClientId, string principalWindowsUser, string actionId, string inputHash, string outcome,
        CancellationToken ct)
    {
        Outcomes.Add(outcome);
        return Task.FromResult($"audit-{Outcomes.Count}");
    }

    public Task<AuditVerification> VerifyChainAsync(CancellationToken ct) =>
        Task.FromResult(new AuditVerification(true, null));
}

/// <summary>In-memory idempotency store implementing the real disposition semantics, for kernel tests.</summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed record Row(string RequestHash, string State, string? ResultJson);

    private readonly Dictionary<(string Client, string Key), Row> _rows = [];

    public Task<IdempotencyBegin> BeginAsync(Principal principal, string key, string requestHash, CancellationToken ct)
    {
        var k = (principal.ClientId, key);
        if (!_rows.TryGetValue(k, out var row))
        {
            _rows[k] = new Row(requestHash, IdempotencyState.Accepted, null);
            return Task.FromResult(new IdempotencyBegin(IdempotencyDisposition.Begin));
        }

        if (!string.Equals(row.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Task.FromResult(new IdempotencyBegin(IdempotencyDisposition.Conflict));
        }

        return Task.FromResult(row.State switch
        {
            IdempotencyState.Completed => new IdempotencyBegin(IdempotencyDisposition.ReplayCompleted, row.ResultJson),
            IdempotencyState.Failed => new IdempotencyBegin(IdempotencyDisposition.ReplayFailed, row.ResultJson),
            IdempotencyState.Unknown => new IdempotencyBegin(IdempotencyDisposition.Unknown),
            _ => new IdempotencyBegin(IdempotencyDisposition.InProgress),
        });
    }

    public Task<bool> MarkExecutingAsync(Principal principal, string key, CancellationToken ct)
    {
        var k = (principal.ClientId, key);
        if (_rows.TryGetValue(k, out var row) && row.State == IdempotencyState.Accepted)
        {
            _rows[k] = row with { State = IdempotencyState.Executing };
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task CompleteAsync(Principal principal, string key, string state, string resultJson, CancellationToken ct)
    {
        var k = (principal.ClientId, key);
        var requestHash = _rows.TryGetValue(k, out var existing) ? existing.RequestHash : string.Empty;
        _rows[k] = new Row(requestHash, state, resultJson);
        return Task.CompletedTask;
    }
}
