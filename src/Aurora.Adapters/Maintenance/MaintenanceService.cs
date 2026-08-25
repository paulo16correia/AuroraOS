using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Maintenance;

/// <summary>
/// One pass of upkeep: expire, decay, reconcile, notice.
/// </summary>
/// <remarks>
/// Everything this does is bookkeeping on Aurora's own records. It produces due runs and detected
/// needs and runs neither — every one of those still goes through the cognitive cycle, with its
/// policy and its approval. A maintenance loop that could act on what it found would be the widest
/// bypass in the system, because it runs unattended and by definition nobody is watching.
/// </remarks>
public sealed class MaintenanceService : IMaintenanceService
{
    /// <summary>How long a reservation may sit in EXECUTING before it is called indeterminate.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private readonly IScheduler _scheduler;
    private readonly ISignalService _signals;
    private readonly INeedsService _needs;
    private readonly ISituationService _situation;
    private readonly IResourceModel _resources;
    private readonly IIdempotencyStore _idempotency;
    private readonly IApprovalStore _approvals;
    private readonly IRetentionService _retention;
    private readonly RetentionPolicy _retentionPolicy;
    private readonly IEventBus _bus;
    private readonly IOperatorPrompt _prompt;
    private readonly IClock _clock;

    public MaintenanceService(
        IScheduler scheduler,
        ISignalService signals,
        INeedsService needs,
        ISituationService situation,
        IResourceModel resources,
        IIdempotencyStore idempotency,
        IApprovalStore approvals,
        IRetentionService retention,
        RetentionPolicy retentionPolicy,
        IEventBus bus,
        IOperatorPrompt prompt,
        IClock clock)
    {
        _scheduler = scheduler;
        _signals = signals;
        _needs = needs;
        _situation = situation;
        _resources = resources;
        _idempotency = idempotency;
        _approvals = approvals;
        _retention = retention;
        _retentionPolicy = retentionPolicy;
        _bus = bus;
        _prompt = prompt;
        _clock = clock;
    }

    public async Task<MaintenanceReport> RunAsync(SituationContext context, CancellationToken ct)
    {
        // Expire and decay first, so everything after this reads a current picture rather than one
        // padded with things that stopped mattering hours ago.
        var expired = await _signals.ExpireDueAsync(ct).ConfigureAwait(false);
        var decayed = await _needs.DecayAsync(ct).ConfigureAwait(false);

        // A process that died mid-effect leaves reservations in EXECUTING. Moving the stale ones to
        // UNKNOWN is not a claim about what happened — it is the opposite, and it is what stops
        // those keys being wedged forever.
        var reconciled = await _idempotency.ReconcileStaleAsync(StaleAfter, ct).ConfigureAwait(false);

        // Working by-products past their retention. Closed records only, and never the audit
        // chain, memories, goals or missions — the pass that forgets must not be able to reach
        // the record of what happened.
        RetentionReport removed = await _retention
            .ApplyAsync(_retentionPolicy, ct).ConfigureAwait(false);

        IReadOnlyList<ScheduleRun> due = await _scheduler.TickAsync(_clock.UtcNow, ct).ConfigureAwait(false);

        foreach (ScheduleRun started in due.Where(r => r.Status == ScheduleRunStatus.Started))
        {
            // Left in flight by a crash. Settled from its cycle, never assumed.
            await _scheduler.ReconcileAsync(started.Id, ct).ConfigureAwait(false);
        }

        IReadOnlyList<Schedule> failed = (await _scheduler.ListAsync(null, ct).ConfigureAwait(false))
            .Where(s => s.Status == ScheduleStatus.Failed)
            .ToList();

        ResourceState resources = await _resources.ObserveAsync(ct).ConfigureAwait(false);
        IReadOnlyList<Signal> pending = await _signals.PendingAsync(ct).ConfigureAwait(false);

        // What is left null is what this pass did not measure, and it stays null rather than
        // becoming a zero. Overdue goals have no read-only count yet — the Planner's only way to
        // find them also applies an action to them — and backup age and consolidation backlog have
        // no source here. Reporting those as "none" would be Aurora inventing good news.
        var snapshot = new NeedsSnapshot(
            DeadLetters: (await _bus.DeadLettersAsync(ct).ConfigureAwait(false)).Count,
            PendingApprovals: await _approvals.CountPendingAsync(ct).ConfigureAwait(false),
            MissedScheduleRuns: due.Count(r => r.Status == ScheduleRunStatus.Missed),
            UnreconciledReservations: reconciled);

        await _needs.DetectAsync(snapshot, pending, ct).ConfigureAwait(false);
        IReadOnlyList<Need> ranked = await _needs.RankAsync(ct).ConfigureAwait(false);

        SituationAssessment situation = await _situation.AssessAsync(context, ct).ConfigureAwait(false);

        // The pass is itself a fact worth publishing: an upkeep loop nobody can see the results of
        // is indistinguishable from one that stopped running.
        await _bus.PublishAsync(
            new OutboxWrite(
                EventCatalogue.MaintenancePassCompleted, 1, EventCatalogue.Producers.Maintenance, Guid.NewGuid().ToString("N"),
                Sensitivity.Private,
                PayloadJson:
                    $$"""{"signals_expired":{{expired}},"needs":{{ranked.Count}},"due_runs":{{due.Count(r => r.Status == ScheduleRunStatus.Due)}},"resources":"{{resources.Status}}","posture":"{{situation.RiskPosture}}"}"""),
            ct).ConfigureAwait(false);

        // Alerts, in the only form that reaches somebody who is not looking at the panel. Sent for
        // an incident and for nothing else: a notification per upkeep pass would be a notification
        // people turn off, and then the one that mattered arrives silenced.
        IReadOnlyList<Need> incidents = ranked.Where(n => NeedKind.IsIncident(n.Kind)).ToList();

        if (incidents.Count > 0 || situation.RiskPosture == RiskPosture.Emergency)
        {
            await _prompt.NotifyAsync(
                "Aurora needs attention",
                incidents.Count > 0
                    ? $"{incidents.Count} thing(s) need looking at: {incidents[0].SatisfactionCondition}"
                    : $"Resources are {resources.Status}.",
                ct).ConfigureAwait(false);
        }

        return new MaintenanceReport(
            Iso(_clock.UtcNow), expired, decayed, reconciled, failed.Count,
            due.Where(r => r.Status == ScheduleRunStatus.Due).Select(r => r.Id).ToList(),
            ranked.Select(n => n.Id).ToList(),
            resources.Status, situation.RiskPosture, removed,
            [.. snapshot.Unmeasured, .. resources.Unmeasured]);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
