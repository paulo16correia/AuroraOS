using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;

namespace Aurora.Adapters.Pilot;

/// <summary>
/// The first vertical slice: answer a local conversation through the full governed cycle,
/// using no external tool.
/// </summary>
public sealed class LocalConversationPilot : IPilotApplication
{
    /// <summary>Policy id for a response, which reaches nothing outside Aurora.</summary>
    private const string RespondPolicy = "policy.respond_no_external_effect";

    private readonly ICognitiveCycle _cycle;
    private readonly IEventBus _bus;
    private readonly IAttentionSystem _attention;
    private readonly IWorkingMemory _working;
    private readonly IMemoryService _memories;
    private readonly IWorldModel _world;
    private readonly IDecisionEngine _decisions;
    private readonly IObservationService _observations;
    private readonly IAuditStore _audit;
    private readonly AttentionPolicy _attentionPolicy;
    private readonly IClock _clock;

    public LocalConversationPilot(
        ICognitiveCycle cycle,
        IEventBus bus,
        IAttentionSystem attention,
        IWorkingMemory working,
        IMemoryService memories,
        IWorldModel world,
        IDecisionEngine decisions,
        IObservationService observations,
        IAuditStore audit,
        AttentionPolicy attentionPolicy,
        IClock clock)
    {
        _cycle = cycle;
        _bus = bus;
        _attention = attention;
        _working = working;
        _memories = memories;
        _world = world;
        _decisions = decisions;
        _observations = observations;
        _audit = audit;
        _attentionPolicy = attentionPolicy;
        _clock = clock;
    }

    public async Task<PilotOutcome> RespondAsync(PilotRequest request, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var omitted = new List<string>();

        // --- Perception -------------------------------------------------------------------
        // An invalid ingress is rejected before any persistent cognitive mutation (RFC 021).
        if (string.IsNullOrWhiteSpace(request.Utterance))
        {
            throw new CognitiveCycleException("An empty utterance is not a request.");
        }

        CognitiveCycle cycle = await _cycle.RunAsync(
            new CycleIngress($"conversation/{request.ConversationRef}", correlationId, correlationId), ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Perception, [request.ConversationRef], [correlationId], null, ct)
            .ConfigureAwait(false);

        // The turn is a domain fact, so it is published rather than merely handled (LAW-007).
        DomainEvent ingress = await _bus.PublishAsync(
            new OutboxWrite(
                EventCatalogue.ConversationTurnReceived, 1, EventCatalogue.Producers.Pilot, correlationId, Sensitivity.Private,
                AggregateRef: $"conversation/{request.ConversationRef}",
                PayloadJson: $$"""{"length":{{request.Utterance.Length}}}""",
                IdempotencyKey: correlationId),
            ct).ConfigureAwait(false);

        // --- Attention --------------------------------------------------------------------
        var access = new MemoryAccessContext(
            request.Principal.ClientId, [MemoryAccessPolicy.Owner], Sensitivity.Private);

        MemorySearchResult recalled = await _memories
            .SearchAsync(request.Utterance, access, new MemoryFilters(), ct).ConfigureAwait(false);

        var candidates = recalled.Matches
            .Select(m => new AttentionItem(
                m.Memory.Id, AttentionKind.Memory, m.Score, 0.3, 0.3, 0.3,
                m.Memory.Confidence, 0.9, m.Memory.SensitivityClass, TokenCost: 50))
            .ToList();

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
        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Memory, [frame.Id],
            recalled.Matches.Select(m => m.Memory.Id).ToList(), null, ct).ConfigureAwait(false);

        // --- World Model ------------------------------------------------------------------
        WorldAnswer known = await _world
            .AskAsync($"conversation/{request.ConversationRef}", "concerns", _clock.UtcNow, ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.WorldModel, [frame.Id], [known.Knowledge.ToString()], null, ct)
            .ConfigureAwait(false);

        // --- Planner ----------------------------------------------------------------------
        // No explicit goal in a single conversational turn, so the stage is omitted with its
        // reason rather than silently skipped.
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Planner, "a single conversational turn carries no explicit goal", ct)
            .ConfigureAwait(false);
        omitted.Add(CycleStage.Planner);

        // --- Decision ---------------------------------------------------------------------
        var evidence = recalled.Matches.Select(m => m.Memory.Id).Append(ingress.EventId).ToList();

        var respond = new DecisionOption(
            DecisionMode.Respond,
            "Answer from the framed context; nothing outside Aurora is touched.",
            ExpectedEffects: [],
            new OptionEvaluation(
                Relevance: 0.9, HasEvidence: recalled.Matches.Count > 0, RiskLevel: "LOW",
                CostEstimate: 1, Permitted: true, Reversible: false),
            Prerequisites: [], BlockingReasons: []);

        // Asking is not free: a needless clarification costs the person a round trip. Priced
        // above answering so that reversibility only decides between genuinely equal options —
        // otherwise the engine would always prefer asking, since answering cannot be unsaid.
        var ask = new DecisionOption(
            DecisionMode.Ask, "Ask for what is missing.", [],
            new OptionEvaluation(
                Relevance: 0.5, HasEvidence: false, RiskLevel: "LOW", CostEstimate: 3,
                Permitted: true, Reversible: true),
            Prerequisites: [], BlockingReasons: []);

        Decision decision = await _decisions.EvaluateAsync(
            new DecisionThought(
                cycle.Id, null, [respond, ask], evidence,
                Confidence: recalled.Confident ? 0.6 : 0.4, RiskLevel: "LOW"),
            new DecisionContext(MotorAvailable: true, AllowedSilenceReasons: []),
            ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Decision, evidence, [decision.Id], decision.Id, ct).ConfigureAwait(false);

        // --- Policy -----------------------------------------------------------------------
        // A response reaches nothing outside Aurora, so it is allowed without an approval — and
        // the decision is still committed against a named policy rather than waved through.
        Decision committed = await _decisions.CommitAsync(
            decision.Id, [new PolicyResult(RespondPolicy, Allowed: true, ApprovalSatisfied: true)], ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Policy, [decision.Id], [RespondPolicy], decision.Id, ct)
            .ConfigureAwait(false);

        // --- Capabilities -----------------------------------------------------------------
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Capabilities, "responding needs no external capability", ct)
            .ConfigureAwait(false);
        omitted.Add(CycleStage.Capabilities);

        // --- Executor ---------------------------------------------------------------------
        var response = Compose(request, recalled, committed);

        AuroraAction action = await _observations.ProposeActionAsync(
            committed.Id, "conversation.respond", $"conversation/{request.ConversationRef}",
            Hashing.Sha256Hex(response), reversible: false, ct).ConfigureAwait(false);

        await _observations.AuthorizeActionAsync(action.Id, ct).ConfigureAwait(false);
        await _observations.DispatchActionAsync(action.Id, toolCallId: null, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Executor, [committed.Id], [action.Id], committed.Id, ct)
            .ConfigureAwait(false);

        await _cycle.MarkExecutedAsync(cycle.Id, policyAllowed: true, approvalSatisfied: true, ct)
            .ConfigureAwait(false);

        // --- Observation ------------------------------------------------------------------
        Observation observation = await _observations.RecordAsync(
            action.Id, "pilot", "conversation", ObservationOutcome.Success,
            payloadRef: null, externalRef: null, ct).ConfigureAwait(false);

        await _observations.ValidateAsync(observation.Id, valid: true, null, ct).ConfigureAwait(false);
        await _observations.ObserveAsync(action.Id, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Observation, [action.Id], [observation.Id], null, ct)
            .ConfigureAwait(false);

        // --- Reflection -------------------------------------------------------------------
        // A plain answer usually teaches nothing, and recording that is the point.
        Reflection reflection = await _observations.ReflectAsync(
            observation.Id, "answered from context", lessons: [], proposals: [], ct).ConfigureAwait(false);

        await _observations.DecideReflectionAsync(reflection.Id, accept: true, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Reflection, [observation.Id], [reflection.Id], null, ct)
            .ConfigureAwait(false);

        // --- Learning ---------------------------------------------------------------------
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Learning, "the reflection proposed no change", ct).ConfigureAwait(false);
        omitted.Add(CycleStage.Learning);

        // --- Audit and close ---------------------------------------------------------------
        var auditRef = await _audit.AppendAsync(
            new AuditEntry(
                request.Principal.ClientId, request.Principal.OsUser, "conversation.respond",
                Hashing.Sha256Hex(request.Utterance), "completed",
                Risk: "Low", Via: ResolutionVia.Explicit, Decision: committed.Mode,
                PolicyIds: RespondPolicy),
            ct).ConfigureAwait(false);

        // Disposing the frame is what makes the context temporary rather than a growing transcript.
        await _working.SealAsync(frame.Id, ct).ConfigureAwait(false);
        await _working.DisposeFrameAsync(frame.Id, [], ct).ConfigureAwait(false);
        await _attention.ReleaseAsync(cycle.Id, ct).ConfigureAwait(false);

        await _cycle.CompleteAsync(
            cycle.Id, carriesPersistentStateOrExecution: true, response, ct).ConfigureAwait(false);

        IReadOnlyList<CycleStageRecord> stages = await _cycle.StagesAsync(cycle.Id, ct).ConfigureAwait(false);

        return new PilotOutcome(
            cycle.Id, committed.Id, action.Id, observation.Id, reflection.Id, response, [auditRef],
            stages.Where(s => s.Status == StageStatus.Done).Select(s => s.Stage).ToList(),
            omitted);
    }

    public async Task<PilotOutcome?> RecallAsync(string cycleId, CancellationToken ct)
    {
        CognitiveCycle? cycle = await _cycle.GetAsync(cycleId, ct).ConfigureAwait(false);
        if (cycle is null)
        {
            return null;
        }

        IReadOnlyList<CycleStageRecord> stages = await _cycle.StagesAsync(cycleId, ct).ConfigureAwait(false);

        var decisionRef = stages.FirstOrDefault(s => s.Stage == CycleStage.Decision)?.DecisionRef;
        var actionRef = stages.FirstOrDefault(s => s.Stage == CycleStage.Executor)?.OutputRefs.FirstOrDefault();
        var observationRef = stages
            .FirstOrDefault(s => s.Stage == CycleStage.Observation)?.OutputRefs.FirstOrDefault();
        var reflectionRef = stages
            .FirstOrDefault(s => s.Stage == CycleStage.Reflection)?.OutputRefs.FirstOrDefault();

        return new PilotOutcome(
            cycleId, decisionRef ?? string.Empty, actionRef ?? string.Empty,
            observationRef ?? string.Empty, reflectionRef ?? string.Empty,
            ResponseSummary: cycle.Status,
            AuditRefs: [],
            stages.Where(s => s.Status == StageStatus.Done).Select(s => s.Stage).ToList(),
            stages.Where(s => s.Status == StageStatus.Omitted).Select(s => s.Stage).ToList());
    }

    /// <summary>
    /// Builds the operational summary the client will turn into words.
    /// </summary>
    /// <remarks>
    /// Aurora does not write the reply. RFC 021 puts natural language with the LLM client, and this
    /// is deliberately a summary of what was found and decided rather than a sentence pretending to
    /// be one.
    /// </remarks>
    private static string Compose(PilotRequest request, MemorySearchResult recalled, Decision decision)
    {
        var evidence = recalled.Matches.Count == 0
            ? recalled.Confident
                ? "nothing recorded on this"
                : "search was degraded, so absence is not established"
            : $"{recalled.Matches.Count} recalled memory(ies)";

        return $"{decision.Mode} for conversation/{request.ConversationRef}: {evidence}.";
    }
}
