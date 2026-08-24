using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.World;

/// <summary>Tuning for identity resolution (RFC 041 rule 2).</summary>
public sealed record WorldModelOptions(double MatchThreshold = 0.9, int MinimumEvidenceForMatch = 1)
{
    public static readonly WorldModelOptions Default = new();
}

/// <summary>Temporal, evidenced representation of operational reality (RFC 041).</summary>
public sealed class SqliteWorldModel : IWorldModel
{
    /// <summary>Actors that may observe but never conclude (rule 5).</summary>
    public const string ToolActor = "TOOL";

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly WorldModelOptions _options;

    public SqliteWorldModel(SqliteConnectionFactory factory, IClock clock, WorldModelOptions options)
    {
        _factory = factory;
        _clock = clock;
        _options = options;
    }

    public async Task<WorldModelVersion> BeginVersionAsync(
        string mindId, string? parentVersionId, CancellationToken ct)
    {
        var version = new WorldModelVersion(
            Guid.NewGuid().ToString("N"), mindId, parentVersionId,
            WorldVersionStatus.Draft, Iso(_clock.UtcNow));

        await ExecuteAsync(
            "INSERT INTO world_version (id, mind_id, parent_version_id, status, created_at_utc) "
            + "VALUES (@id, @mind, @parent, @status, @at);", ct,
            ("@id", version.Id), ("@mind", mindId),
            ("@parent", (object?)parentVersionId ?? DBNull.Value),
            ("@status", version.Status), ("@at", version.CreatedAtUtc)).ConfigureAwait(false);

        return version;
    }

    public async Task<WorldModelVersion> ActivateVersionAsync(
        string versionId, string actor, CancellationToken ct)
    {
        if (actor == ToolActor)
        {
            throw new WorldModelException("A tool cannot activate a world model version.");
        }

        await ExecuteAsync(
            "UPDATE world_version SET status = @active WHERE id = @id AND status = @draft;", ct,
            ("@active", WorldVersionStatus.Active), ("@id", versionId),
            ("@draft", WorldVersionStatus.Draft)).ConfigureAwait(false);

        return await GetVersionAsync(versionId, ct).ConfigureAwait(false)
            ?? throw new WorldModelException("Unknown version.");
    }

    public async Task<WorldAssertion> ObserveAsync(
        WorldObservation observation, string versionId, CancellationToken ct)
    {
        if (observation.EvidenceRefs.Count == 0)
        {
            throw new WorldModelException("An observation must carry evidence.");
        }

        // Rule 5: an observation enters as PROPOSED. A tool may see; concluding is someone else's
        // job, and the status is where that boundary is enforced rather than assumed.
        var assertion = new WorldAssertion(
            Guid.NewGuid().ToString("N"), observation.SubjectRef, observation.Predicate,
            observation.Category, observation.ObjectRef, observation.Literal,
            observation.EvidenceRefs, Math.Clamp(observation.Confidence, 0, 1),
            observation.ValidFromUtc, observation.ValidToUtc,
            // Rule 1: when it happened and when we said so are different facts.
            observation.ObservedAtUtc, Iso(_clock.UtcNow),
            WorldAssertionStatus.Proposed, versionId);

        await InsertAsync(assertion, ct).ConfigureAwait(false);
        return assertion;
    }

    public async Task<WorldAssertion> ValidateAsync(
        string assertionId, string actor, IReadOnlyList<string> evidenceRefs, CancellationToken ct)
    {
        if (actor == ToolActor)
        {
            throw new WorldModelException(
                "A tool can create an observation but never a CURRENT assertion.");
        }

        WorldAssertion assertion = await GetAsync(assertionId, ct).ConfigureAwait(false)
            ?? throw new WorldModelException("Unknown assertion.");

        if (assertion.Status != WorldAssertionStatus.Proposed)
        {
            throw new WorldModelException($"Only a PROPOSED assertion is validated; this is {assertion.Status}.");
        }

        var evidence = assertion.EvidenceRefs.Concat(evidenceRefs).Distinct(StringComparer.Ordinal).ToList();

        IReadOnlyList<WorldAssertion> overlapping =
            await OverlappingCurrentAsync(assertion, ct).ConfigureAwait(false);

        DateTimeOffset newFrom = Parse(assertion.ValidFromUtc) ?? DateTimeOffset.MinValue;

        var differing = overlapping
            .Where(a => !string.Equals(a.ObjectRef ?? a.Literal, assertion.ObjectRef ?? assertion.Literal,
                StringComparison.Ordinal))
            .ToList();

        // RFC 041 names two different limit cases and they turn on time, not on content. A claim
        // that starts later than an open one is a reassociation: the previous relationship ends.
        // A claim about the same period is a contradiction: both stay, in parallel.
        var successions = differing
            .Where(a => newFrom > (Parse(a.ValidFromUtc) ?? DateTimeOffset.MinValue))
            .ToList();

        var contradictions = differing.Except(successions).ToList();

        if (contradictions.Count > 0)
        {
            // Limit case: keep parallel statements DISPUTED. Choosing by which phrasing appears
            // more often is exactly the inference the RFC forbids.
            foreach (WorldAssertion other in contradictions)
            {
                await SetStatusAsync(other.Id, WorldAssertionStatus.Disputed, ct).ConfigureAwait(false);
            }

            WorldAssertion disputed = assertion with
            {
                Status = WorldAssertionStatus.Disputed,
                EvidenceRefs = evidence,
            };
            await UpdateAsync(disputed, ct).ConfigureAwait(false);
            return disputed;
        }

        // Reassociation ends the previous window; it never rewrites it.
        foreach (WorldAssertion previous in successions.Concat(
                     overlapping.Except(differing)))
        {
            await CloseAsync(previous.Id, assertion.ValidFromUtc, ct).ConfigureAwait(false);
        }

        WorldAssertion current = assertion with
        {
            Status = WorldAssertionStatus.Current,
            EvidenceRefs = evidence,
            AssertedAtUtc = Iso(_clock.UtcNow),
        };

        await UpdateAsync(current, ct).ConfigureAwait(false);
        return current;
    }

    public async Task<EntityResolution> ResolveAsync(
        EntityCandidate candidate, string decidedBy, CancellationToken ct)
    {
        // Rule 2: identity is merged only with a resolution rule and sufficient evidence. Anything
        // short of both defers, and deferring is a real answer rather than a failure.
        var enoughEvidence = candidate.EvidenceRefs.Count >= _options.MinimumEvidenceForMatch;
        var confident = candidate.MatchScore >= _options.MatchThreshold;

        var decision = (candidate.SuggestedEntityRef, confident, enoughEvidence) switch
        {
            (not null, true, true) => ResolutionDecision.Match,
            (not null, _, _) => ResolutionDecision.Defer,
            (null, _, true) => ResolutionDecision.Create,
            _ => ResolutionDecision.Defer,
        };

        var resolution = new EntityResolution(
            Guid.NewGuid().ToString("N"), candidate.ObservedName, candidate.MatchScore,
            candidate.EvidenceRefs, decision, decidedBy, Iso(_clock.UtcNow),
            decision == ResolutionDecision.Match ? candidate.SuggestedEntityRef : null);

        await ExecuteAsync("""
            INSERT INTO entity_resolution
                (id, candidate_ref, observed_name, match_score, evidence_refs, decision,
                 decided_by, decided_at_utc, matched_entity_ref)
            VALUES (@id, @cref, @name, @score, @ev, @decision, @by, @at, @matched);
            """, ct,
            ("@id", resolution.CandidateRef), ("@cref", resolution.CandidateRef),
            ("@name", resolution.ObservedName), ("@score", resolution.MatchScore),
            ("@ev", string.Join(',', resolution.EvidenceRefs)), ("@decision", resolution.Decision),
            ("@by", decidedBy), ("@at", resolution.DecidedAtUtc),
            ("@matched", (object?)resolution.MatchedEntityRef ?? DBNull.Value)).ConfigureAwait(false);

        return resolution;
    }

    public async Task<WorldAnswer> AskAsync(
        string subjectRef, string predicate, DateTimeOffset asOf, CancellationToken ct)
    {
        IReadOnlyList<WorldAssertion> all =
            await ForSubjectAsync(subjectRef, predicate, category: null, ct).ConfigureAwait(false);

        return Answer(all, asOf, $"{subjectRef} {predicate}");
    }

    public async Task<WorldAnswer> HasAccessAsync(
        string subjectRef, string resourceRef, DateTimeOffset asOf, CancellationToken ct)
    {
        // Rule 3: only ACCESS assertions answer this. Owning an account, or being in a server, is
        // a different claim — "the person has Discord" does not mean Aurora can read it.
        IReadOnlyList<WorldAssertion> access =
            await ForSubjectAsync(subjectRef, predicate: null, WorldPredicateCategory.Access, ct)
                .ConfigureAwait(false);

        var about = access.Where(a => a.ObjectRef == resourceRef).ToList();

        return Answer(about, asOf, $"evidenced access from {subjectRef} to {resourceRef}");
    }

    public async Task<int> MarkInaccessibleAsync(string subjectRef, string reason, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // The external thing is gone; the evidence about it stays. Deleting would destroy the
        // history that explains why Aurora believed anything about it.
        command.CommandText =
            "UPDATE world_assertion SET status = @inaccessible WHERE subject_ref = @s AND status = @current;";
        command.Parameters.AddWithValue("@inaccessible", WorldAssertionStatus.Inaccessible);
        command.Parameters.AddWithValue("@s", subjectRef);
        command.Parameters.AddWithValue("@current", WorldAssertionStatus.Current);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<WorldAssertion?> GetAsync(string assertionId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", assertionId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    // ---- internals ----

    /// <summary>
    /// Turns the record set into an answer, including the one the RFC insists on: not knowing.
    /// </summary>
    private static WorldAnswer Answer(
        IReadOnlyList<WorldAssertion> assertions, DateTimeOffset asOf, string subject)
    {
        // Half-open windows: [valid_from, valid_to). A boundary instant belongs to exactly one
        // interval, so a handover never reads as two simultaneous truths or as a gap.
        var atTime = assertions
            .Where(a => Covers(a, asOf) && a.Status is not (WorldAssertionStatus.Proposed
                or WorldAssertionStatus.Retracted))
            .ToList();

        if (atTime.Any(a => a.Status == WorldAssertionStatus.Disputed))
        {
            return new WorldAnswer(
                WorldKnowledge.Disputed, atTime,
                $"Parallel claims about {subject} disagree; no choice was inferred.");
        }

        var current = atTime
            .Where(a => a.Status is WorldAssertionStatus.Current or WorldAssertionStatus.Inaccessible)
            .ToList();

        if (current.Count > 0)
        {
            return new WorldAnswer(WorldKnowledge.Asserted, current, $"Asserted for {subject}.");
        }

        var historical = assertions.Where(a => a.Status == WorldAssertionStatus.Historical).ToList();
        if (historical.Count > 0)
        {
            return new WorldAnswer(
                WorldKnowledge.OnlyHistorical, historical,
                $"Nothing current for {subject}; only past records.");
        }

        // Rule 4: the absence of an edge is a fact about Aurora, not about the world.
        return new WorldAnswer(
            WorldKnowledge.Unknown, [],
            $"Nothing recorded about {subject}. This is not evidence that it is untrue.");
    }

    private static bool Covers(WorldAssertion a, DateTimeOffset asOf)
    {
        DateTimeOffset from = Parse(a.ValidFromUtc) ?? DateTimeOffset.MinValue;
        DateTimeOffset to = Parse(a.ValidToUtc) ?? DateTimeOffset.MaxValue;
        return asOf >= from && asOf < to;
    }

    private async Task<IReadOnlyList<WorldAssertion>> ForSubjectAsync(
        string subjectRef, string? predicate, string? category, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // A DRAFT version is not a source for decisions until its import is validated.
        command.CommandText = Select + """
             WHERE subject_ref = @s
               AND (@p IS NULL OR predicate = @p)
               AND (@c IS NULL OR category = @c)
               AND version_id IN (SELECT id FROM world_version WHERE status = @active);
            """;
        command.Parameters.AddWithValue("@s", subjectRef);
        command.Parameters.AddWithValue("@p", (object?)predicate ?? DBNull.Value);
        command.Parameters.AddWithValue("@c", (object?)category ?? DBNull.Value);
        command.Parameters.AddWithValue("@active", WorldVersionStatus.Active);

        var rows = new List<WorldAssertion>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    private async Task<IReadOnlyList<WorldAssertion>> OverlappingCurrentAsync(
        WorldAssertion assertion, CancellationToken ct)
    {
        IReadOnlyList<WorldAssertion> all = await ForSubjectAsync(
            assertion.SubjectRef, assertion.Predicate, category: null, ct).ConfigureAwait(false);

        DateTimeOffset from = Parse(assertion.ValidFromUtc) ?? DateTimeOffset.MinValue;
        DateTimeOffset to = Parse(assertion.ValidToUtc) ?? DateTimeOffset.MaxValue;

        return all.Where(a =>
                a.Id != assertion.Id
                && a.Status == WorldAssertionStatus.Current
                && (Parse(a.ValidFromUtc) ?? DateTimeOffset.MinValue) < to
                && from < (Parse(a.ValidToUtc) ?? DateTimeOffset.MaxValue))
            .ToList();
    }

    private Task CloseAsync(string id, string validToUtc, CancellationToken ct) =>
        ExecuteAsync(
            "UPDATE world_assertion SET valid_to_utc = @to, status = @historical WHERE id = @id;", ct,
            ("@to", validToUtc), ("@historical", WorldAssertionStatus.Historical), ("@id", id));

    private Task SetStatusAsync(string id, string status, CancellationToken ct) =>
        ExecuteAsync("UPDATE world_assertion SET status = @s WHERE id = @id;", ct,
            ("@s", status), ("@id", id));

    private Task InsertAsync(WorldAssertion a, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO world_assertion
                (id, subject_ref, predicate, category, object_ref, literal, evidence_refs, confidence,
                 valid_from_utc, valid_to_utc, observed_at_utc, asserted_at_utc, status, version_id)
            VALUES (@id, @s, @p, @cat, @o, @lit, @ev, @conf, @from, @to, @obs, @ass, @status, @ver);
            """, ct,
            ("@id", a.Id), ("@s", a.SubjectRef), ("@p", a.Predicate), ("@cat", a.Category),
            ("@o", (object?)a.ObjectRef ?? DBNull.Value), ("@lit", (object?)a.Literal ?? DBNull.Value),
            ("@ev", string.Join(',', a.EvidenceRefs)), ("@conf", a.Confidence),
            ("@from", a.ValidFromUtc), ("@to", (object?)a.ValidToUtc ?? DBNull.Value),
            ("@obs", a.ObservedAtUtc), ("@ass", a.AssertedAtUtc), ("@status", a.Status), ("@ver", a.VersionId));

    private Task UpdateAsync(WorldAssertion a, CancellationToken ct) =>
        ExecuteAsync("""
            UPDATE world_assertion
               SET status = @status, evidence_refs = @ev, asserted_at_utc = @ass
             WHERE id = @id;
            """, ct,
            ("@status", a.Status), ("@ev", string.Join(',', a.EvidenceRefs)),
            ("@ass", a.AssertedAtUtc), ("@id", a.Id));

    private async Task<WorldModelVersion?> GetVersionAsync(string id, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, mind_id, parent_version_id, status, created_at_utc FROM world_version WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new WorldModelVersion(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
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

    private const string Select = """
        SELECT id, subject_ref, predicate, category, object_ref, literal, evidence_refs, confidence,
               valid_from_utc, valid_to_utc, observed_at_utc, asserted_at_utc, status, version_id
          FROM world_assertion
        """;

    private static WorldAssertion Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries), r.GetDouble(7),
        r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.GetString(10), r.GetString(11), r.GetString(12), r.GetString(13));

    private static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
