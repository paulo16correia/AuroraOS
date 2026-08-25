using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Relationships;

/// <summary>
/// Relationships and preferences (RFC 029).
/// </summary>
/// <remarks>
/// Two things that look adjacent and are not: a relationship is a fact about the world, a
/// preference is a habit of the person, and neither is a permission. Nothing here can turn one into
/// another — rule 1 keeps relationship, permission and identity separate, and the way to keep them
/// separate is to have no method that crosses between them.
/// </remarks>
public sealed class SqliteRelationshipModel : IRelationshipModel
{
    /// <summary>How long an inferred preference stands before somebody has to look at it again.</summary>
    private static readonly TimeSpan PreferenceReview = TimeSpan.FromDays(60);

    private readonly SqliteConnectionFactory _factory;
    private readonly IKnowledgeGraph _graph;
    private readonly IClock _clock;

    public SqliteRelationshipModel(
        SqliteConnectionFactory factory, IKnowledgeGraph graph, IClock clock)
    {
        _factory = factory;
        _graph = graph;
        _clock = clock;
    }

    public async Task<RelationshipAssertion> AssertAsync(
        RelationshipCandidate candidate, IReadOnlyList<string> evidenceRefs, CancellationToken ct)
    {
        if (!RelationType.IsKnown(candidate.RelationType))
        {
            throw new RelationshipException($"Unknown relation type '{candidate.RelationType}'.");
        }

        if (!AuthorityScope.IsKnown(candidate.AuthorityScope))
        {
            throw new RelationshipException($"Unknown authority scope '{candidate.AuthorityScope}'.");
        }

        if (evidenceRefs.Count == 0)
        {
            throw new RelationshipException("A relationship states what demonstrates it.");
        }

        // Rule 1, at the one point where relationship and permission would otherwise blur. Being
        // someone's client is a fact about a tie; acting for them is a separate grant, and claiming
        // one needs the approval that grants it rather than the evidence that shows the tie.
        if (AuthorityScope.NeedsApproval(candidate.AuthorityScope)
            && string.IsNullOrWhiteSpace(candidate.AuthorizationRef))
        {
            throw new RelationshipException(
                $"{candidate.AuthorityScope} is authority, not a relationship. It needs its own approval.");
        }

        // Limit case: two people with the same name. Until entity resolution has settled which one
        // this is, asserting a tie would be attaching a fact to a guess.
        await RequireResolvedAsync(candidate.SubjectRef, ct).ConfigureAwait(false);
        await RequireResolvedAsync(candidate.ObjectRef, ct).ConfigureAwait(false);

        DateTimeOffset now = _clock.UtcNow;

        // Rule 3: a third party's relationship is stored only with an authorisation and a bounded
        // retention. The owner's own ties are theirs; everyone else's are somebody else's.
        var thirdParty = !candidate.SubjectRef.StartsWith("person/owner", StringComparison.Ordinal);
        if (thirdParty && string.IsNullOrWhiteSpace(candidate.AuthorizationRef))
        {
            throw new RelationshipException(
                "Storing someone else's relationship needs an authorisation and a retention.");
        }

        var relationship = new RelationshipAssertion(
            Guid.NewGuid().ToString("N"), candidate.SubjectRef, candidate.RelationType,
            candidate.ObjectRef, candidate.QualifiersJson, candidate.AuthorityScope,
            Math.Clamp(candidate.Confidence, 0, 1), evidenceRefs,
            candidate.ValidFromUtc ?? Iso(now), ValidToUtc: null, RelationshipStatus.Active,
            candidate.AuthorizationRef,
            thirdParty ? Iso(now + (candidate.Retention ?? TimeSpan.FromDays(365))) : null);

        await ExecuteAsync("""
            INSERT INTO relationship_assertion
                (id, subject_ref, relation_type, object_ref, qualifiers_json, authority_scope,
                 confidence, evidence_refs, valid_from_utc, valid_to_utc, status,
                 authorization_ref, retention_until_utc)
            VALUES (@id, @subject, @type, @object, @qualifiers, @scope, @confidence, @evidence,
                    @from, NULL, @status, @authorization, @retention);
            """, ct,
            ("@id", relationship.Id), ("@subject", relationship.SubjectRef),
            ("@type", relationship.RelationType), ("@object", relationship.ObjectRef),
            ("@qualifiers", relationship.QualifiersJson), ("@scope", relationship.AuthorityScope),
            ("@confidence", relationship.Confidence),
            ("@evidence", string.Join('\n', evidenceRefs)),
            ("@from", relationship.ValidFromUtc), ("@status", relationship.Status),
            ("@authorization", (object?)relationship.AuthorizationRef ?? DBNull.Value),
            ("@retention", (object?)relationship.RetentionUntilUtc ?? DBNull.Value))
            .ConfigureAwait(false);

        return relationship;
    }

    public async Task<RelationshipAssertion> EndAsync(
        string relationshipId, string evidenceRef, CancellationToken ct)
    {
        RelationshipAssertion relationship = await RequireAsync(relationshipId, ct).ConfigureAwait(false);

        if (relationship.ValidToUtc is not null)
        {
            throw new RelationshipException("This relationship already ended.");
        }

        var endedAt = Iso(_clock.UtcNow);
        var evidence = relationship.EvidenceRefs
            .Append(evidenceRef).Distinct(StringComparer.Ordinal).ToList();

        // Rule 4: the interval closes and nothing is deleted. Reassigning a relationship opens a
        // new one beside this; it does not rewrite what was true before.
        await ExecuteAsync("""
            UPDATE relationship_assertion
               SET valid_to_utc = @to, status = @ended, evidence_refs = @evidence
             WHERE id = @id;
            """, ct,
            ("@to", endedAt), ("@ended", RelationshipStatus.Ended),
            ("@evidence", string.Join('\n', evidence)), ("@id", relationshipId))
            .ConfigureAwait(false);

        return relationship with
        {
            ValidToUtc = endedAt, Status = RelationshipStatus.Ended, EvidenceRefs = evidence,
        };
    }

    public async Task<RelationshipAssertion> DisputeAsync(
        string relationshipId, string evidenceRef, string reason, CancellationToken ct)
    {
        RelationshipAssertion relationship = await RequireAsync(relationshipId, ct).ConfigureAwait(false);

        var evidence = relationship.EvidenceRefs
            .Append($"{evidenceRef} (against: {reason})").Distinct(StringComparer.Ordinal).ToList();

        await ExecuteAsync(
            "UPDATE relationship_assertion SET status = @disputed, evidence_refs = @e WHERE id = @id;",
            ct,
            ("@disputed", RelationshipStatus.Disputed), ("@e", string.Join('\n', evidence)),
            ("@id", relationshipId)).ConfigureAwait(false);

        return relationship with { Status = RelationshipStatus.Disputed, EvidenceRefs = evidence };
    }

    /// <summary>
    /// The ties in force at a moment — which is a question about the interval, not about now.
    /// </summary>
    /// <remarks>
    /// Half-open <c>[valid_from, valid_to)</c>, so a relationship that ended at noon was not in
    /// force at noon and <i>was</i> in force the day before. Filtering on the current status
    /// instead would make an ended relationship never have existed, which is precisely what rule 4
    /// forbids: the beginning and the end both have to survive.
    /// <para>
    /// What is excluded is contested and withdrawn: DISPUTED means the tie is contradicted and
    /// cannot be leant on, RETRACTED means it should never have been asserted, and PROPOSED means
    /// nobody has accepted it yet. ENDED is not excluded — the interval already handles it.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<RelationshipAssertion>> InForceAsync(
        string subjectRef, DateTimeOffset at, CancellationToken ct) =>
        ReadAsync($"""
            {Select}
             WHERE subject_ref = @subject
               AND status IN (@active, @ended)
               AND valid_from_utc <= @at
               AND (valid_to_utc IS NULL OR valid_to_utc > @at)
             ORDER BY valid_from_utc;
            """, ct,
            ("@subject", subjectRef), ("@active", RelationshipStatus.Active),
            ("@ended", RelationshipStatus.Ended), ("@at", Iso(at)));

    public Task<IReadOnlyList<RelationshipAssertion>> HistoryAsync(
        string subjectRef, CancellationToken ct) =>
        ReadAsync($"{Select} WHERE subject_ref = @subject ORDER BY valid_from_utc;", ct,
            ("@subject", subjectRef));

    // ---- preferences ----

    public async Task<Preference> SetExplicitAsync(
        string ownerRef, string subjectRef, string dimension, string valueJson,
        IReadOnlyList<string> evidenceRefs, CancellationToken ct)
    {
        DateTimeOffset now = _clock.UtcNow;

        // Limit case: an explicit preference displaces the inference it contradicts. Rejected
        // rather than deleted — what Aurora guessed, and that the person corrected it, is worth
        // being able to read later.
        await ExecuteAsync("""
            UPDATE preference
               SET status = @rejected
             WHERE owner_ref = @owner AND dimension = @dimension AND basis <> @explicit
               AND status IN (@active, @candidate);
            """, ct,
            ("@rejected", PreferenceStatus.Rejected), ("@owner", ownerRef),
            ("@dimension", dimension), ("@explicit", PreferenceBasis.Explicit),
            ("@active", PreferenceStatus.Active), ("@candidate", PreferenceStatus.Candidate))
            .ConfigureAwait(false);

        var preference = new Preference(
            Guid.NewGuid().ToString("N"), ownerRef, subjectRef, dimension, valueJson,
            Strength: 1.0, PreferenceBasis.Explicit, evidenceRefs, ScopeJson: "{}",
            PreferenceStatus.Active, Iso(now + PreferenceReview), ConsentRequired: false);

        await InsertAsync(preference, ct).ConfigureAwait(false);
        return preference;
    }

    public async Task<Preference> InferAsync(
        Preference candidate, IReadOnlyList<string> evidenceRefs, CancellationToken ct)
    {
        if (!PreferenceBasis.IsKnown(candidate.Basis) || candidate.Basis == PreferenceBasis.Explicit)
        {
            throw new RelationshipException(
                "Inference produces an OBSERVED or INFERRED preference; what the person said is set, not inferred.");
        }

        if (evidenceRefs.Count == 0)
        {
            throw new RelationshipException("An inferred preference states what it was inferred from.");
        }

        // An explicit preference already on file is not displaced by an inference. The person's
        // own words outrank a pattern about them, in that direction and not the other.
        IReadOnlyList<Preference> stated = await ReadPreferencesAsync("""
            SELECT id, owner_ref, subject_ref, dimension, value_json, strength, basis,
                   evidence_refs, scope_json, status, review_at_utc, consent_required
              FROM preference
             WHERE owner_ref = @owner AND dimension = @dimension AND basis = @explicit
               AND status = @active;
            """, ct,
            ("@owner", candidate.OwnerRef), ("@dimension", candidate.Dimension),
            ("@explicit", PreferenceBasis.Explicit), ("@active", PreferenceStatus.Active))
            .ConfigureAwait(false);

        DateTimeOffset now = _clock.UtcNow;

        var preference = candidate with
        {
            Id = Guid.NewGuid().ToString("N"),
            EvidenceRefs = evidenceRefs,
            Strength = Math.Clamp(candidate.Strength, 0, 1),
            Status = stated.Count > 0 ? PreferenceStatus.Rejected : PreferenceStatus.Active,
            ReviewAtUtc = Iso(now + PreferenceReview),

            // Rule 2, carried on the record itself: an inference never acts unasked.
            ConsentRequired = true,
        };

        await InsertAsync(preference, ct).ConfigureAwait(false);
        return preference;
    }

    public async Task<PreferenceResolution> ResolveAsync(
        string ownerRef, string dimension, string effect, CancellationToken ct)
    {
        if (!PreferenceEffect.IsKnown(effect))
        {
            throw new RelationshipException($"Unknown effect '{effect}'.");
        }

        IReadOnlyList<Preference> applicable = await ReadPreferencesAsync("""
            SELECT id, owner_ref, subject_ref, dimension, value_json, strength, basis,
                   evidence_refs, scope_json, status, review_at_utc, consent_required
              FROM preference
             WHERE owner_ref = @owner AND dimension = @dimension AND status = @active
             ORDER BY CASE basis WHEN 'EXPLICIT' THEN 0 ELSE 1 END, strength DESC;
            """, ct,
            ("@owner", ownerRef), ("@dimension", dimension), ("@active", PreferenceStatus.Active))
            .ConfigureAwait(false);

        if (applicable.Count == 0)
        {
            return new PreferenceResolution([], false, "nothing is on record for this");
        }

        // Rule 2. A purchase, a message, sensitive data or a persistent change is not something a
        // habit gets to trigger; presentational choices are, because getting those wrong costs a
        // sentence rather than an outcome.
        if (!PreferenceEffect.NeedsConfirmation(effect))
        {
            return new PreferenceResolution(applicable, true, "presentational; nothing leaves Aurora");
        }

        var inferred = applicable.Where(p => p.Basis != PreferenceBasis.Explicit).ToList();

        return inferred.Count > 0
            ? new PreferenceResolution(
                applicable, false,
                $"{inferred.Count} of these were worked out rather than stated; {effect} needs confirmation")
            : new PreferenceResolution(
                applicable, true, "stated explicitly by the owner");
    }

    public async Task<int> ReviewDueAsync(CancellationToken ct)
    {
        var expired = await ExecuteAsync("""
            UPDATE preference SET status = @expired
             WHERE status IN (@active, @candidate) AND basis <> @explicit AND review_at_utc <= @now;
            """, ct,
            ("@expired", PreferenceStatus.Expired), ("@active", PreferenceStatus.Active),
            ("@candidate", PreferenceStatus.Candidate), ("@explicit", PreferenceBasis.Explicit),
            ("@now", Iso(_clock.UtcNow))).ConfigureAwait(false);

        // A third party's relationship past its retention stops being in force. The row stays,
        // because rule 4 keeps the history and rule 3 only bounds how long it is acted on.
        var ended = await ExecuteAsync("""
            UPDATE relationship_assertion
               SET status = @ended, valid_to_utc = COALESCE(valid_to_utc, @now)
             WHERE status = @active AND retention_until_utc IS NOT NULL
               AND retention_until_utc <= @now;
            """, ct,
            ("@ended", RelationshipStatus.Ended), ("@active", RelationshipStatus.Active),
            ("@now", Iso(_clock.UtcNow))).ConfigureAwait(false);

        return expired + ended;
    }

    public async Task<RelationshipAssertion?> GetAsync(string relationshipId, CancellationToken ct)
    {
        IReadOnlyList<RelationshipAssertion> found = await ReadAsync(
            $"{Select} WHERE id = @id;", ct, ("@id", relationshipId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    // ---- plumbing ----

    /// <summary>
    /// Refuses a reference to an entity that has not been resolved.
    /// </summary>
    /// <remarks>
    /// RFC 029's first limit case: two people with the same name. Until resolution has settled
    /// which one is meant, asserting a tie attaches a fact to a guess — and a wrong relationship is
    /// worse than a missing one, because it looks like knowledge.
    /// </remarks>
    private async Task RequireResolvedAsync(string entityRef, CancellationToken ct)
    {
        if (!entityRef.StartsWith("entity/", StringComparison.Ordinal))
        {
            // Not a graph entity — a project, an account, a resource. Nothing to resolve.
            return;
        }

        var entityId = entityRef["entity/".Length..];
        KnowledgeEntity? entity = await _graph.GetEntityAsync(entityId, ct).ConfigureAwait(false);

        if (entity is null)
        {
            throw new RelationshipException(
                $"'{entityRef}' has not been resolved to an entity; resolve it before asserting a tie.");
        }

        if (entity.MergedIntoId is not null)
        {
            throw new RelationshipException(
                $"'{entityRef}' was merged into another entity; assert against the survivor.");
        }
    }

    private Task InsertAsync(Preference preference, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO preference
                (id, owner_ref, subject_ref, dimension, value_json, strength, basis,
                 evidence_refs, scope_json, status, review_at_utc, consent_required)
            VALUES (@id, @owner, @subject, @dimension, @value, @strength, @basis, @evidence,
                    @scope, @status, @review, @consent);
            """, ct,
            ("@id", preference.Id), ("@owner", preference.OwnerRef),
            ("@subject", preference.SubjectRef), ("@dimension", preference.Dimension),
            ("@value", preference.ValueJson), ("@strength", preference.Strength),
            ("@basis", preference.Basis),
            ("@evidence", string.Join('\n', preference.EvidenceRefs)),
            ("@scope", preference.ScopeJson), ("@status", preference.Status),
            ("@review", preference.ReviewAtUtc), ("@consent", preference.ConsentRequired ? 1 : 0));

    private async Task<RelationshipAssertion> RequireAsync(string relationshipId, CancellationToken ct) =>
        await GetAsync(relationshipId, ct).ConfigureAwait(false)
        ?? throw new RelationshipException("Unknown relationship.");

    private const string Select = """
        SELECT id, subject_ref, relation_type, object_ref, qualifiers_json, authority_scope,
               confidence, evidence_refs, valid_from_utc, valid_to_utc, status,
               authorization_ref, retention_until_utc
          FROM relationship_assertion
        """;

    private async Task<IReadOnlyList<RelationshipAssertion>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var found = new List<RelationshipAssertion>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found.Add(new RelationshipAssertion(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetDouble(6),
                Lines(reader.GetString(7)), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return found;
    }

    private async Task<IReadOnlyList<Preference>> ReadPreferencesAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var found = new List<Preference>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found.Add(new Preference(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDouble(5), reader.GetString(6),
                Lines(reader.GetString(7)), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetInt32(11) == 1));
        }

        return found;
    }

    private async Task<int> ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
