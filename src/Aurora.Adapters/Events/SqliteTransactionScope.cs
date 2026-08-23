using Aurora.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Events;

/// <summary>
/// A transaction a producer owns, so its state change and its outbox row commit together
/// (RFC 050 rule 1).
/// </summary>
public sealed class SqliteTransactionScope : IDbTransactionScope
{
    private bool _committed;

    internal SqliteTransactionScope(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    internal SqliteConnection Connection { get; }

    internal SqliteTransaction Transaction { get; }

    public async Task CommitAsync(CancellationToken ct)
    {
        await Transaction.CommitAsync(ct).ConfigureAwait(false);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        // An uncommitted scope rolls back, taking the outbox row with the state change it
        // described. Neither can survive without the other.
        if (!_committed)
        {
            try
            {
                await Transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already completed.
            }
        }

        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
