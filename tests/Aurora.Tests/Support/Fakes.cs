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

/// <summary>
/// A session store that never covers anything, for tests exercising the approval path alone.
/// With it, <c>SessionAwareConsentGate</c> behaves exactly like the pre-session gate.
/// </summary>
public sealed class NoConsentSessionStore : IConsentSessionStore
{
    public Task<ConsentSession> OpenAsync(Principal principal, CancellationToken ct) =>
        Task.FromResult(new ConsentSession(
            "none", principal.ClientId, principal.OsUser, "boot", "pv",
            ConsentSessionStatus.Active, 0, 0, "", ""));

    public Task<ConsentSessionUse> TryUseAsync(Principal principal, CancellationToken ct) =>
        Task.FromResult(new ConsentSessionUse(ConsentSessionUseOutcome.None));

    public Task<int> RevokeAllAsync(CancellationToken ct) => Task.FromResult(0);

    public Task<int> CountActiveAsync(CancellationToken ct) => Task.FromResult(0);
}

/// <summary>A passphrase guard the test controls; not enrolled by default, as most tests want.</summary>
public sealed class FakePassphrase(bool enrolled = false, string? expected = null) : IPassphraseAuthenticator
{
    public bool IsEnrolled { get; private set; } = enrolled;

    public bool LockedOut { get; set; }

    public void Enroll(string passphrase) => IsEnrolled = true;

    public PassphraseCheck Verify(string? passphrase)
    {
        if (!IsEnrolled) return new PassphraseCheck(PassphraseOutcome.NotEnrolled);
        if (LockedOut) return new PassphraseCheck(PassphraseOutcome.LockedOut, DateTimeOffset.MaxValue);
        return string.Equals(passphrase, expected, StringComparison.Ordinal)
            ? new PassphraseCheck(PassphraseOutcome.Verified)
            : new PassphraseCheck(PassphraseOutcome.Rejected);
    }

    public void Revoke() => IsEnrolled = false;
}

/// <summary>A server identity with a boot id the test controls, to simulate restarts.</summary>
public sealed class FakeServerIdentity(string bootId) : IServerIdentity
{
    public string BootId { get; } = bootId;
}

/// <summary>A policy whose version the test controls, to simulate a rule change.</summary>
public sealed class VersionedFakePolicy(bool allow, string version) : IPolicyEngine
{
    public string Version { get; } = version;

    public PolicyDecision Evaluate(CapabilityDescriptor capability, JsonElement input, Principal principal) =>
        allow ? PolicyDecision.Allow("test") : PolicyDecision.Deny("denied", "test");
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
public sealed class FakeApprovalStore(int pending = 0) : IApprovalStore
{
    private readonly Dictionary<string, ApprovalRecord> _byId = [];

    /// <summary>Plus any seeded by the constructor, for tests about what a pending count causes.</summary>
    public Task<int> CountPendingAsync(CancellationToken ct) =>
        Task.FromResult(_byId.Values.Count(r => r.Status == ApprovalStatus.Pending) + pending);

    private int _sequence;

    /// <summary>Puts a pending approval on file, for tests about deciding one.</summary>
    public ApprovalRecord Seed(Principal principal, string actionId, string scopeHash)
    {
        var record = new ApprovalRecord(
            $"approval-{++_sequence}", principal.ClientId, principal.OsUser, actionId, scopeHash,
            ApprovalStatus.Pending, "2026-01-01T00:00:00.0000000+00:00",
            "2099-01-01T00:00:00.0000000+00:00", null);

        _byId[record.ApprovalId] = record;
        return record;
    }

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
            id, principal.ClientId, principal.OsUser, actionId, scopeHash, ApprovalStatus.Pending, "t0", "t1", null);
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

    public Task<IReadOnlyList<AuditRecordView>> QueryAsync(
        long afterSequence, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AuditRecordView>>([]);

    public Task<string?> HeadHashAsync(CancellationToken ct) =>
        Task.FromResult<string?>(Outcomes.Count == 0 ? null : $"audit-{Outcomes.Count}");

    public Task<string> SealBreakAsync(string reason, string actor, CancellationToken ct) =>
        AppendAsync(
            new AuditEntry(actor, actor, "audit.chain_break", reason, "chain_break_acknowledged"), ct);
}


/// <summary>
/// In-memory idempotency store implementing the real disposition semantics, for kernel tests.
/// Rows carry no timestamps, so staleness reconciliation is covered against the SQLite store.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    /// <summary>
    /// Not implemented, and returning nothing rather than pretending.
    /// </summary>
    /// <remarks>
    /// This fake keeps no timestamps, so it cannot judge staleness. A test whose subject is what a
    /// restart left behind needs <c>SqliteIdempotencyStore</c>; using this one would be reading a
    /// stub's zero as a finding.
    /// </remarks>
    public Task<int> ReconcileStaleAsync(TimeSpan staleAfter, CancellationToken ct) => Task.FromResult(0);

    /// <inheritdoc cref="ReconcileStaleAsync"/>
    public Task<IReadOnlyList<string>> ListUnknownAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

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

/// <summary>
/// A resource probe that reports whatever the test says the machine is doing.
/// </summary>
/// <remarks>
/// The real probe reports the host, which makes any test that depends on it a test of the machine
/// it happened to run on. This is how the policy above it — what counts as constrained, what gives
/// way first — gets tested against stated conditions instead.
/// </remarks>
public sealed class FakeResourceProbe(
    double? cpu = 0.1, double? memory = 0.2, double? disk = 0.3) : IResourceProbe
{
    public double? Cpu { get; set; } = cpu;

    public double? Memory { get; set; } = memory;

    public double? Disk { get; set; } = disk;

    public ResourceReading Read() => new(Cpu, Memory, Disk);

    /// <summary>A host that reports nothing at all.</summary>
    public static FakeResourceProbe Blind() => new(null, null, null);
}

/// <summary>
/// An Event Bus that records what was published and does nothing else.
/// </summary>
/// <remarks>
/// For unit tests whose subject is not the bus. It still runs the declared-contract check, because
/// a stub that accepted anything would hide the mistakes LAW-007's declaration exists to catch.
/// </remarks>
public sealed class RecordingEventBus : IEventBus
{
    public List<OutboxWrite> Published { get; } = [];

    public Task<DomainEvent> PublishAsync(OutboxWrite write, CancellationToken ct)
    {
        if (!EventCatalogue.TryGet(write.Type, write.SchemaVersion, out EventContract? contract)
            || contract!.Producer != write.Producer)
        {
            throw new EventContractException($"'{write.Type}' is not declared for '{write.Producer}'.");
        }

        Published.Add(write);

        return Task.FromResult(new DomainEvent(
            Guid.NewGuid().ToString("N"), write.Type, write.SchemaVersion, write.Producer,
            "2026-01-01T00:00:00.0000000+00:00", write.CorrelationId, write.CausationId,
            write.AggregateRef, write.PayloadJson, write.PayloadRef, write.SensitivityClass,
            write.IdempotencyKey, "hash"));
    }

    public Task<IDbTransactionScope> BeginAsync(CancellationToken ct) =>
        throw new NotSupportedException("This bus records publications; it owns no transaction.");

    public Task<Subscription> SubscribeAsync(Subscription subscription, CancellationToken ct) =>
        Task.FromResult(subscription);

    public Task<int> PumpAsync(IEventConsumer consumer, CancellationToken ct) => Task.FromResult(0);

    public Task<Delivery?> AckAsync(string deliveryId, CancellationToken ct) =>
        Task.FromResult<Delivery?>(null);

    public Task<IReadOnlyList<Delivery>> ReplayAsync(string subscriptionId, long cursor, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Delivery>>([]);

    public Task<IReadOnlyList<Delivery>> DeadLettersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Delivery>>([]);

    public Task<IReadOnlyList<SequencedEvent>> ReadAsync(
        long afterSequence, int limit, string maxSensitivity, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SequencedEvent>>([]);
}

/// <summary>
/// A catalogue that declares whatever it is asked about.
/// </summary>
/// <remarks>
/// For tests whose subject is the bus mechanics — fan-out, retry, dead-lettering, replay — rather
/// than the declaration itself. The declaration is checked against the real
/// <see cref="DeclaredEventCatalogue"/> in its own tests, including that the server registers that
/// one and not this.
/// </remarks>
public sealed class PermissiveEventCatalogue : IEventCatalogue
{
    public IReadOnlyList<EventContract> Declared => EventCatalogue.Declared;

    public bool TryValidate(OutboxWrite write, out string? violation)
    {
        violation = null;
        return true;
    }
}

/// <summary>
/// A machine with no desktop.
/// </summary>
/// <remarks>
/// The default for tests: <see cref="IsAvailable"/> is false, so the kernel takes the supplied-
/// passphrase path and the tests stay about approvals rather than about windows.
/// </remarks>
public sealed class NoOperatorPrompt : IOperatorPrompt
{
    public bool IsAvailable => false;

    public Task<OperatorAnswer> AskAsync(
        string title, string question, bool secret, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(new OperatorAnswer(false, null, "no desktop in tests"));

    public Task NotifyAsync(string title, string message, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A desktop that answers whatever the test says the person typed.</summary>
public sealed class ScriptedOperatorPrompt(string? answer) : IOperatorPrompt
{
    public bool IsAvailable => true;

    public int Asked { get; private set; }

    public string? LastQuestion { get; private set; }

    public Task<OperatorAnswer> AskAsync(
        string title, string question, bool secret, TimeSpan timeout, CancellationToken ct)
    {
        Asked++;
        LastQuestion = question;

        return Task.FromResult(answer is null
            ? new OperatorAnswer(false, null, "dismissed")
            : new OperatorAnswer(true, answer, "answered"));
    }

    public Task NotifyAsync(string title, string message, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>An incident service that remembers what it was asked to open and contains nothing.</summary>
/// <remarks>
/// Real containment revokes consent sessions and disables tools, which is not what a test about
/// maintenance upkeep wants happening underneath it. What those tests need to know is whether the
/// pass raised the incident at all.
/// </remarks>
public sealed class RecordingIncidentService : IIncidentService
{
    public List<SecurityEvent> Opened { get; } = [];

    public Task<Incident> OpenAsync(SecurityEvent securityEvent, CancellationToken ct)
    {
        Opened.Add(securityEvent);

        return Task.FromResult(new Incident(
            $"incident-{Opened.Count}", securityEvent, IncidentStatus.Contained, ["recorded"],
            securityEvent.DetectedAtUtc, securityEvent.DetectedAtUtc, null, null));
    }

    public Task<Incident> ResolveAsync(
        string incidentId, string resolution, string actor, CancellationToken ct) =>
        throw new NotSupportedException("The fake opens incidents; it does not close them.");

    public Task<Incident?> GetAsync(string incidentId, CancellationToken ct) =>
        Task.FromResult<Incident?>(null);

    public Task<IReadOnlyList<Incident>> OpenIncidentsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Incident>>([]);
}

/// <summary>A clock guard that says whatever the test says.</summary>
public sealed class FakeClockGuard(bool trustworthy = true) : IClockGuard
{
    public Task<ClockVerdict> CheckAsync(CancellationToken ct) =>
        Task.FromResult(new ClockVerdict(trustworthy, trustworthy ? "fine" : "went backwards"));
}
