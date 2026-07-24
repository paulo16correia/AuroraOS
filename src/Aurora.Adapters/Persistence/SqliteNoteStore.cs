using System.Globalization;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Persistence;

/// <summary>SQLite-backed store for <c>memory.remember</c> notes, scoped per principal.</summary>
public sealed class SqliteNoteStore : INoteStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IClock _clock;

    public SqliteNoteStore(SqliteConnectionFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<RememberedNote> SaveAsync(Principal principal, string note, CancellationToken ct)
    {
        var noteId = Guid.NewGuid().ToString("N");
        var now = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO remembered_note (note_id, principal_client_id, note, created_at_utc)
            VALUES (@id, @c, @note, @now);
            """;
        command.Parameters.AddWithValue("@id", noteId);
        command.Parameters.AddWithValue("@c", principal.ClientId);
        command.Parameters.AddWithValue("@note", note);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return new RememberedNote(noteId, principal.ClientId, note, now);
    }

    public async Task<IReadOnlyList<RememberedNote>> ListAsync(Principal principal, CancellationToken ct)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT note_id, principal_client_id, note, created_at_utc
            FROM remembered_note
            WHERE principal_client_id = @c
            ORDER BY created_at_utc ASC;
            """;
        command.Parameters.AddWithValue("@c", principal.ClientId);

        var notes = new List<RememberedNote>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            notes.Add(new RememberedNote(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return notes;
    }
}
