using System.Text;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using Aurora.Server.Security;

namespace Aurora.Server.Api;

/// <summary>Request bodies for the RFC 10 write commands.</summary>
/// <remarks>
/// There is no <c>Type</c>. The ingress endpoint publishes exactly one declared event —
/// <see cref="EventCatalogue.ExternalObservationReported"/> — because a surface outside Aurora
/// choosing its own event type is a surface that can assert anything about anything (LAW-007).
/// What it saw goes in the payload, where it reads as a report rather than as a fact.
/// </remarks>
public sealed record PublishEventBody(
    string Observation, string? SubjectRef = null, string? PayloadRef = null);

public sealed record CreateGoalBody(
    string Title,
    string Outcome,
    IReadOnlyList<string>? SuccessCriteria = null,
    IReadOnlyList<string>? Assumptions = null,
    int Priority = 3,
    string? DeadlineAtUtc = null,
    string? ApprovalPolicyId = null);

public sealed record DecideApprovalBody(string Decision, string? Passphrase = null);

public sealed record CorrectMemoryBody(string Reason);

public sealed record CommunicationPreferenceBody(
    string Channel, string Language, double Verbosity, bool ConsentForProactivity,
    string? QuietHours = null);

public sealed record ActivateProfileBody(string ApprovalRef, string Reason);

/// <summary>
/// The operator and UI surface (RFC 10).
/// </summary>
/// <remarks>
/// Deliberately separate from the MCP surface. MCP is the door the model knocks on; this is the
/// door a person uses. Keeping them apart is what lets the person decide, revoke and inspect what
/// the model may do — collapsing them into one endpoint set would hand the agent the controls
/// meant to govern it.
/// </remarks>
public static class ApiEndpoints
{
    /// <summary>Default page size for the paginated reads; the stores clamp the ceiling.</summary>
    private const int DefaultPageSize = 50;

    public static WebApplication MapAuroraApi(this WebApplication app)
    {
        app.MapPost("/v1/events", PublishEventAsync);
        app.MapPost("/v1/goals", CreateGoalAsync);
        app.MapGet("/v1/goals/{id}", ReadGoalAsync);
        app.MapPost("/v1/approvals/{id}/decide", DecideApprovalAsync);
        app.MapGet("/v1/memories", SearchMemoriesAsync);
        app.MapPatch("/v1/memories/{id}", CorrectMemoryAsync);
        app.MapDelete("/v1/memories/{id}", ForgetMemoryAsync);
        app.MapGet("/v1/audit", ReadAuditAsync);
        app.MapGet("/v1/stream", StreamAsync);
        app.MapGet("/v1/status", ReadStatusAsync);
        app.MapGet("/v1/catalog", ReadCatalog);
        app.MapGet("/v1/personality", ReadPersonalityAsync);
        app.MapPut("/v1/personality/preference", SetCommunicationPreferenceAsync);
        app.MapPost("/v1/personality/{id}/activate", ActivatePersonalityAsync);
        app.MapGet("/v1/cycles/{id}/why", ExplainAsync);
        app.MapPost("/v1/maintenance", RunMaintenanceAsync);
        return app;
    }

    // ---- events: normalized ingress ----

    private static Task<IResult> PublishEventAsync(
        PublishEventBody body, HttpRequest request,
        IEventBus bus, IIdempotencyStore idempotency, IPrincipalAccessor principals, CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);
        var key = KeyOf(request);

        if (string.IsNullOrWhiteSpace(body.Observation))
        {
            return Task.FromResult(BadRequest(
                correlationId, "An observation needs to say what was observed.", nameof(body.Observation)));
        }

        EventContract contract = EventCatalogue
            .For(EventCatalogue.Producers.Api)
            .Single(c => c.Type == EventCatalogue.ExternalObservationReported);

        return ApiIdempotency.RunAsync(
            idempotency, principals.Current, key, body, correlationId,
            token => bus.PublishAsync(
                new OutboxWrite(
                    contract.Type, contract.SchemaVersion, contract.Producer, correlationId,
                    contract.SensitivityClass,
                    AggregateRef: body.SubjectRef,

                    // Reported, and marked as reported. Nothing downstream treats this as
                    // established: it is an observation from outside, which is what the ingress
                    // endpoint can honestly produce.
                    PayloadJson: body.PayloadRef is null
                        ? AuroraJson.Serialize(
                            new { observed = body.Observation, reported_by = principals.Current.ClientId })
                        : null,
                    PayloadRef: body.PayloadRef,
                    IdempotencyKey: key),
                token),
            ct);
    }

    // ---- goals ----

    private static Task<IResult> CreateGoalAsync(
        CreateGoalBody body, HttpRequest request,
        IPlanner planner, IIdempotencyStore idempotency, IPrincipalAccessor principals, CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);
        Principal principal = principals.Current;

        var goal = new GoalRequest(
            body.Title, body.Outcome, OwnerId: principal.ClientId,
            body.SuccessCriteria ?? [], body.Assumptions ?? [],
            body.Priority, DeadlineAtUtc: body.DeadlineAtUtc, ApprovalPolicyId: body.ApprovalPolicyId);

        // DRAFT, and no tasks from outside. A goal arriving over HTTP is something the person wants,
        // not work Aurora has agreed to do; how it decomposes is the Planner's decision, and a caller
        // handing in its own task list would be planning around the engine rather than through it.
        return ApiIdempotency.RunAsync(
            idempotency, principal, KeyOf(request), body, correlationId,
            token => planner.DraftAsync(goal, token), ct);
    }

    private static async Task<IResult> ReadGoalAsync(
        string id, HttpRequest request,
        IPlanner planner, ITaskService tasks, IPrincipalAccessor principals, CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        Goal? goal = await planner.GetGoalAsync(id, ct);

        // Rule 4 again: someone else's goal answers the same way a nonexistent one does.
        if (goal is null || !string.Equals(goal.OwnerId, principals.Current.ClientId, StringComparison.Ordinal))
        {
            return NotFound(correlationId, "No such goal.");
        }

        Plan? plan = await planner.GetActivePlanAsync(id, ct);
        IReadOnlyList<PlannedTask> planned = await tasks.ForGoalAsync(id, ct);

        return Results.Json(ApiEnvelopes.Ok(new { goal, plan, tasks = planned }, correlationId));
    }

    // ---- approvals: this is where the person decides ----

    private static Task<IResult> DecideApprovalAsync(
        string id, DecideApprovalBody body, HttpContext context,
        AuroraKernel kernel, IIdempotencyStore idempotency, IPrincipalAccessor principals,
        CancellationToken ct)
    {
        HttpRequest request = context.Request;
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        if (RequireOperator(context, correlationId) is { } refused)
        {
            return Task.FromResult(refused);
        }

        // Through the Kernel rather than straight to the store, so an approval decided here is
        // subject to the same passphrase check and leaves the same audit trail as one decided
        // over MCP. Two surfaces, one decision path.
        return ApiIdempotency.RunAsync(
            idempotency, principals.Current, KeyOf(request), new { id, body.Decision }, correlationId,
            token => kernel.ApproveAsync(
                new ApproveRequest(id, body.Decision, body.Passphrase), principals.Current, token),
            ct);
    }

    // ---- memories: search, correct, forget ----

    private static async Task<IResult> SearchMemoriesAsync(
        string? q, string? kind, string? subject, HttpRequest request,
        IMemoryService memories, IPrincipalAccessor principals, CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        // Rule 3: authorization is applied here, on the server. A client never receives everything
        // and filters locally, because that is not filtering — it is disclosure with extra steps.
        MemorySearchResult result = await memories.SearchAsync(
            q ?? string.Empty, AccessFor(principals.Current),
            new MemoryFilters(Kind: kind, SubjectRef: subject), ct);

        return Results.Json(ApiEnvelopes.Ok(result, correlationId));
    }

    private static async Task<IResult> CorrectMemoryAsync(
        string id, CorrectMemoryBody body, HttpContext context,
        IMemoryService memories, IIdempotencyStore idempotency, IPrincipalAccessor principals,
        CancellationToken ct)
    {
        HttpRequest request = context.Request;
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        if (RequireOperator(context, correlationId) is { } refused)
        {
            return refused;
        }

        if (await HiddenAsync(memories, principals, id, ct))
        {
            return NotFound(correlationId, "No such memory.");
        }

        return await ApiIdempotency.RunAsync(
            idempotency, principals.Current, KeyOf(request), new { id, body.Reason }, correlationId,
            token => memories.ReviseAsync(
                id, RevisionOperation.Correct, MemoryOrigin.User, body.Reason, token),
            ct);
    }

    private static async Task<IResult> ForgetMemoryAsync(
        string id, HttpContext context,
        IMemoryService memories, IIdempotencyStore idempotency, IPrincipalAccessor principals,
        CancellationToken ct)
    {
        HttpRequest request = context.Request;
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        if (RequireOperator(context, correlationId) is { } refused)
        {
            return refused;
        }

        if (await HiddenAsync(memories, principals, id, ct))
        {
            return NotFound(correlationId, "No such memory.");
        }

        // The tombstone reports what forgetting actually removed, rather than claiming it removed
        // everything (RFC 03). That answer is the point of the endpoint, so it is returned as-is.
        return await ApiIdempotency.RunAsync(
            idempotency, principals.Current, KeyOf(request), new { id, op = "forget" }, correlationId,
            token => memories.ForgetAsync(id, MemoryOrigin.User, token), ct);
    }

    // ---- audit ----

    private static async Task<IResult> ReadAuditAsync(
        long? after, int? limit, HttpRequest request, IAuditStore audit, CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);
        var size = limit ?? DefaultPageSize;
        IReadOnlyList<AuditRecordView> page = await audit.QueryAsync(after ?? 0, size, ct);

        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        if (page.Count > 0)
        {
            links["next"] = $"/v1/audit?after={page[^1].Sequence}&limit={size}";
        }

        return Results.Json(ApiEnvelopes.Ok(page, correlationId, links));
    }

    /// <summary>
    /// What Aurora is offering to do, and what each of those costs in permission.
    /// </summary>
    /// <remarks>
    /// The same list the agent sees through <c>aurora_catalog</c>. Shown to the person too, because
    /// "what is this thing allowed to do" is the first question anyone reasonably asks, and it
    /// should not require reading a config file to answer.
    /// </remarks>
    private static IResult ReadCatalog(string? query, HttpRequest request, AuroraKernel kernel) =>
        Results.Json(ApiEnvelopes.Ok(kernel.Catalog(query), ApiEnvelopes.CorrelationOf(request)));

    /// <summary>
    /// Why Aurora did what it did during one cycle (RFC 025).
    /// </summary>
    /// <remarks>
    /// Returns explanations — reason, sources, what happened next — and never the working notes
    /// behind them. Those are encrypted, expire in a week, and no interface can reach them; a
    /// transcript of intermediate reasoning is not an explanation, and handing one over would be
    /// answering a different question than the one asked.
    /// </remarks>
    private static async Task<IResult> ExplainAsync(
        string id, HttpRequest request, IDeliberationService deliberation, CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);
        IReadOnlyList<Thought> thoughts = await deliberation.ThoughtsForCycleAsync(id, ct);

        return Results.Json(ApiEnvelopes.Ok(thoughts, correlationId));
    }

    // ---- who Aurora is: read by anyone with the panel, changed only by a person ----

    private static async Task<IResult> ReadPersonalityAsync(
        string? channel, HttpRequest request,
        IPersonalityService personality, IPrincipalAccessor principals, CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        ResolvedProfile resolved = await personality.ResolveAsync(
            principals.Current.ClientId, channel ?? "local", DateTimeOffset.UtcNow, ct);

        return Results.Json(ApiEnvelopes.Ok(resolved, correlationId));
    }

    /// <summary>
    /// Changes how Aurora speaks to somebody.
    /// </summary>
    /// <remarks>
    /// Operator only, and this is where RFC 07's limit case is actually closed: a request to change
    /// Aurora's personality arriving inside a third-party message is ignored unless there is
    /// authenticated delegation. The agent relays such a message; it does not hold the credential
    /// that acts on one, so relaying is all it can do.
    /// </remarks>
    private static async Task<IResult> SetCommunicationPreferenceAsync(
        CommunicationPreferenceBody body, HttpContext context,
        IPersonalityService personality, IIdempotencyStore idempotency,
        IPrincipalAccessor principals, CancellationToken ct)
    {
        HttpRequest request = context.Request;
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        if (RequireOperator(context, correlationId) is { } refused)
        {
            return refused;
        }

        return await ApiIdempotency.RunAsync(
            idempotency, principals.Current, KeyOf(request), body, correlationId,
            token => personality.SetPreferenceAsync(
                new CommunicationPreference(
                    principals.Current.ClientId, body.Channel, body.Language,
                    body.Verbosity, body.QuietHours, "{}", body.ConsentForProactivity, ""),
                token),
            ct);
    }

    /// <summary>Makes a drafted identity the active one. The owner's decision, and nobody else's.</summary>
    private static async Task<IResult> ActivatePersonalityAsync(
        string id, ActivateProfileBody body, HttpContext context,
        IPersonalityService personality, IIdempotencyStore idempotency,
        IPrincipalAccessor principals, CancellationToken ct)
    {
        HttpRequest request = context.Request;
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        if (RequireOperator(context, correlationId) is { } refused)
        {
            return refused;
        }

        return await ApiIdempotency.RunAsync(
            idempotency, principals.Current, KeyOf(request), new { id, body.Reason }, correlationId,
            token => personality.ActivateAsync(
                id, body.ApprovalRef, principals.Current.OsUser, body.Reason, token),
            ct);
    }

    // ---- status and upkeep ----

    /// <summary>
    /// What Aurora currently has, notices and is waiting on.
    /// </summary>
    /// <remarks>
    /// The observability half of the steps 10–12 gate. An automation that cannot be looked at is
    /// not limited by anything, whatever its rules say.
    /// </remarks>
    private static async Task<IResult> ReadStatusAsync(
        string? timezone, HttpRequest request,
        ISituationService situation, IResourceModel resources,
        ISignalService signals, INeedsService needs, IScheduler schedules,
        CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);

        SituationAssessment assessment;
        try
        {
            assessment = await situation.AssessAsync(
                new SituationContext(timezone ?? TimeZoneInfo.Local.Id), ct);
        }
        catch (SituationException unknownZone)
        {
            return BadRequest(correlationId, unknownZone.Message, nameof(timezone));
        }

        return Results.Json(ApiEnvelopes.Ok(
            new
            {
                situation = assessment,
                resources = await resources.ObserveAsync(ct),
                signals = await signals.PendingAsync(ct),
                needs = await needs.RankAsync(ct),
                schedules = await schedules.ListAsync(null, ct),
            },
            correlationId));
    }

    /// <summary>
    /// Runs one upkeep pass on demand.
    /// </summary>
    /// <remarks>
    /// Safe to expose because of what maintenance is not allowed to do: it expires, decays,
    /// reconciles and notices, and runs nothing it finds. Everything it surfaces still goes through
    /// the cycle.
    /// </remarks>
    private static Task<IResult> RunMaintenanceAsync(
        string? timezone, HttpRequest request,
        IMaintenanceService maintenance, IIdempotencyStore idempotency, IPrincipalAccessor principals,
        CancellationToken ct)
    {
        var correlationId = ApiEnvelopes.CorrelationOf(request);
        var zone = timezone ?? TimeZoneInfo.Local.Id;

        return ApiIdempotency.RunAsync(
            idempotency, principals.Current, KeyOf(request), new { zone }, correlationId,
            token => maintenance.RunAsync(new SituationContext(zone), token), ct);
    }

    // ---- stream ----

    private static async Task StreamAsync(
        HttpContext context, long? after, IEventBus bus, CancellationToken ct)
    {
        // SSE rather than WebSocket: the stream is one-way by design. A client that could send on
        // this channel would have a second command path that skips the checks the commands carry.
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        var cursor = after ?? 0;

        // Resume by cursor with bounded retention: the client keeps the last id it saw and asks for
        // what follows, so a dropped connection costs a reconnect and not a gap.
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<SequencedEvent> page = await bus.ReadAsync(
                cursor, DefaultPageSize, StreamCeiling, ct);

            if (page.Count == 0)
            {
                break;
            }

            foreach (SequencedEvent item in page)
            {
                var frame = new StringBuilder()
                    .Append("id: ").Append(item.Sequence).Append('\n')
                    .Append("event: ").Append(item.Event.Type).Append('\n')
                    .Append("data: ").Append(AuroraJson.Serialize(item.Event)).Append("\n\n")
                    .ToString();

                await context.Response.WriteAsync(frame, ct);
                cursor = item.Sequence;
            }

            await context.Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>
    /// The ceiling for the event stream.
    /// </summary>
    /// <remarks>
    /// PRIVATE, not the owner's full CONFIDENTIAL reach. A stream is a standing subscription that
    /// keeps delivering long after the operator stopped watching it, so it is held one class below
    /// what a deliberate request can reach; classified material is read through the endpoint that
    /// asks for it by name.
    /// </remarks>
    private const string StreamCeiling = Sensitivity.Private;

    /// <summary>
    /// Whether a memory should be treated as absent for this caller.
    /// </summary>
    /// <remarks>
    /// RFC 10 rule 4: an unauthorized caller must not learn that a sensitive resource exists. So a
    /// memory outside their reach answers 404 rather than 403 — a refusal would confirm it is there.
    /// </remarks>
    private static async Task<bool> HiddenAsync(
        IMemoryService memories, IPrincipalAccessor principals, string id, CancellationToken ct)
    {
        MemoryRecord? memory = await memories.GetAsync(id, ct);
        if (memory is null)
        {
            return true;
        }

        MemoryAccessContext access = AccessFor(principals.Current);

        return !access.AccessPolicyIds.Contains(memory.AccessPolicyId, StringComparer.Ordinal)
            || Sensitivity.Rank(memory.SensitivityClass) > Sensitivity.Rank(access.MaxSensitivity);
    }

    /// <summary>
    /// The local owner's reach. Single-principal by construction on a local deployment; a hosted
    /// Aurora would resolve the policy set per caller instead of assuming one.
    /// </summary>
    private static MemoryAccessContext AccessFor(Principal principal) =>
        new(principal.ClientId, [MemoryAccessPolicy.Owner], Sensitivity.Confidential);

    /// <summary>
    /// Refuses a deciding request that did not come from a person.
    /// </summary>
    /// <remarks>
    /// RFC 11 makes the panel the place where impact actions are approved, and the whole value of
    /// that is that the panel needs a credential the agent does not hold. Approving, correcting,
    /// forgetting and revoking therefore require an operator session; the bearer token, which
    /// belongs to the MCP client, is not enough. Without this the agent could approve its own
    /// request simply by calling the endpoint instead of the tool.
    /// </remarks>
    private static IResult? RequireOperator(HttpContext context, string correlationId) =>
        RequestActor.IsOperator(context)
            ? null
            : Results.Json(
                ApiEnvelopes.Fail(
                    correlationId, ApiErrorCode.Forbidden,
                    "This decision is made by a person. Run 'ui' on the Aurora console to open the "
                    + "control panel."),
                statusCode: StatusCodes.Status403Forbidden);

    private static string? KeyOf(HttpRequest request) =>
        request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : null;

    private static IResult NotFound(string correlationId, string message) =>
        Results.Json(
            ApiEnvelopes.Fail(correlationId, ApiErrorCode.NotFound, message),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult BadRequest(string correlationId, string message, string field) =>
        Results.Json(
            ApiEnvelopes.Fail(correlationId, ApiErrorCode.Invalid, message, field: field),
            statusCode: StatusCodes.Status400BadRequest);
}
