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

public sealed class FakeConsent : IConsentGate
{
    private readonly ConsentOutcome _outcome;

    public FakeConsent(bool grant, string? decision = null, string? approvalId = null)
    {
        var effectiveDecision = decision ?? (grant ? ConsentDecision.AutoLow : ConsentDecision.Denied);
        _outcome = new ConsentOutcome(grant, new ConsentInfo(effectiveDecision, "test", approvalId));
    }

    public Task<ConsentOutcome> EvaluateAsync(
        CapabilityDescriptor capability, JsonElement input, string scopeHash, Principal principal, CancellationToken ct) =>
        Task.FromResult(_outcome);
}

/// <summary>In-memory <see cref="IApprovalStore"/> mirroring the real one-time-per-scope semantics.</summary>
public sealed class FakeApprovalStore : IApprovalStore
{
    private readonly Dictionary<string, ApprovalRecord> _byId = [];

    public Task<int> CountPendingAsync(CancellationToken ct) =>
        Task.FromResult(_byId.Values.Count(r => r.Status == ApprovalStatus.Pending));

    private int _sequence;

    public Task<ApprovalEvaluation> EvaluateAsync(Principal principal, string actionId, string scopeHash, CancellationToken ct)
    {
        var existing = _byId.Values.FirstOrDefault(a =>
            a.PrincipalClientId == principal.ClientId && a.ActionId == actionId && a.ScopeHash == scopeHash
            && a.Status is ApprovalStatus.Pending or ApprovalStatus.Approved or ApprovalStatus.Rejected);

        if (existing is { Status: ApprovalStatus.Approved })
        {
            _byId[existing.ApprovalId] = existing with { Status = ApprovalStatus.Consumed };
            return Task.FromResult(new ApprovalEvaluation(ApprovalOutcome.Consumed, existing.ApprovalId));
        }

        if (existing is { Status: ApprovalStatus.Rejected })
        {
            return Task.FromResult(new ApprovalEvaluation(ApprovalOutcome.Rejected, existing.ApprovalId));
        }

        if (existing is { Status: ApprovalStatus.Pending })
        {
            return Task.FromResult(new ApprovalEvaluation(ApprovalOutcome.Pending, existing.ApprovalId));
        }

        var id = $"appr-{++_sequence}";
        _byId[id] = new ApprovalRecord(
            id, principal.ClientId, principal.WindowsUser, actionId, scopeHash, ApprovalStatus.Pending, "t0", "t1", null);
        return Task.FromResult(new ApprovalEvaluation(ApprovalOutcome.Pending, id));
    }

    public Task<ApprovalDecideResult> DecideAsync(Principal principal, string approvalId, bool approve, CancellationToken ct)
    {
        if (!_byId.TryGetValue(approvalId, out var record) || record.PrincipalClientId != principal.ClientId)
        {
            return Task.FromResult(new ApprovalDecideResult(ApprovalDecideOutcome.NotFound, null));
        }

        if (record.Status != ApprovalStatus.Pending)
        {
            return Task.FromResult(new ApprovalDecideResult(ApprovalDecideOutcome.NotPending, null));
        }

        var updated = record with { Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected, DecidedAtUtc = "t2" };
        _byId[approvalId] = updated;
        return Task.FromResult(new ApprovalDecideResult(ApprovalDecideOutcome.Decided, updated));
    }
}

/// <summary>Fixed-principal <see cref="IPrincipalAccessor"/> for unit tests.</summary>
public sealed class FakePrincipalAccessor(Principal principal) : IPrincipalAccessor
{
    public Principal Current { get; } = principal;
}

public sealed class DirectExecutor : ICapabilityExecutor
{
    public ValueTask<JsonElement> ExecuteAsync(ICapability capability, JsonElement input, CancellationToken ct) =>
        capability.ExecuteAsync(input, ct);
}

public sealed class RecordingAuditStore : IAuditStore
{
    public List<string> Outcomes { get; } = [];

    /// <summary>Full entries, so tests can assert on the decision context and not just the outcome.</summary>
    public List<AuditEntry> Entries { get; } = [];

    public Task<string> AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        Outcomes.Add(entry.Outcome);
        Entries.Add(entry);
        return Task.FromResult($"audit-{Outcomes.Count}");
    }

    public Task<AuditVerification> VerifyChainAsync(CancellationToken ct) =>
        Task.FromResult(new AuditVerification(true, null));
}

/// <summary>
/// In-memory idempotency store implementing the real disposition semantics, for kernel tests.
/// Rows carry no timestamps, so staleness reconciliation is covered against the SQLite store.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    public Task<int> ReconcileStaleAsync(TimeSpan staleAfter, CancellationToken ct) => Task.FromResult(0);

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

    public Task AbandonAsync(Principal principal, string key, CancellationToken ct)
    {
        var k = (principal.ClientId, key);
        if (_rows.TryGetValue(k, out var row) && row.State == IdempotencyState.Accepted)
        {
            _rows.Remove(k);
        }

        return Task.CompletedTask;
    }
}
