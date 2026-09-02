using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Adapters.Persistence;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Presence;

/// <summary>
/// Voice sessions, kept where the rest of Aurora's state is kept (docs/adr/0073).
/// </summary>
/// <remarks>
/// One table for every channel. A phone table and a Discord table would agree about nothing, and
/// the three things that have to see all of them at once — the operator's stop, the concurrency
/// limit and the audit's correlation — would each have to know about both.
/// <para>
/// The provider is never the source of truth. A call exists because Aurora recorded a session; the
/// provider's own identifier is stored beside it so the two can be reconciled, which is a different
/// thing from letting the provider decide what happened.
/// </para>
/// </remarks>
public sealed class SqliteVoiceSessionStore : IVoiceSessionStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate;

    public SqliteVoiceSessionStore(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
        _gate = Gates.GetOrAdd("voice:" + factory.DbPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<VoiceSession> OpenAsync(VoiceSession session, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO voice_session
                (session_id, channel, provider, direction, participant_json, grant_json,
                 state, started_at_utc, correlation_id, external_ref, ended_at_utc,
                 ended_reason, tool_calls_used, intent_json)
            VALUES (@id, @channel, @provider, @direction, @participant, @grant,
                    @state, @started, @correlation, @external, NULL, NULL, 0, @intent);
            """;

        Bind(command, session);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return session;
    }

    public async Task<VoiceSession?> FindAsync(string sessionId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = Select + " WHERE session_id = @id;";
        command.Parameters.AddWithValue("@id", sessionId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<VoiceSession?> FindByExternalAsync(
        string provider, string externalRef, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // What stops a provider's duplicate webhook becoming a second session: the same call
        // resolves to the same row, whichever delivery arrives first.
        command.CommandText = Select + " WHERE provider = @provider AND external_ref = @external;";
        command.Parameters.AddWithValue("@provider", provider);
        command.Parameters.AddWithValue("@external", externalRef);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<VoiceSession> AdvanceAsync(
        string sessionId, VoiceSessionState state, string? reason, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        var terminal = state is VoiceSessionState.Ended or VoiceSessionState.Failed
            or VoiceSessionState.Cancelled;

        command.CommandText = """
            UPDATE voice_session
               SET state = @state,
                   ended_reason = COALESCE(@reason, ended_reason),
                   ended_at_utc = CASE WHEN @terminal = 1 THEN @now ELSE ended_at_utc END
             WHERE session_id = @id;
            """;

        command.Parameters.AddWithValue("@state", state.ToString());
        command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("@terminal", terminal ? 1 : 0);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));
        command.Parameters.AddWithValue("@id", sessionId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return await FindAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"voice session '{sessionId}' is not there");
    }

    public async Task<VoiceBudgetUse> SpendToolCallAsync(string sessionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await using SqliteConnection connection =
                await _factory.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();

            // Compare-and-set, like the consent sessions do it. Two tool requests arriving together
            // must not both consume the last unit of a budget, and the predicate that selects the
            // row is the one that guards the update.
            command.CommandText = """
                UPDATE voice_session
                   SET tool_calls_used = tool_calls_used + 1
                 WHERE session_id = @id
                   AND tool_calls_used < json_extract(grant_json, '$.MaxToolCalls')
                RETURNING tool_calls_used,
                          json_extract(grant_json, '$.MaxToolCalls');
                """;

            command.Parameters.AddWithValue("@id", sessionId);

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new VoiceBudgetUse(true, reader.GetInt32(0), reader.GetInt32(1));
            }

            VoiceSession? session = await FindAsync(sessionId, ct).ConfigureAwait(false);

            return new VoiceBudgetUse(
                false, session?.ToolCallsUsed ?? 0, session?.Grant.MaxToolCalls ?? 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VoiceSession>> LiveAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = Select + " WHERE state IN ('Pending', 'Connecting', 'Active');";

        var live = new List<VoiceSession>();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            live.Add(Read(reader));
        }

        return live;
    }

    public async Task<int> EndAllAsync(string reason, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // The operator's stop. Marks every live session cancelled in one statement, so that a
        // session started between two of them cannot slip through a loop.
        command.CommandText = """
            UPDATE voice_session
               SET state = 'Cancelled', ended_reason = @reason, ended_at_utc = @now
             WHERE state IN ('Pending', 'Connecting', 'Active');
            """;

        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string Select = """
        SELECT session_id, channel, provider, direction, participant_json, grant_json,
               state, started_at_utc, correlation_id, external_ref, ended_at_utc,
               ended_reason, tool_calls_used, intent_json
          FROM voice_session
        """;

    private static void Bind(SqliteCommand command, VoiceSession session)
    {
        command.Parameters.AddWithValue("@id", session.SessionId);
        command.Parameters.AddWithValue("@channel", session.Channel.ToString());
        command.Parameters.AddWithValue("@provider", session.Provider);
        command.Parameters.AddWithValue("@direction", session.Direction.ToString());
        command.Parameters.AddWithValue("@participant", JsonSerializer.Serialize(session.Participant));
        command.Parameters.AddWithValue("@grant", JsonSerializer.Serialize(session.Grant));
        command.Parameters.AddWithValue("@state", session.State.ToString());
        command.Parameters.AddWithValue("@started", session.StartedAtUtc);
        command.Parameters.AddWithValue("@correlation", session.CorrelationId);
        command.Parameters.AddWithValue("@external", (object?)session.ExternalRef ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@intent",
            session.Intent is null ? DBNull.Value : JsonSerializer.Serialize(session.Intent));
    }

    private static VoiceSession Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<VoiceChannel>(reader.GetString(1)),
        reader.GetString(2),
        Enum.Parse<VoiceCallDirection>(reader.GetString(3)),
        JsonSerializer.Deserialize<VoiceParticipant>(reader.GetString(4))!,
        JsonSerializer.Deserialize<VoiceGrant>(reader.GetString(5))!,
        Enum.Parse<VoiceSessionState>(reader.GetString(6)),
        reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetInt32(12),
        reader.IsDBNull(13)
            ? null
            : JsonSerializer.Deserialize<OutboundCallIntent>(reader.GetString(13)));

    private static string Iso(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
