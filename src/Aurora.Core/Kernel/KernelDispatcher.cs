using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Core.Kernel;

/// <summary>
/// Dispatches an MCP call through the cognitive cycle (RFC 045 rule 3).
/// </summary>
/// <remarks>
/// RFC 045 requires the Kernel to apply <i>Mind semantics</i> to MCP ingress, not only policy and
/// audit. Executing a capability straight from a tool call satisfies the governance rules and skips
/// the cognition: nothing is attended to, nothing is decided, nothing is observed afterwards. This
/// runs the same call as a cycle, so what happened has a record that can be read back rather than
/// inferred from an audit line.
/// <para>
/// The Kernel remains the sole authority that commits an effect. The cycle decides what Aurora will
/// do; the Kernel decides what it may do, and the Kernel's answer is final in both directions — it
/// can refuse an action the cycle chose, and it never runs one the cycle did not.
/// </para>
/// </remarks>
public sealed class KernelDispatcher
{
    /// <summary>The policy under which the cycle records a permitted capability call.</summary>
    private const string CapabilityPolicy = "policy.capability_authorized_by_kernel";

    private readonly AuroraKernel _kernel;
    private readonly ICognitiveCycle _cycle;
    private readonly IEventBus _bus;
    private readonly IAttentionSystem _attention;
    private readonly IWorkingMemory _working;
    private readonly IMemoryService _memories;
    private readonly IWorldModel _world;
    private readonly IDecisionEngine _decisions;
    private readonly IObservationService _observations;
    private readonly AttentionPolicy _attentionPolicy;
    private readonly IClock _clock;

    public KernelDispatcher(
        AuroraKernel kernel,
        ICognitiveCycle cycle,
        IEventBus bus,
        IAttentionSystem attention,
        IWorkingMemory working,
        IMemoryService memories,
        IWorldModel world,
        IDecisionEngine decisions,
        IObservationService observations,
        AttentionPolicy attentionPolicy,
        IClock clock)
    {
        _kernel = kernel;
        _cycle = cycle;
        _bus = bus;
        _attention = attention;
        _working = working;
        _memories = memories;
        _world = world;
        _decisions = decisions;
        _observations = observations;
        _attentionPolicy = attentionPolicy;
        _clock = clock;
    }

    public async Task<ExecuteResponse> DispatchAsync(
        ExecuteRequest request, Principal principal, string? mcpSessionRef, CancellationToken ct)
    {
        // RFC 021: an invalid ingress is refused before any persistent cognitive mutation. A
        // malformed tool call does not get a cycle, an attention set, or a place in the record.
        if (AuroraKernel.ValidateIngress(request) is { } malformed)
        {
            return malformed;
        }

        // Resolution has to precede the decision, because a decision needs to know what it is
        // about: the option is priced by the capability's declared risk and effects.
        ResolutionOutcome resolution = await _kernel.ResolveAsync(request, principal, ct).ConfigureAwait(false);
        if (resolution.Refusal is { } unresolved)
        {
            return unresolved;
        }

        ActionResolution resolved = resolution.Resolution!;
        var correlationId = Guid.NewGuid().ToString("N");

        // --- Perception -------------------------------------------------------------------
        CognitiveCycle cycle = await _cycle.RunAsync(
            new CycleIngress($"mcp/{resolved.Resolved.ActionId}", correlationId, mcpSessionRef), ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Perception, [resolved.InputHash], [correlationId], null, ct)
            .ConfigureAwait(false);

        // A tool call is a domain fact, so it is published rather than merely handled (LAW-007).
        DomainEvent ingress = await _bus.PublishAsync(
            new OutboxWrite(
                EventCatalogue.KernelCommandAccepted, 1, EventCatalogue.Producers.Kernel, correlationId, Sensitivity.Private,
                AggregateRef: $"capability/{resolved.Resolved.ActionId}",
                PayloadJson: $$"""{"action_id":"{{resolved.Resolved.ActionId}}","via":"{{resolved.Resolved.Via}}"}""",
                IdempotencyKey: correlationId),
            ct).ConfigureAwait(false);

        try
        {
            return await ReasonAsync(request, principal, resolved, cycle, ingress, correlationId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The cycle ends recording why, rather than being left RUNNING forever. Bookkeeping is
            // non-cancellable: a cycle abandoned mid-flight is exactly the one worth reading later.
            await _cycle.FailAsync(cycle.Id, failure.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ExecuteResponse> ReasonAsync(
        ExecuteRequest request, Principal principal, ActionResolution resolved,
        CognitiveCycle cycle, DomainEvent ingress, string correlationId, CancellationToken ct)
    {
        // --- Attention --------------------------------------------------------------------
        var access = new MemoryAccessContext(
            principal.ClientId, [MemoryAccessPolicy.Owner], Sensitivity.Private);

        var query = request.Objective ?? resolved.Resolved.ActionId;
        MemorySearchResult recalled = await _memories
            .SearchAsync(query, access, new MemoryFilters(), ct).ConfigureAwait(false);

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
            .AskAsync($"capability/{resolved.Resolved.ActionId}", "affects", _clock.UtcNow, ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.WorldModel, [frame.Id], [known.Knowledge.ToString()], null, ct)
            .ConfigureAwait(false);

        // --- Planner ----------------------------------------------------------------------
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Planner, "a single capability call carries no explicit goal", ct)
            .ConfigureAwait(false);

        // --- Decision ---------------------------------------------------------------------
        Decision decision = await DecideAsync(resolved, cycle, ingress, recalled, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Decision,
            recalled.Matches.Select(m => m.Memory.Id).Append(ingress.EventId).ToList(),
            [decision.Id], decision.Id, ct).ConfigureAwait(false);

        if (!DecisionMode.HasExternalEffect(decision.Mode))
        {
            return await AskAsync(resolved, cycle, frame, decision, ct).ConfigureAwait(false);
        }

        // --- Policy -----------------------------------------------------------------------
        // The Kernel's own policy and consent evaluation *is* this stage. Running it here rather
        // than inside the executor is what puts permission before the effect instead of around it.
        AuthorizationOutcome authorization = await _kernel
            .AuthorizeAsync(resolved, ct).ConfigureAwait(false);

        if (authorization.Refusal is { } refused)
        {
            return await RefuseAsync(cycle, frame, decision, refused, ct).ConfigureAwait(false);
        }

        ActionAuthorization authorized = authorization.Authorization!;

        // Committing a TOOL_CALL is refused without an allowing policy result and, where the
        // decision says approval is required, a satisfied one. So the optimistic evaluation the
        // option carried cannot become a commitment on its own.
        Decision committed = await _decisions.CommitAsync(
            decision.Id,
            [new PolicyResult(CapabilityPolicy, Allowed: true, ApprovalSatisfied: true)], ct)
            .ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Policy, [decision.Id],
            authorized.PolicyIds.Append(authorized.Consent.Decision).ToList(), decision.Id, ct)
            .ConfigureAwait(false);

        // --- Capabilities -----------------------------------------------------------------
        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Capabilities, [decision.Id],
            [resolved.Resolved.ActionId], decision.Id, ct).ConfigureAwait(false);

        // --- Executor ---------------------------------------------------------------------
        AuroraAction action = await _observations.ProposeActionAsync(
            committed.Id, resolved.Resolved.ActionId, $"capability/{resolved.Resolved.ActionId}",
            resolved.InputHash, reversible: !resolved.HasExternalEffect, ct).ConfigureAwait(false);

        await _observations.AuthorizeActionAsync(action.Id, ct).ConfigureAwait(false);
        await _observations.DispatchActionAsync(action.Id, toolCallId: null, ct).ConfigureAwait(false);

        ExecuteResponse response;
        try
        {
            response = await _kernel.CommitAsync(authorized, ct).ConfigureAwait(false);
        }
        catch
        {
            // The reservation is ours and the effect did not settle. Release it rather than leave
            // the key wedged until reconciliation.
            await _kernel.ReleaseAsync(
                authorized, "the cycle failed after authorization", CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Executor, [committed.Id], [action.Id], committed.Id, ct)
            .ConfigureAwait(false);

        // The reservation was never claimed, so nothing reached the capability. Recording this as
        // an execution would be claiming an effect that did not happen.
        if (response.Status == ExecuteStatus.InProgress)
        {
            return await CloseWithoutEffectAsync(
                cycle, frame, response, "the idempotency reservation was lost before the effect", ct)
                .ConfigureAwait(false);
        }

        await _cycle.MarkExecutedAsync(cycle.Id, policyAllowed: true, approvalSatisfied: true, ct)
            .ConfigureAwait(false);

        // --- Observation ------------------------------------------------------------------
        var outcome = response.Status switch
        {
            ExecuteStatus.Completed => ObservationOutcome.Success,
            ExecuteStatus.Failed => ObservationOutcome.Failure,

            // LAW-003: we called out and did not learn what happened. That is not a success.
            _ => ObservationOutcome.Unknown,
        };

        Observation observation = await _observations.RecordAsync(
            action.Id, "kernel", "capability", outcome, payloadRef: null, externalRef: null, ct)
            .ConfigureAwait(false);

        // Validity is about whether the observation itself is trustworthy, not whether the news
        // was good: a cleanly reported failure is a valid observation.
        await _observations.ValidateAsync(observation.Id, valid: true, null, ct).ConfigureAwait(false);
        await _observations.ObserveAsync(action.Id, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Observation, [action.Id], [observation.Id], null, ct)
            .ConfigureAwait(false);

        // --- Reflection -------------------------------------------------------------------
        IReadOnlyList<string> lessons = outcome == ObservationOutcome.Success
            ? []
            : [$"{resolved.Resolved.ActionId} ended as {outcome} on input {resolved.InputHash[..8]}"];

        Reflection reflection = await _observations.ReflectAsync(
            observation.Id, outcome, lessons, proposals: [], ct).ConfigureAwait(false);

        await _observations.DecideReflectionAsync(reflection.Id, accept: true, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Reflection, [observation.Id], [reflection.Id], null, ct)
            .ConfigureAwait(false);

        // --- Learning ---------------------------------------------------------------------
        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Learning, "the reflection proposed no change", ct).ConfigureAwait(false);

        await ReleaseContextAsync(cycle, frame, ct).ConfigureAwait(false);

        await _cycle.CompleteAsync(
            cycle.Id, carriesPersistentStateOrExecution: true,
            $"{resolved.Resolved.ActionId} → {response.Status}", ct).ConfigureAwait(false);

        return response with { CycleRef = cycle.Id };
    }

    /// <summary>
    /// Chooses between running the capability and asking first.
    /// </summary>
    /// <remarks>
    /// The engine prefers whichever option reaches nothing outside Aurora, so asking wins unless it
    /// is blocked. It is blocked in two cases, and only those: the caller named the action, so the
    /// question has no answer left to give; or the action changes nothing outside Aurora, so the
    /// answer could not prevent anything and would only cost a round trip.
    /// <para>
    /// What that leaves open is real: an action <i>inferred</i> from an objective that reaches
    /// outside Aurora. Aurora asks rather than acting on its own reading of what was wanted.
    /// </para>
    /// </remarks>
    private async Task<Decision> DecideAsync(
        ActionResolution resolved, CognitiveCycle cycle, DomainEvent ingress,
        MemorySearchResult recalled, CancellationToken ct)
    {
        var instructed = resolved.Resolved.Via == ResolutionVia.Explicit;

        var blocking = instructed
            ? "the caller named the action and its input; asking could add nothing"
            : !resolved.HasExternalEffect
                ? "this reaches nothing outside Aurora, so asking could not prevent anything"
                : null;

        var act = new DecisionOption(
            DecisionMode.ToolCall,
            $"Run {resolved.Resolved.ActionId}, resolved {resolved.Resolved.Via}.",
            ExpectedEffects: resolved.HasExternalEffect ? [resolved.Resolved.ActionId] : [],
            new OptionEvaluation(
                Relevance: resolved.Resolved.Confidence,

                // An explicitly named action is its own evidence: the caller stated it.
                HasEvidence: instructed || recalled.Matches.Count > 0,
                RiskLevel: resolved.Risk,
                CostEstimate: resolved.HasExternalEffect ? 5 : 1,

                // Optimistic, and it cannot become a commitment on its own: the engine refuses to
                // commit a TOOL_CALL without a real allowing policy result, which only the Kernel
                // produces, at the Policy stage below.
                Permitted: true,
                Reversible: !resolved.HasExternalEffect),
            Prerequisites: [], BlockingReasons: []);

        var ask = new DecisionOption(
            DecisionMode.Ask,
            "Ask which action was meant before acting on an inference.",
            ExpectedEffects: [],
            new OptionEvaluation(0.5, false, RiskLevel.Low.ToString(), 3, true, true),
            Prerequisites: [],
            BlockingReasons: blocking is null ? [] : [blocking]);

        return await _decisions.EvaluateAsync(
            new DecisionThought(
                cycle.Id, null, [act, ask],
                recalled.Matches.Select(m => m.Memory.Id).Append(ingress.EventId).ToList(),
                Confidence: resolved.Resolved.Confidence, RiskLevel: resolved.Risk),
            new DecisionContext(MotorAvailable: true, AllowedSilenceReasons: []),
            ct).ConfigureAwait(false);
    }

    /// <summary>Closes a cycle that decided to ask. Nothing was reserved and nothing ran.</summary>
    private async Task<ExecuteResponse> AskAsync(
        ActionResolution resolved, CognitiveCycle cycle, WorkingMemoryFrame frame,
        Decision decision, CancellationToken ct)
    {
        await _decisions.CommitAsync(decision.Id, [], ct).ConfigureAwait(false);

        await _cycle.OmitAsync(
            cycle.Id, CycleStage.Policy, "asking reaches nothing, so there is nothing to permit", ct)
            .ConfigureAwait(false);

        await OmitTailAsync(cycle, "the decision was to ask rather than act", ct).ConfigureAwait(false);
        await ReleaseContextAsync(cycle, frame, ct).ConfigureAwait(false);

        await _cycle.CompleteAsync(
            cycle.Id, carriesPersistentStateOrExecution: false,
            $"{decision.Mode} for {resolved.Resolved.ActionId}", ct).ConfigureAwait(false);

        return new ExecuteResponse(
            ExecuteStatus.Asked, resolved.Resolved,
            Error: new ExecuteError(
                ErrorCodes.ClarificationRequired, decision.SelectedOption.RationaleSummary),
            CycleRef: cycle.Id);
    }

    /// <summary>Closes a cycle whose action the Kernel refused. The decision is not committed.</summary>
    private async Task<ExecuteResponse> RefuseAsync(
        CognitiveCycle cycle, WorkingMemoryFrame frame, Decision decision,
        ExecuteResponse refusal, CancellationToken ct)
    {
        var reason = refusal.Error?.Code ?? refusal.Status;

        // Superseded rather than committed: a decision the Kernel would not permit was never a
        // decision Aurora got to make, and recording it as committed would say otherwise.
        await _decisions.InvalidateAsync(decision.Id, reason, ct).ConfigureAwait(false);

        await _cycle.AdvanceAsync(
            cycle.Id, CycleStage.Policy, [decision.Id], [reason], decision.Id, ct).ConfigureAwait(false);

        return await CloseWithoutEffectAsync(cycle, frame, refusal, reason, ct).ConfigureAwait(false);
    }

    private async Task<ExecuteResponse> CloseWithoutEffectAsync(
        CognitiveCycle cycle, WorkingMemoryFrame frame, ExecuteResponse response,
        string reason, CancellationToken ct)
    {
        await OmitTailAsync(cycle, reason, ct).ConfigureAwait(false);
        await ReleaseContextAsync(cycle, frame, ct).ConfigureAwait(false);

        await _cycle.CompleteAsync(
            cycle.Id, carriesPersistentStateOrExecution: false, $"{response.Status}: {reason}", ct)
            .ConfigureAwait(false);

        return response with { CycleRef = cycle.Id };
    }

    /// <summary>
    /// Records the stages after the point of no effect as deliberately not run.
    /// </summary>
    /// <remarks>
    /// Omitting with a reason rather than leaving them blank is what makes the difference between
    /// "Aurora did not act" and "the record stops here" legible afterwards (RFC 021 rule 1).
    /// </remarks>
    private async Task OmitTailAsync(CognitiveCycle cycle, string reason, CancellationToken ct)
    {
        IReadOnlyList<CycleStageRecord> recorded = await _cycle.StagesAsync(cycle.Id, ct).ConfigureAwait(false);
        var already = recorded.Select(s => s.Stage).ToHashSet(StringComparer.Ordinal);

        foreach (var stage in new[]
                 {
                     CycleStage.Capabilities, CycleStage.Executor,
                     CycleStage.Observation, CycleStage.Reflection, CycleStage.Learning,
                 })
        {
            if (!already.Contains(stage))
            {
                await _cycle.OmitAsync(cycle.Id, stage, reason, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Disposing the frame is what makes the context temporary rather than a transcript.</summary>
    private async Task ReleaseContextAsync(
        CognitiveCycle cycle, WorkingMemoryFrame frame, CancellationToken ct)
    {
        await _working.SealAsync(frame.Id, ct).ConfigureAwait(false);
        await _working.DisposeFrameAsync(frame.Id, [], ct).ConfigureAwait(false);
        await _attention.ReleaseAsync(cycle.Id, ct).ConfigureAwait(false);
    }
}
