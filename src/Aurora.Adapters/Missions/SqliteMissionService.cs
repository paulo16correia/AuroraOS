using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Missions;

/// <summary>
/// Missions (RFC 052): what Aurora is for, decided by the person it is for.
/// </summary>
/// <remarks>
/// Nothing here grants anything. Aligning a goal to a mission changes what that goal is <i>for</i>
/// and never what it is allowed to do — a mission is not an execution order and does not stand in
/// for the approval a risky task needs (rule 1).
/// <para>
/// Every mutation takes an actor and refuses the system as one. Rule 4 says missions are reviewed,
/// paused and removed by their owner and do not evolve by automatic inference; a system that could
/// revise its own purpose would have, in the only sense that matters, no purpose at all.
/// </para>
/// </remarks>
public sealed class SqliteMissionService : IMissionService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IPlanner _planner;
    private readonly IEventBus _bus;
    private readonly IClock _clock;

    public SqliteMissionService(SqliteConnectionFactory factory, IPlanner planner, IEventBus bus, IClock clock)
    {
        _factory = factory;
        _planner = planner;
        _clock = clock;
        _bus = bus;
    }

    public async Task<Mission> CreateAsync(
        MissionDefinition definition, string approvalRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition.Purpose)
            || string.IsNullOrWhiteSpace(definition.SuccessDefinition))
        {
            throw new MissionException(
                "A mission states a purpose and how it would be known to be succeeding.");
        }

        // A purpose with no stated edge quietly grows one. The boundaries are the owner's, written
        // down at the moment the mission is defined rather than argued about afterwards.
        if (definition.Boundaries.Count == 0)
        {
            throw new MissionException(
                "A mission states what it does not extend to; an unbounded purpose is not one.");
        }

        // Rule 4: deciding what Aurora is for over months is a decision only a person makes.
        if (string.IsNullOrWhiteSpace(approvalRef))
        {
            throw new MissionException("A mission needs the owner's approval to exist.");
        }

        RequireHuman(definition.Owner);

        var mission = new Mission(
            Guid.NewGuid().ToString("N"), definition.MindId, definition.Title, definition.Purpose,
            definition.SuccessDefinition, definition.Boundaries, definition.PriorityPolicy,
            definition.Owner, MissionStatus.Draft, definition.ReviewAtUtc, EvidenceRefs: [],
            Iso(_clock.UtcNow), approvalRef);

        await ExecuteAsync("""
            INSERT INTO mission
                (id, mind_id, title, purpose, success_definition, boundaries, priority_policy,
                 owner, status, review_at_utc, evidence_refs, created_at_utc, approval_ref)
            VALUES (@id, @mind, @title, @purpose, @success, @bounds, @priority, @owner, @status,
                    @review, '', @at, @approval);
            """, ct,
            ("@id", mission.Id), ("@mind", mission.MindId), ("@title", mission.Title),
            ("@purpose", mission.Purpose), ("@success", mission.SuccessDefinition),
            ("@bounds", string.Join('\n', mission.Boundaries)),
            ("@priority", mission.PriorityPolicy), ("@owner", mission.Owner),
            ("@status", mission.Status),
            ("@review", (object?)mission.ReviewAtUtc ?? DBNull.Value),
            ("@at", mission.CreatedAtUtc), ("@approval", approvalRef)).ConfigureAwait(false);

        return mission;
    }

    public async Task<Goal> AlignAsync(string goalId, string missionId, string actor, CancellationToken ct)
    {
        RequireHuman(actor);

        Mission mission = await RequireAsync(missionId, ct).ConfigureAwait(false);

        if (mission.Status is MissionStatus.Retired or MissionStatus.Paused)
        {
            throw new MissionException(
                $"A {mission.Status} mission does not take on new goals; activate it or choose another.");
        }

        Goal goal = await _planner.GetGoalAsync(goalId, ct).ConfigureAwait(false)
            ?? throw new MissionException("Unknown goal.");

        // Aligned, so no longer drifting: the ad-hoc review date exists precisely for goals that
        // belong to no mission, and this one now belongs to one.
        await ExecuteAsync(
            "UPDATE goal SET mission_ref = @m, ad_hoc_review_at_utc = NULL WHERE id = @id;", ct,
            ("@m", missionId), ("@id", goalId)).ConfigureAwait(false);

        return goal with { MissionRef = missionId, AdHocReviewAtUtc = null };
    }

    public async Task<MissionReview> ReviewAsync(string missionId, CancellationToken ct)
    {
        Mission mission = await RequireAsync(missionId, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.UtcNow;

        IReadOnlyList<(string Id, string? Mission, string? AdHoc)> goals =
            await GoalAlignmentAsync(ct).ConfigureAwait(false);

        var aligned = goals
            .Where(g => string.Equals(g.Mission, missionId, StringComparison.Ordinal))
            .Select(g => g.Id)
            .ToList();

        // Rule 2's real purpose: catch the goals that belong to nothing and have quietly outlived
        // the date somebody said they would look at them again.
        var drifting = goals
            .Where(g => g.Mission is null && g.AdHoc is not null && Parse(g.AdHoc) <= now)
            .Select(g => g.Id)
            .ToList();

        var overdue = mission.ReviewAtUtc is { } due && Parse(due) <= now;

        var summary = (aligned.Count, drifting.Count, overdue) switch
        {
            (0, 0, false) => "nothing is aligned to this mission yet",
            (_, 0, false) => $"{aligned.Count} goal(s) aligned; nothing is drifting",
            (_, _, true) => $"{aligned.Count} goal(s) aligned; this mission is overdue a review",
            _ => $"{aligned.Count} goal(s) aligned; {drifting.Count} unaligned goal(s) are past review",
        };

        // Reports, and changes nothing. What to do about a drifting goal is the owner's call, and
        // a review that quietly retired things would be making it for them.
        return new MissionReview(missionId, Iso(now), aligned, drifting, overdue, summary);
    }

    public Task<Mission> PauseAsync(string missionId, string actor, CancellationToken ct) =>
        SetStatusAsync(missionId, MissionStatus.Paused, actor, ct);

    public Task<Mission> ActivateAsync(string missionId, string actor, CancellationToken ct) =>
        SetStatusAsync(missionId, MissionStatus.Active, actor, ct);

    public Task<Mission> RetireAsync(string missionId, string actor, CancellationToken ct) =>
        SetStatusAsync(missionId, MissionStatus.Retired, actor, ct);

    public async Task<Mission?> GetAsync(string missionId, CancellationToken ct)
    {
        IReadOnlyList<Mission> found = await ReadAsync(
            $"{Select} WHERE id = @id;", ct, ("@id", missionId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    public Task<IReadOnlyList<Mission>> ListAsync(string? owner, CancellationToken ct) =>
        owner is null
            ? ReadAsync($"{Select} ORDER BY created_at_utc;", ct)
            : ReadAsync($"{Select} WHERE owner = @o ORDER BY created_at_utc;", ct, ("@o", owner));

    // ---- plumbing ----

    /// <summary>
    /// Refuses the system as an actor on a mission.
    /// </summary>
    /// <remarks>
    /// Rule 4, enforced rather than trusted. Aurora may notice that a mission looks stale and say
    /// so in a review; it may not act on that by revising what it is for.
    /// </remarks>
    private static void RequireHuman(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.Equals(actor, NeedOwner.System, StringComparison.Ordinal))
        {
            throw new MissionException(
                "A mission is defined and changed by its owner; Aurora does not decide what it is for.");
        }
    }

    private async Task<Mission> SetStatusAsync(
        string missionId, string status, string actor, CancellationToken ct)
    {
        RequireHuman(actor);

        Mission mission = await RequireAsync(missionId, ct).ConfigureAwait(false);

        if (mission.Status == MissionStatus.Retired)
        {
            throw new MissionException("A retired mission is not revived; define a new one.");
        }

        await ExecuteAsync("UPDATE mission SET status = @s WHERE id = @id;", ct,
            ("@s", status), ("@id", missionId)).ConfigureAwait(false);

        // LAW-007: what Aurora is for changing is a state change the panel and the review both
        // need to see. The status travels; the purpose text does not.
        await _bus.PublishAsync(
            new OutboxWrite(
                EventCatalogue.MissionChanged, 1, EventCatalogue.Producers.Missions,
                Guid.NewGuid().ToString("N"), Sensitivity.Private,
                AggregateRef: $"mission/{missionId}",
                PayloadJson: AuroraJson.Serialize(new { mission_id = missionId, status }),
                IdempotencyKey: $"mission:{missionId}:{status}:{Iso(_clock.UtcNow)}"),
            ct).ConfigureAwait(false);

        return mission with { Status = status };
    }

    private async Task<IReadOnlyList<(string Id, string? Mission, string? AdHoc)>> GoalAlignmentAsync(
        CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, mission_ref, ad_hoc_review_at_utc
              FROM goal WHERE status NOT IN ('COMPLETED', 'CANCELLED', 'FAILED');
            """;

        var rows = new List<(string, string?, string?)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    private async Task<Mission> RequireAsync(string missionId, CancellationToken ct) =>
        await GetAsync(missionId, ct).ConfigureAwait(false)
        ?? throw new MissionException("Unknown mission.");

    private const string Select = """
        SELECT id, mind_id, title, purpose, success_definition, boundaries, priority_policy,
               owner, status, review_at_utc, evidence_refs, created_at_utc, approval_ref
          FROM mission
        """;

    private async Task<IReadOnlyList<Mission>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var missions = new List<Mission>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            missions.Add(new Mission(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), Lines(reader.GetString(5)), reader.GetString(6),
                reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                Lines(reader.GetString(10)), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return missions;
    }

    private async Task ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
