using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Knowledge;

/// <summary>Typed, temporal, explainable graph over active memories (RFC 04).</summary>
public sealed class SqliteKnowledgeGraph : IKnowledgeGraph
{
    /// <summary>RFC 04 rule 2 caps expansion at three hops.</summary>
    public const int MaxDepth = 3;

    private readonly SqliteConnectionFactory _factory;
    private readonly IMemoryService _memories;
    private readonly IClock _clock;

    public SqliteKnowledgeGraph(SqliteConnectionFactory factory, IMemoryService memories, IClock clock)
    {
        _factory = factory;
        _memories = memories;
        _clock = clock;
    }

    public async Task<PredicateSchema> RegisterPredicateAsync(PredicateSchema schema, CancellationToken ct)
    {
        await ExecuteAsync("""
            INSERT INTO predicate_schema
                (key, display_name, allowed_subject_types, allowed_object_types, cardinality,
                 inverse_key, sensitivity_rule, acyclic)
            VALUES (@k, @d, @st, @ot, @card, @inv, @rule, @acyclic)
            ON CONFLICT(key) DO UPDATE SET
                display_name = excluded.display_name,
                allowed_subject_types = excluded.allowed_subject_types,
                allowed_object_types = excluded.allowed_object_types,
                cardinality = excluded.cardinality,
                inverse_key = excluded.inverse_key,
                sensitivity_rule = excluded.sensitivity_rule,
                acyclic = excluded.acyclic;
            """, ct,
            ("@k", schema.Key), ("@d", schema.DisplayName),
            ("@st", string.Join(',', schema.AllowedSubjectTypes)),
            ("@ot", string.Join(',', schema.AllowedObjectTypes)),
            ("@card", schema.Cardinality), ("@inv", (object?)schema.InverseKey ?? DBNull.Value),
            ("@rule", (object?)schema.SensitivityRule ?? DBNull.Value),
            ("@acyclic", schema.Acyclic ? 1 : 0)).ConfigureAwait(false);

        return schema;
    }

    public async Task<KnowledgeEntity> UpsertEntityAsync(KnowledgeEntity entity, CancellationToken ct)
    {
        await ExecuteAsync("""
            INSERT INTO knowledge_entity
                (id, type, canonical_name, aliases, attributes_json, status, sensitivity,
                 source_refs, merged_into_id)
            VALUES (@id, @type, @name, @aliases, @attrs, @status, @sens, @sources, @merged)
            ON CONFLICT(id) DO UPDATE SET
                canonical_name = excluded.canonical_name,
                aliases = excluded.aliases,
                attributes_json = excluded.attributes_json,
                status = excluded.status,
                sensitivity = excluded.sensitivity,
                source_refs = excluded.source_refs,
                merged_into_id = excluded.merged_into_id;
            """, ct,
            ("@id", entity.Id), ("@type", entity.Type), ("@name", entity.CanonicalName),
            ("@aliases", string.Join(',', entity.Aliases)), ("@attrs", entity.AttributesJson),
            ("@status", entity.Status), ("@sens", entity.SensitivityClass),
            ("@sources", string.Join(',', entity.SourceRefs)),
            ("@merged", (object?)entity.MergedIntoId ?? DBNull.Value)).ConfigureAwait(false);

        return (await GetEntityAsync(entity.Id, ct).ConfigureAwait(false))!;
    }

    public async Task<GraphChangeSet> ProposeAsync(string memoryId, CancellationToken ct)
    {
        MemoryRecord? memory = await _memories.GetAsync(memoryId, ct).ConfigureAwait(false);
        if (memory is null)
        {
            return new GraphChangeSet([], [], ["Unknown memory."], []);
        }

        PredicateSchema? schema = await GetPredicateAsync(memory.Predicate, ct).ConfigureAwait(false);
        if (schema is null)
        {
            // Rule 1: an unregistered predicate is a phrase, not a fact. It does not enter the
            // canonical graph, and saying so is more useful than inventing a type for it.
            return new GraphChangeSet(
                [], [], [$"Predicate '{memory.Predicate}' is not in the schema; free relations are not allowed."], []);
        }

        var ambiguous = new List<string>();
        (KnowledgeEntity subject, var subjectAmbiguous) =
            await ResolveOrCreateAsync(memory.SubjectRef, schema.AllowedSubjectTypes, memory, ct)
                .ConfigureAwait(false);
        if (subjectAmbiguous)
        {
            ambiguous.Add(memory.SubjectRef);
        }

        if (!schema.AllowedSubjectTypes.Contains(subject.Type, StringComparer.Ordinal))
        {
            return new GraphChangeSet(
                [], [], [$"'{schema.Key}' does not accept a subject of type '{subject.Type}'."], ambiguous);
        }

        // A relation whose memory is not an active, sourced fact stays PROPOSED, and RFC 04 says a
        // PROPOSED relation is never the sole basis of an action.
        var status = memory.Status == MemoryStatus.Active && memory.SourceRefs.Count > 0
            ? RelationStatus.Asserted
            : RelationStatus.Proposed;

        var relation = new KnowledgeRelation(
            Guid.NewGuid().ToString("N"), subject.Id, schema.Key, ObjectId: null,
            LiteralJson: memory.ObjectJson, QualifierJson: null, memory.Confidence,
            SourceMemoryIds: [memory.Id], status, memory.ValidFromUtc, memory.ValidToUtc,
            AssertedAtUtc: Iso(_clock.UtcNow));

        if (schema.Acyclic && relation.ObjectId is not null)
        {
            IReadOnlyList<string> cycle =
                await FindCycleAsync(schema.Key, subject.Id, relation.ObjectId, ct).ConfigureAwait(false);
            if (cycle.Count > 0)
            {
                throw new KnowledgeGraphException(
                    $"'{schema.Key}' would create a cycle: {string.Join(" → ", cycle)}.", cycle);
            }
        }

        // Rule 3: a new assertion closes the previous one rather than deleting it, so "was" and
        // "is" both remain answerable.
        if (schema.Cardinality == Cardinality.One && status == RelationStatus.Asserted)
        {
            await CloseOpenRelationsAsync(subject.Id, schema.Key, relation.AssertedAtUtc, ct)
                .ConfigureAwait(false);
        }

        await InsertRelationAsync(relation, ct).ConfigureAwait(false);

        return new GraphChangeSet([subject], [relation], [], ambiguous);
    }

    public async Task<KnowledgeRelation> AssertRelationAsync(
        string subjectId, string predicate, string objectId,
        IReadOnlyList<string> sourceMemoryIds, CancellationToken ct)
    {
        PredicateSchema schema = await GetPredicateAsync(predicate, ct).ConfigureAwait(false)
            ?? throw new KnowledgeGraphException(
                $"Predicate '{predicate}' is not in the schema; free relations are not allowed.");

        KnowledgeEntity subject = await GetEntityAsync(subjectId, ct).ConfigureAwait(false)
            ?? throw new KnowledgeGraphException("Unknown subject entity.");
        KnowledgeEntity target = await GetEntityAsync(objectId, ct).ConfigureAwait(false)
            ?? throw new KnowledgeGraphException("Unknown object entity.");

        if (!schema.AllowedSubjectTypes.Contains(subject.Type, StringComparer.Ordinal))
        {
            throw new KnowledgeGraphException(
                $"'{predicate}' does not accept a subject of type '{subject.Type}'.");
        }

        if (!schema.AllowedObjectTypes.Contains(target.Type, StringComparer.Ordinal))
        {
            throw new KnowledgeGraphException(
                $"'{predicate}' does not accept an object of type '{target.Type}'.");
        }

        if (schema.Acyclic)
        {
            IReadOnlyList<string> cycle =
                await FindCycleAsync(predicate, subjectId, objectId, ct).ConfigureAwait(false);
            if (cycle.Count > 0)
            {
                // The chain travels with the refusal: "there is a cycle" is not actionable,
                // "t1 → t2 → t3 → t1" is.
                throw new KnowledgeGraphException(
                    $"'{predicate}' would create a cycle: {string.Join(" -> ", cycle)}.", cycle);
            }
        }

        var status = sourceMemoryIds.Count > 0 ? RelationStatus.Asserted : RelationStatus.Proposed;
        var now = Iso(_clock.UtcNow);

        if (schema.Cardinality == Cardinality.One && status == RelationStatus.Asserted)
        {
            await CloseOpenRelationsAsync(subjectId, predicate, now, ct).ConfigureAwait(false);
        }

        var relation = new KnowledgeRelation(
            Guid.NewGuid().ToString("N"), subjectId, predicate, objectId, LiteralJson: null,
            QualifierJson: null, Confidence: 1.0, sourceMemoryIds, status,
            ValidFromUtc: now, ValidToUtc: null, AssertedAtUtc: now);

        await InsertRelationAsync(relation, ct).ConfigureAwait(false);
        return relation;
    }

    public async Task<Subgraph> QueryAsync(
        GraphPattern pattern, int depth, MemoryAccessContext access, CancellationToken ct)
    {
        var clamped = Math.Clamp(depth, 0, MaxDepth);
        var ceiling = Sensitivity.Rank(access.MaxSensitivity);

        try
        {
            var seeds = await SeedsAsync(pattern, ceiling, ct).ConfigureAwait(false);
            var entities = seeds.ToDictionary(e => e.Id, StringComparer.Ordinal);
            var relations = new List<KnowledgeRelation>();
            var frontier = seeds.Select(e => e.Id).ToList();
            var reached = 0;

            for (var hop = 0; hop < clamped && frontier.Count > 0; hop++)
            {
                var next = new List<string>();
                foreach (KnowledgeRelation relation in
                         await OutgoingAsync(frontier, pattern, ct).ConfigureAwait(false))
                {
                    relations.Add(relation);

                    if (relation.ObjectId is null || entities.ContainsKey(relation.ObjectId))
                    {
                        continue;
                    }

                    KnowledgeEntity? neighbour =
                        await GetEntityAsync(relation.ObjectId, ct).ConfigureAwait(false);
                    if (neighbour is not null && Sensitivity.Rank(neighbour.SensitivityClass) <= ceiling)
                    {
                        entities[neighbour.Id] = neighbour;
                        next.Add(neighbour.Id);
                    }
                }

                reached = hop + 1;
                frontier = next;
            }

            return new Subgraph(entities.Values.ToList(), relations, reached);
        }
        catch (SqliteException ex)
        {
            // Rule from the limit cases: fall back and say relational research is degraded rather
            // than answering as though the graph had nothing to say.
            return new Subgraph([], [], 0, Degraded: true,
                Degradation: $"Graph unavailable ({ex.SqliteErrorCode}); use structured memory.");
        }
    }

    public async Task<MergeRecord> MergeAsync(
        string survivorId, string mergedId, string actor, CancellationToken ct)
    {
        KnowledgeEntity survivor = await GetEntityAsync(survivorId, ct).ConfigureAwait(false)
            ?? throw new KnowledgeGraphException("Unknown survivor entity.");
        KnowledgeEntity merged = await GetEntityAsync(mergedId, ct).ConfigureAwait(false)
            ?? throw new KnowledgeGraphException("Unknown merged entity.");

        if (survivor.Id == merged.Id)
        {
            throw new KnowledgeGraphException("An entity cannot be merged into itself.");
        }

        var record = new MergeRecord(
            Guid.NewGuid().ToString("N"), survivorId, mergedId, actor, Iso(_clock.UtcNow), Reversed: false);

        // Rule 4: the merged entity is redirected, never deleted. That redirection is the whole
        // reversibility mechanism — an entity that was destroyed cannot be un-merged.
        await UpsertEntityAsync(
            merged with { Status = EntityStatus.Merged, MergedIntoId = survivorId }, ct).ConfigureAwait(false);

        await ExecuteAsync(
            "INSERT INTO entity_merge (id, survivor_id, merged_id, actor, at_utc, reversed) "
            + "VALUES (@id, @s, @m, @a, @at, 0);", ct,
            ("@id", record.Id), ("@s", survivorId), ("@m", mergedId),
            ("@a", actor), ("@at", record.AtUtc)).ConfigureAwait(false);

        return record;
    }

    public async Task<MergeRecord> UnmergeAsync(string mergeRecordId, string actor, CancellationToken ct)
    {
        MergeRecord? record = await GetMergeAsync(mergeRecordId, ct).ConfigureAwait(false)
            ?? throw new KnowledgeGraphException("Unknown merge record.");

        if (record.Reversed)
        {
            throw new KnowledgeGraphException("That merge has already been reversed.");
        }

        KnowledgeEntity merged = await GetEntityAsync(record.MergedId, ct).ConfigureAwait(false)
            ?? throw new KnowledgeGraphException("The merged entity is gone.");

        await UpsertEntityAsync(
            merged with { Status = EntityStatus.Active, MergedIntoId = null }, ct).ConfigureAwait(false);

        await ExecuteAsync("UPDATE entity_merge SET reversed = 1 WHERE id = @id;", ct, ("@id", mergeRecordId))
            .ConfigureAwait(false);

        return record with { Reversed = true };
    }

    public async Task<IReadOnlyList<RelationProvenance>> ExplainAsync(string relationId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, source_memory_ids, status, asserted_at_utc FROM knowledge_relation WHERE id = @id;";
        command.Parameters.AddWithValue("@id", relationId);

        var rows = new List<RelationProvenance>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new RelationProvenance(
                reader.GetString(0),
                reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries),
                reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    public async Task<int> OnSourceWithdrawnAsync(string memoryId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // The edge is preserved for audit and loses ASSERTED. A fact whose evidence was withdrawn
        // is no longer a fact, but pretending it never existed would erase the reasoning trail.
        command.CommandText = """
            UPDATE knowledge_relation
               SET status = @proposed
             WHERE status = @asserted AND source_memory_ids = @mid;
            """;
        command.Parameters.AddWithValue("@proposed", RelationStatus.Proposed);
        command.Parameters.AddWithValue("@asserted", RelationStatus.Asserted);
        command.Parameters.AddWithValue("@mid", memoryId);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<KnowledgeEntity?> GetEntityAsync(string entityId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = EntitySelect + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", entityId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadEntity(reader) : null;
    }

    // ---- internals ----

    private async Task<(KnowledgeEntity Entity, bool Ambiguous)> ResolveOrCreateAsync(
        string name, IReadOnlyList<string> allowedTypes, MemoryRecord memory, CancellationToken ct)
    {
        var type = allowedTypes.Count > 0 ? allowedTypes[0] : "Thing";

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = EntitySelect + " WHERE type = @type AND canonical_name = @name AND status = @active;";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@active", EntityStatus.Active);

        var matches = new List<KnowledgeEntity>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                matches.Add(ReadEntity(reader));
            }
        }

        if (matches.Count == 1)
        {
            return (matches[0], false);
        }

        // Homonyms: with more than one candidate the graph does not pick. A separate entity is
        // created and the ambiguity reported, until there is evidence enough to merge.
        var created = new KnowledgeEntity(
            Guid.NewGuid().ToString("N"), type, name, [], "{}", EntityStatus.Active,
            memory.SensitivityClass, memory.SourceRefs);

        return (await UpsertEntityAsync(created, ct).ConfigureAwait(false), matches.Count > 1);
    }

    private async Task<IReadOnlyList<KnowledgeEntity>> SeedsAsync(
        GraphPattern pattern, int ceiling, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Rule 5: SECRET data produces no node reachable by name. Searching by name excludes it;
        // it is still reachable by an id someone already holds.
        command.CommandText = pattern.StartEntityId is not null
            ? EntitySelect + " WHERE id = @id;"
            : EntitySelect + " WHERE status = @active AND sensitivity <> @secret"
                + " AND (@type IS NULL OR type = @type)"
                + " AND (@name IS NULL OR canonical_name = @name);";

        if (pattern.StartEntityId is not null)
        {
            command.Parameters.AddWithValue("@id", pattern.StartEntityId);
        }
        else
        {
            command.Parameters.AddWithValue("@active", EntityStatus.Active);
            command.Parameters.AddWithValue("@secret", Sensitivity.Secret);
            command.Parameters.AddWithValue("@type", (object?)pattern.EntityType ?? DBNull.Value);
            command.Parameters.AddWithValue("@name", (object?)pattern.SearchName ?? DBNull.Value);
        }

        var rows = new List<KnowledgeEntity>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            KnowledgeEntity entity = ReadEntity(reader);
            if (Sensitivity.Rank(entity.SensitivityClass) <= ceiling)
            {
                rows.Add(entity);
            }
        }

        return rows;
    }

    private async Task<IReadOnlyList<KnowledgeRelation>> OutgoingAsync(
        IReadOnlyList<string> subjectIds, GraphPattern pattern, CancellationToken ct)
    {
        if (subjectIds.Count == 0)
        {
            return [];
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var placeholders = string.Join(',', subjectIds.Select((_, i) => "@s" + i));
        var predicateClause = pattern.Predicates is { Count: > 0 }
            ? " AND predicate IN (" + string.Join(',', pattern.Predicates.Select((_, i) => "@p" + i)) + ")"
            : string.Empty;

        // "As of now" hides relations already closed, without deleting them (rule 3).
        var temporalClause = pattern.AsOfNowOnly ? " AND (valid_to_utc IS NULL OR valid_to_utc > @now)" : string.Empty;

        command.CommandText = RelationSelect
            + $" WHERE subject_id IN ({placeholders}) AND status <> @retracted{predicateClause}{temporalClause};";

        for (var i = 0; i < subjectIds.Count; i++)
        {
            command.Parameters.AddWithValue("@s" + i, subjectIds[i]);
        }

        if (pattern.Predicates is { Count: > 0 })
        {
            for (var i = 0; i < pattern.Predicates.Count; i++)
            {
                command.Parameters.AddWithValue("@p" + i, pattern.Predicates[i]);
            }
        }

        command.Parameters.AddWithValue("@retracted", RelationStatus.Retracted);
        if (pattern.AsOfNowOnly)
        {
            command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));
        }

        var rows = new List<KnowledgeRelation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadRelation(reader));
        }

        return rows;
    }

    /// <summary>Walks the predicate backwards from the proposed object to see if it reaches the subject.</summary>
    private async Task<IReadOnlyList<string>> FindCycleAsync(
        string predicate, string subjectId, string objectId, CancellationToken ct)
    {
        var path = new List<string> { subjectId, objectId };
        var seen = new HashSet<string>(StringComparer.Ordinal) { subjectId, objectId };
        var current = objectId;

        for (var hop = 0; hop < 64; hop++)
        {
            IReadOnlyList<KnowledgeRelation> next = await OutgoingAsync(
                [current], new GraphPattern(Predicates: [predicate]), ct).ConfigureAwait(false);

            var target = next.FirstOrDefault(r => r.ObjectId is not null)?.ObjectId;
            if (target is null)
            {
                return [];
            }

            path.Add(target);
            if (target == subjectId)
            {
                return path;
            }

            if (!seen.Add(target))
            {
                return [];
            }

            current = target;
        }

        return [];
    }

    private Task CloseOpenRelationsAsync(
        string subjectId, string predicate, string atUtc, CancellationToken ct) =>
        ExecuteAsync("""
            UPDATE knowledge_relation
               SET valid_to_utc = @at
             WHERE subject_id = @s AND predicate = @p AND status = @asserted AND valid_to_utc IS NULL;
            """, ct,
            ("@at", atUtc), ("@s", subjectId), ("@p", predicate), ("@asserted", RelationStatus.Asserted));

    private Task InsertRelationAsync(KnowledgeRelation r, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO knowledge_relation
                (id, subject_id, predicate, object_id, literal_json, qualifier_json, confidence,
                 source_memory_ids, status, valid_from_utc, valid_to_utc, asserted_at_utc)
            VALUES (@id, @s, @p, @o, @lit, @q, @c, @src, @status, @from, @to, @at);
            """, ct,
            ("@id", r.Id), ("@s", r.SubjectId), ("@p", r.Predicate),
            ("@o", (object?)r.ObjectId ?? DBNull.Value), ("@lit", (object?)r.LiteralJson ?? DBNull.Value),
            ("@q", (object?)r.QualifierJson ?? DBNull.Value), ("@c", r.Confidence),
            ("@src", string.Join(',', r.SourceMemoryIds)), ("@status", r.Status),
            ("@from", (object?)r.ValidFromUtc ?? DBNull.Value),
            ("@to", (object?)r.ValidToUtc ?? DBNull.Value), ("@at", r.AssertedAtUtc));

    private async Task<PredicateSchema?> GetPredicateAsync(string key, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM predicate_schema WHERE key = @k;";
        command.Parameters.AddWithValue("@k", key);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new PredicateSchema(
            reader.GetString(0), reader.GetString(1),
            reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7) == 1);
    }

    private async Task<MergeRecord?> GetMergeAsync(string id, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, survivor_id, merged_id, actor, at_utc, reversed FROM entity_merge WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new MergeRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5) == 1)
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

    private const string EntitySelect = """
        SELECT id, type, canonical_name, aliases, attributes_json, status, sensitivity,
               source_refs, merged_into_id
          FROM knowledge_entity
        """;

    private const string RelationSelect = """
        SELECT id, subject_id, predicate, object_id, literal_json, qualifier_json, confidence,
               source_memory_ids, status, valid_from_utc, valid_to_utc, asserted_at_utc
          FROM knowledge_relation
        """;

    private static KnowledgeEntity ReadEntity(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries),
        r.GetString(4), r.GetString(5), r.GetString(6),
        r.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries),
        r.IsDBNull(8) ? null : r.GetString(8));

    private static KnowledgeRelation ReadRelation(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetDouble(6),
        r.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries),
        r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11));

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
