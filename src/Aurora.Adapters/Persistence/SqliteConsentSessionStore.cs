using System.Collections.Concurrent;
using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Persistence;

/// <summary>Limits applied to every consent session (docs/adr/0010).</summary>
public sealed record ConsentSessionOptions(TimeSpan Lifetime, int MaxActions)
{
    public static readonly ConsentSessionOptions Default = new(TimeSpan.FromMinutes(15), 50);
}

/// <summary>
/// SQLite-backed session store. Check-and-spend is serialised per database so two concurrent
/// requests cannot both consume the last unit of a session's budget (docs/adr/0010).
/// </summary>
/// <remarks>
/// Liveness is expressed entirely as a WHERE clause — matching boot id, matching policy version,
/// ACTIVE, unexpired, budget remaining. There is no sweeper and no background job: a session that
/// stops matching is dead by construction, which means a restart, a policy change or a clock
/// moving past the expiry can never leave a stale grant usable.
/// </remarks>
public sealed class SqliteConsentSessionStore : IConsentSessionStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly IServerIdentity _server;
    private readonly IPolicyEngine _policy;
    private readonly ConsentSessionOptions _options;
    private readonly SemaphoreSlim _gate;

    public SqliteConsentSessionStore(
        SqliteConnectionFactory factory,
        IClock clock,
        IServerIdentity server,
        IPolicyEngine policy,
        ConsentSessionOptions options)
    {
        _factory = factory;
        _clock = clock;
        _server = server;
        _policy = policy;
        _options = options;
        _gate = Gates.GetOrAdd("session:" + factory.DbPath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<ConsentSession> OpenAsync(Principal principal, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

            // Reuse rather than duplicate: two approvals granted close together should extend the
            // same grant, not stack two budgets for the same principal.
            await using (var existing = connection.CreateCommand())
            {
                existing.CommandText = LiveSelect + " LIMIT 1;";
                Bind(existing, principal, now);
                await using var reader = await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return Read(reader);
                }
            }

            var session = new ConsentSession(
                Guid.NewGuid().ToString("N"),
                principal.ClientId,
                principal.WindowsUser,
                _server.BootId,
                _policy.Version,
                ConsentSessionStatus.Active,
                0,
                _options.MaxActions,
                Iso(now),
                Iso(now.Add(_options.Lifetime)));

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO consent_session
                        (session_id, principal_client_id, principal_windows_user, server_boot_id,
                         policy_version, status, actions_used, max_actions, created_at_utc, expires_at_utc)
                    VALUES (@id, @cid, @wu, @boot, @pv, @st, 0, @max, @created, @expires);
                    """;
                insert.Parameters.AddWithValue("@id", session.SessionId);
                insert.Parameters.AddWithValue("@cid", session.PrincipalClientId);
                insert.Parameters.AddWithValue("@wu", session.PrincipalWindowsUser);
                insert.Parameters.AddWithValue("@boot", session.ServerBootId);
                insert.Parameters.AddWithValue("@pv", session.PolicyVersion);
                insert.Parameters.AddWithValue("@st", session.Status);
                insert.Parameters.AddWithValue("@max", session.MaxActions);
                insert.Parameters.AddWithValue("@created", session.CreatedAtUtc);
                insert.Parameters.AddWithValue("@expires", session.ExpiresAtUtc);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConsentSessionUse> TryUseAsync(Principal principal, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

            // Compare-and-set on actions_used: the same predicate that selected the session also
            // guards the update, so the budget cannot be overspent under concurrency.
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE consent_session
                   SET actions_used = actions_used + 1
                 WHERE session_id = (
                       SELECT session_id FROM consent_session
                        WHERE principal_client_id = @cid
                          AND server_boot_id = @boot
                          AND policy_version = @pv
                          AND status = @active
                          AND expires_at_utc > @now
                          AND actions_used < max_actions
                        ORDER BY created_at_utc ASC
                        LIMIT 1)
                RETURNING session_id;
                """;
            Bind(command, principal, now);

            var sessionId = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;

            return sessionId is null
                ? new ConsentSessionUse(ConsentSessionUseOutcome.None)
                : new ConsentSessionUse(ConsentSessionUseOutcome.Used, sessionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RevokeAllAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Deliberately not scoped to this boot: the kill switch must also bury grants left by an
        // earlier run, so that an operator pressing it does not have to reason about restarts.
        command.CommandText = "UPDATE consent_session SET status = @revoked WHERE status = @active;";
        command.Parameters.AddWithValue("@revoked", ConsentSessionStatus.Revoked);
        command.Parameters.AddWithValue("@active", ConsentSessionStatus.Active);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountActiveAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM consent_session
             WHERE server_boot_id = @boot AND policy_version = @pv AND status = @active
               AND expires_at_utc > @now AND actions_used < max_actions;
            """;
        command.Parameters.AddWithValue("@boot", _server.BootId);
        command.Parameters.AddWithValue("@pv", _policy.Version);
        command.Parameters.AddWithValue("@active", ConsentSessionStatus.Active);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private const string LiveSelect = """
        SELECT session_id, principal_client_id, principal_windows_user, server_boot_id,
               policy_version, status, actions_used, max_actions, created_at_utc, expires_at_utc
          FROM consent_session
         WHERE principal_client_id = @cid
           AND server_boot_id = @boot
           AND policy_version = @pv
           AND status = @active
           AND expires_at_utc > @now
           AND actions_used < max_actions
         ORDER BY created_at_utc ASC
        """;

    private void Bind(Microsoft.Data.Sqlite.SqliteCommand command, Principal principal, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("@cid", principal.ClientId);
        command.Parameters.AddWithValue("@boot", _server.BootId);
        command.Parameters.AddWithValue("@pv", _policy.Version);
        command.Parameters.AddWithValue("@active", ConsentSessionStatus.Active);
        command.Parameters.AddWithValue("@now", Iso(now));
    }

    private static ConsentSession Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
        reader.GetString(8), reader.GetString(9));

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
