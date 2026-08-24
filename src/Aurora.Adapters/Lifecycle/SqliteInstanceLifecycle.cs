using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Lifecycle;

/// <summary>
/// The RFC 039 state machine, owned by the Kernel and persisted so an instance's existence survives
/// the process that was running it.
/// </summary>
public sealed class SqliteInstanceLifecycle : IInstanceLifecycle
{
    /// <summary>
    /// Edges of the RFC 039 diagram, plus the returns its own interfaces require.
    /// </summary>
    /// <remarks>
    /// The diagram draws the forward path to shutdown. Three sets of edges are added because the
    /// rest of the RFC requires them and a machine without them would be one-way into termination:
    /// the working states return to <c>READY</c>; <c>PAUSED</c>, <c>BACKING_UP</c> and
    /// <c>UPDATING</c> return to <c>READY</c>, which is what <c>Lifecycle.resume</c> is for; and
    /// <c>RECOVERING</c> is reachable from any live state, because the RFC says abrupt failure is
    /// observed as <c>RECOVERING</c> and a crash can strike in any of them.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [InstanceState.Created] = [InstanceState.Bootstrapping],
            [InstanceState.Bootstrapping] = [InstanceState.Recovering],
            [InstanceState.Recovering] = [InstanceState.Ready],
            [InstanceState.Ready] =
                [InstanceState.Deliberating, InstanceState.Executing, InstanceState.Maintaining,
                 InstanceState.Waiting, InstanceState.ShuttingDown],
            [InstanceState.Deliberating] = [InstanceState.Waiting, InstanceState.Ready],
            [InstanceState.Executing] = [InstanceState.Waiting, InstanceState.Ready],
            [InstanceState.Maintaining] = [InstanceState.Waiting, InstanceState.Ready],
            [InstanceState.Waiting] =
                [InstanceState.Paused, InstanceState.BackingUp, InstanceState.Updating, InstanceState.Ready],
            [InstanceState.Paused] = [InstanceState.ShuttingDown, InstanceState.Ready],
            [InstanceState.BackingUp] = [InstanceState.ShuttingDown, InstanceState.Ready],
            [InstanceState.Updating] = [InstanceState.ShuttingDown, InstanceState.Ready],
            [InstanceState.ShuttingDown] = [InstanceState.Stopped],
            [InstanceState.Stopped] = [InstanceState.Retired],
            [InstanceState.Retired] = [],
        };

    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteInstanceLifecycle(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<InstanceLifecycle> GetOrCreateAsync(string instanceId, CancellationToken ct)
    {
        InstanceLifecycle? existing = await LoadAsync(instanceId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO instance_lifecycle
                (instance_id, state, entered_at_utc, reason, active_cycle_refs,
                 pending_action_refs, last_verified_snapshot_ref, version)
            VALUES (@id, @state, @now, @reason, '', '', NULL, 1);
            """;
        command.Parameters.AddWithValue("@id", instanceId);
        command.Parameters.AddWithValue("@state", InstanceState.Created);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));
        command.Parameters.AddWithValue("@reason", "created");
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return (await LoadAsync(instanceId, ct).ConfigureAwait(false))!;
    }

    public async Task<TransitionResult> TransitionAsync(
        string instanceId, string targetState, string actor, string reason,
        bool emergency = false, CancellationToken ct = default)
    {
        // Rule 4: only the Kernel performs transitions. The Mind proposes.
        if (!string.Equals(actor, TransitionActor.Kernel, StringComparison.Ordinal))
        {
            return new TransitionResult(false, null, TransitionRefusal.NotAuthorised);
        }

        InstanceLifecycle? current = await LoadAsync(instanceId, ct).ConfigureAwait(false);
        if (current is null)
        {
            return new TransitionResult(false, null, TransitionRefusal.NotFound);
        }

        if (!IsLegal(current.State, targetState))
        {
            return new TransitionResult(false, current, TransitionRefusal.IllegalTransition);
        }

        // Rule 1: a clean stop needs a verified snapshot; anything else needs an audited emergency.
        if (targetState == InstanceState.Stopped
            && string.IsNullOrEmpty(current.LastVerifiedSnapshotRef)
            && !emergency)
        {
            return new TransitionResult(false, current, TransitionRefusal.StopWithoutSnapshotOrEmergency);
        }

        // Rule 2: BACKING_UP and UPDATING drain idempotent work first. Incomplete external calls
        // may remain, but only as pending refs — that is what "marked for reconciliation" means.
        if (InstanceState.RequiresDrain(targetState) && current.ActiveCycleRefs.Count > 0)
        {
            return new TransitionResult(false, current, TransitionRefusal.DrainRequired);
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE instance_lifecycle
               SET state = @state, entered_at_utc = @now, reason = @reason, version = version + 1
             WHERE instance_id = @id AND version = @version;
            """;
        command.Parameters.AddWithValue("@state", targetState);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@id", instanceId);
        command.Parameters.AddWithValue("@version", current.Version);

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            // Someone else moved it first; report the state as it now stands rather than overwriting.
            return new TransitionResult(false, await LoadAsync(instanceId, ct).ConfigureAwait(false),
                TransitionRefusal.IllegalTransition);
        }

        return new TransitionResult(true, await LoadAsync(instanceId, ct).ConfigureAwait(false));
    }

    public async Task<LifecycleProposal> ProposeAsync(
        string instanceId, string targetState, string reason, CancellationToken ct)
    {
        var proposal = new LifecycleProposal(instanceId, targetState, reason, Iso(_clock.UtcNow));

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO lifecycle_proposal (proposal_id, instance_id, target_state, reason, proposed_at_utc)
            VALUES (@pid, @id, @target, @reason, @at);
            """;
        command.Parameters.AddWithValue("@pid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@id", instanceId);
        command.Parameters.AddWithValue("@target", targetState);
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@at", proposal.ProposedAtUtc);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return proposal;
    }

    public async Task<ShutdownPlan> PrepareShutdownAsync(string instanceId, CancellationToken ct)
    {
        InstanceLifecycle current = await GetOrCreateAsync(instanceId, ct).ConfigureAwait(false);

        var steps = new List<string>
        {
            "drain active cycles",
            "mark incomplete external calls for reconciliation",
            "capture and verify a Mind State snapshot",
            "revoke temporary leases",
        };

        if (current.PendingActionRefs.Count > 0)
        {
            steps.Add($"reconcile {current.PendingActionRefs.Count} pending action(s)");
        }

        return new ShutdownPlan(
            instanceId, steps, current.PendingActionRefs,
            !string.IsNullOrEmpty(current.LastVerifiedSnapshotRef));
    }

    public Task<TransitionResult> ResumeAsync(string instanceId, string reason, CancellationToken ct) =>
        TransitionAsync(instanceId, InstanceState.Ready, TransitionActor.Kernel, reason, ct: ct);

    public Task SetVerifiedSnapshotAsync(string instanceId, string snapshotRef, CancellationToken ct) =>
        SetColumnAsync(instanceId, "last_verified_snapshot_ref", snapshotRef, ct);

    public Task SetPendingActionsAsync(
        string instanceId, IReadOnlyList<string> actionRefs, CancellationToken ct) =>
        SetColumnAsync(instanceId, "pending_action_refs", string.Join(',', actionRefs), ct);

    /// <summary>Abrupt failure is observed as RECOVERING, so it is reachable from any live state.</summary>
    private static bool IsLegal(string from, string to)
    {
        if (to == InstanceState.Recovering)
        {
            return from is not (InstanceState.Stopped or InstanceState.Retired or InstanceState.Recovering);
        }

        return Allowed.TryGetValue(from, out var targets)
            && targets.Contains(to, StringComparer.Ordinal);
    }

    private async Task SetColumnAsync(string instanceId, string column, string value, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE instance_lifecycle SET {column} = @v WHERE instance_id = @id;";
        command.Parameters.AddWithValue("@v", value);
        command.Parameters.AddWithValue("@id", instanceId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<InstanceLifecycle?> LoadAsync(string instanceId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT instance_id, state, entered_at_utc, reason, active_cycle_refs,
                   pending_action_refs, last_verified_snapshot_ref, version
              FROM instance_lifecycle WHERE instance_id = @id;
            """;
        command.Parameters.AddWithValue("@id", instanceId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new InstanceLifecycle(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt64(7));
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
