using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;

namespace Aurora.Adapters.Applications;

/// <summary>
/// Reviews what Aurora did and what is waiting on it, through the full cycle.
/// </summary>
/// <remarks>
/// The second application the frozen order allows: low-risk, reading-only, no external tool. Every
/// source is Aurora's own record of itself.
/// <para>
/// It could have been a query. It is a cycle because a briefing is a claim about what happened, and
/// a claim is something Aurora decides to make and is then accountable for — the same standard as
/// anything else it says.
/// </para>
/// </remarks>
public sealed class DailyReviewApplication : IReviewApplication
{
    /// <summary>Reading Aurora's own records reaches nothing and changes nothing.</summary>
    private const string ReviewPolicy = "policy.review_reads_own_records";

    private readonly ICognitiveCycle _cycle;
    private readonly IEventBus _bus;
    private readonly IAttentionSystem _attention;
    private readonly IWorkingMemory _working;
    private readonly IWorldModel _world;
    private readonly IDecisionEngine _decisions;
    private readonly IObservationService _observations;
    private readonly IAuditStore _audit;
    private readonly INeedsService _needs;
    private readonly ISignalService _signals;
    private readonly IScheduler _scheduler;
    private readonly IMissionService _missions;
    private readonly ICuriosityEngine _curiosity;
    private readonly ISituationService _situation;
    private readonly IResourceModel _resources;
    private readonly AttentionPolicy _attentionPolicy;
    private readonly IClock _clock;

    public DailyReviewApplication(
        ICognitiveCycle cycle,
        IEventBus bus,
        IAttentionSystem attention,
        IWorkingMemory working,
        IWorldModel world,
        IDecisionEngine decisions,
        IObservationService observations,
        IAuditStore audit,
        INeedsService needs,
        ISignalService signals,
        IScheduler scheduler,
        IMissionService missions,
        ICuriosityEngine curiosity,
        ISituationService situation,
        IResourceModel resources,
        AttentionPolicy attentionPolicy,
        IClock clock)
    {
        _cycle = cycle;
        _bus = bus;
        _attention = attention;
        _working = working;
        _world = world;
        _decisions = decisions;
        _observations = observations;
        _audit = audit;
        _needs = needs;
        _signals = signals;
        _scheduler = scheduler;
        _missions = missions;
        _curiosity = curiosity;
        _situation = situation;
        _resources = resources;
        _attentionPolicy = attentionPolicy;
        _clock = clock;
    }

    public async Task<ReviewOutcome> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var omitted = new List<string>();

        // --- Perception -------------------------------------------------------------------
        CognitiveCycle cycle = await _cycle.RunAsync(
            new CycleIngress($"review/{request.Principal.ClientId}", correlationId, null), ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Perception, [correlationId], [correlationId], null, ct)
            .ConfigureAwait(false);

        DomainEvent ingress = await _bus.PublishAsync(
            new OutboxWrite(
                "ReviewRequested", 1, "review", correlationId, Sensitivity.Private,
                AggregateRef: $"review/{request.Principal.ClientId}",
                PayloadJson: $$"""{"after":{{request.AfterAuditSequence}}}""",
                IdempotencyKey: correlationId),
            ct).ConfigureAwait(false);

        ReviewFindings findings = await GatherAsync(request, ct).ConfigureAwait(false);

        // --- Attention --------------------------------------------------------------------
        // What is waiting is what deserves attention, and it is ranked by the same system that
        // ranks anything else rather than by a list this application decided on.
        var candidates = findings.OpenNeeds
            .Select(id => new AttentionItem(
                id, AttentionKind.Goal, 0.7, 0.6, 0.5, 0.5, 0.8, 0.9, Sensitivity.Private, 40))
            .Concat(findings.PendingSignals.Select(id => new AttentionItem(
                id, AttentionKind.Alert, 0.8, 0.8, 0.6, 0.5, 0.8, 0.9, Sensitivity.Private, 40)))
            .ToList();

        var access = new MemoryAccessContext(
            request.Principal.ClientId, [MemoryAccessPolicy.Owner], Sensitivity.Private);

        AttentionSet attention = await _attention
            .RankAsync(cycle.Id, candidates, _attentionPolicy, access, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Attention, [ingress.EventId],
            attention.Items.Select(i => i.Ref).ToList(), null, ct).ConfigureAwait(false);

        // --- Working Memory ---------------------------------------------------------------
        WorkingMemoryFrame frame = await _working
            .OpenAsync(cycle.Id, correlationId, attention, _attentionPolicy, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.WorkingMemory, [attention.Id], [frame.Id], null, ct).ConfigureAwait(false);

        // --- Memory -----------------------------------------------------------------------
        // A review reads records, not recollections. Recalling memories here would mix what Aurora
        // believes into a report about what it did, which is exactly the confusion it exists to
        // prevent.
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Memory, "a review reads records, not recollections", ct)
            .ConfigureAwait(false);
        omitted.Add(CycleStage.Memory);

        // --- World Model ------------------------------------------------------------------
        WorldAnswer known = await _world
            .AskAsync($"review/{request.Principal.ClientId}", "concerns", _clock.UtcNow, ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.WorldModel, [frame.Id], [known.Knowledge.ToString()], null, ct)
            .ConfigureAwait(false);

        // --- Planner ----------------------------------------------------------------------
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Planner, "a review reports; it does not take on work", ct)
            .ConfigureAwait(false);
        omitted.Add(CycleStage.Planner);

        // --- Decision ---------------------------------------------------------------------
        var evidence = findings.OpenNeeds
            .Concat(findings.PendingSignals)
            .Append(ingress.EventId)
            .ToList();

        var report = new DecisionOption(
            DecisionMode.Respond,
            "Report what the records show.",
            ExpectedEffects: [],
            new OptionEvaluation(
                Relevance: 0.9, HasEvidence: true, RiskLevel: RiskLevel.Low.ToString(),
                CostEstimate: 1, Permitted: true, Reversible: false),
            Prerequisites: [], BlockingReasons: []);

        // There is nothing to ask about: the person asked what happened, and the answer is in the
        // records either way. Priced and blocked rather than left out, so the record shows it was
        // considered.
        var ask = new DecisionOption(
            DecisionMode.Ask, "Ask what the review should cover.", [],
            new OptionEvaluation(0.4, false, RiskLevel.Low.ToString(), 3, true, true),
            Prerequisites: [],
            BlockingReasons: ["the records answer this without asking anything of the person"]);

        Decision decision = await _decisions.EvaluateAsync(
            new DecisionThought(
                cycle.Id, null, [report, ask], evidence,
                Confidence: 0.7, RiskLevel: RiskLevel.Low.ToString()),
            new DecisionContext(MotorAvailable: true, AllowedSilenceReasons: []),
            ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Decision, evidence, [decision.Id], decision.Id, ct).ConfigureAwait(false);

        // --- Policy -----------------------------------------------------------------------
        Decision committed = await _decisions.CommitAsync(
            decision.Id, [new PolicyResult(ReviewPolicy, Allowed: true, ApprovalSatisfied: true)], ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Policy, [decision.Id], [ReviewPolicy], decision.Id, ct)
            .ConfigureAwait(false);

        // --- Capabilities -----------------------------------------------------------------
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Capabilities, "reading Aurora's own records needs no capability", ct)
            .ConfigureAwait(false);
        omitted.Add(CycleStage.Capabilities);

        // --- Executor ---------------------------------------------------------------------
        var summary = Compose(findings);

        AuroraAction action = await _observations.ProposeActionAsync(
            committed.Id, "review.brief", $"review/{request.Principal.ClientId}",
            Hashing.Sha256Hex(summary), reversible: true, ct).ConfigureAwait(false);

        await _observations.AuthorizeActionAsync(action.Id, ct).ConfigureAwait(false);
        await _observations.DispatchActionAsync(action.Id, toolCallId: null, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Executor, [committed.Id], [action.Id], committed.Id, ct)
            .ConfigureAwait(false);

        await _cycle.MarkExecutedAsync(cycle.Id, policyAllowed: true, approvalSatisfied: true, ct)
            .ConfigureAwait(false);

        // --- Observation ------------------------------------------------------------------
        Observation observation = await _observations.RecordAsync(
            action.Id, "review", "records", ObservationOutcome.Success, null, null, ct)
            .ConfigureAwait(false);

        await _observations.ValidateAsync(observation.Id, valid: true, null, ct).ConfigureAwait(false);
        await _observations.ObserveAsync(action.Id, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Observation, [action.Id], [observation.Id], null, ct)
            .ConfigureAwait(false);

        // --- Reflection -------------------------------------------------------------------
        IReadOnlyList<string> lessons = findings.GoalsPastReview.Count > 0
            ? [$"{findings.GoalsPastReview.Count} goal(s) are past the date somebody said they would be reviewed"]
            : [];

        Reflection reflection = await _observations.ReflectAsync(
            observation.Id, "reviewed", lessons, proposals: [], ct).ConfigureAwait(false);

        await _observations.DecideReflectionAsync(reflection.Id, accept: true, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Reflection, [observation.Id], [reflection.Id], null, ct)
            .ConfigureAwait(false);

        // --- Learning ---------------------------------------------------------------------
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Learning, "a review reports what is; it does not change anything", ct)
            .ConfigureAwait(false);
        omitted.Add(CycleStage.Learning);

        // --- Audit and close ---------------------------------------------------------------
        var auditRef = await _audit.AppendAsync(
            new AuditEntry(
                request.Principal.ClientId, request.Principal.OsUser, "review.brief",
                Hashing.Sha256Hex(summary), "completed",
                Risk: "Low", Via: ResolutionVia.Explicit, Decision: committed.Mode,
                PolicyIds: ReviewPolicy),
            ct).ConfigureAwait(false);

        await _working.SealAsync(frame.Id, ct).ConfigureAwait(false);
        await _working.DisposeFrameAsync(frame.Id, [], ct).ConfigureAwait(false);
        await _attention.ReleaseAsync(cycle.Id, ct).ConfigureAwait(false);

        await _cycle.CompleteAsync(
            cycle.Id, carriesPersistentStateOrExecution: true, summary, ct).ConfigureAwait(false);

        IReadOnlyList<CycleStageRecord> stages = await _cycle.StagesAsync(cycle.Id, ct).ConfigureAwait(false);

        return new ReviewOutcome(
            cycle.Id, committed.Id, action.Id, observation.Id, reflection.Id, summary, [auditRef],
            stages.Where(s => s.Status == StageStatus.Done).Select(s => s.Stage).ToList(),
            omitted, findings);
    }

    /// <summary>
    /// Reads everything the review reports on, from Aurora's own records.
    /// </summary>
    /// <remarks>
    /// Nothing here interprets. The counts and identifiers are what the stores say they are, so a
    /// person can check any line of the briefing against the thing it came from.
    /// </remarks>
    private async Task<ReviewFindings> GatherAsync(ReviewRequest request, CancellationToken ct)
    {
        IReadOnlyList<AuditRecordView> entries = await _audit
            .QueryAsync(request.AfterAuditSequence, request.Limit, ct).ConfigureAwait(false);

        IReadOnlyList<Need> needs = await _needs.RankAsync(ct).ConfigureAwait(false);
        IReadOnlyList<Signal> signals = await _signals.PendingAsync(ct).ConfigureAwait(false);
        IReadOnlyList<Schedule> schedules = await _scheduler.ListAsync(null, ct).ConfigureAwait(false);

        IReadOnlyList<Mission> missions = await _missions.ListAsync(null, ct).ConfigureAwait(false);
        var drifting = new List<string>();
        foreach (Mission mission in missions)
        {
            MissionReview review = await _missions.ReviewAsync(mission.Id, ct).ConfigureAwait(false);
            drifting.AddRange(review.AdHocGoalsPastReview);
        }

        IReadOnlyList<CuriosityProposal> questions = await _curiosity
            .ListAsync(CuriosityStatus.Candidate, ct).ConfigureAwait(false);

        SituationAssessment situation = await _situation
            .AssessAsync(new SituationContext(request.Timezone), ct).ConfigureAwait(false);

        ResourceState resources = await _resources.ObserveAsync(ct).ConfigureAwait(false);

        return new ReviewFindings(
            HighestAuditSequence: entries.Count == 0 ? request.AfterAuditSequence : entries[^1].Sequence,
            AuditEntries: entries.Count,
            OpenNeeds: needs.Select(n => n.Id).ToList(),
            PendingSignals: signals.Select(s => s.Id).ToList(),
            GoalsPastReview: drifting.Distinct(StringComparer.Ordinal).ToList(),
            OpenQuestions: questions.Select(q => q.Id).ToList(),
            FailedSchedules: schedules.Where(s => s.Status == ScheduleStatus.Failed).Select(s => s.Id).ToList(),
            situation.RiskPosture, resources.Status);
    }

    /// <summary>
    /// Builds the operational summary the client turns into words.
    /// </summary>
    /// <remarks>
    /// Counts and states, not prose. RFC 021 leaves the wording to the LLM client, and a summary
    /// that wrote itself into sentences here would be Aurora deciding how its own record sounds.
    /// </remarks>
    private static string Compose(ReviewFindings f) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{f.AuditEntries} audited action(s); {f.OpenNeeds.Count} open need(s); "
            + $"{f.PendingSignals.Count} pending signal(s); {f.GoalsPastReview.Count} goal(s) past review; "
            + $"{f.OpenQuestions.Count} open question(s); {f.FailedSchedules.Count} failed schedule(s); "
            + $"posture {f.RiskPosture}; resources {f.ResourceStatus}.");
}
