using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Vault;

/// <summary>How long a lease stays usable.</summary>
public sealed record VaultOptions(TimeSpan LeaseLifetime)
{
    public static readonly VaultOptions Default = new(TimeSpan.FromSeconds(60));
}

/// <summary>
/// Local vault provider (RFC 09, RFC 040): references in SQLite, values encrypted at rest, leased
/// to one tool call at a time and audited without ever recording the value.
/// </summary>
public sealed class SqliteVault : IVault
{
    private const string LocalProvider = "local";

    private readonly SqliteConnectionFactory _factory;
    private readonly AesGcmSecretProtector _protector;
    private readonly IClock _clock;
    private readonly IAuditStore _audit;
    private readonly IPrincipalAccessor _principals;
    private readonly VaultOptions _options;

    public SqliteVault(
        SqliteConnectionFactory factory,
        AesGcmSecretProtector protector,
        IClock clock,
        IAuditStore audit,
        IPrincipalAccessor principals,
        VaultOptions options)
    {
        _factory = factory;
        _protector = protector;
        _clock = clock;
        _audit = audit;
        _principals = principals;
        _options = options;
    }

    public async Task<SecretReference> PutAsync(
        string purpose, IReadOnlyList<string> allowedToolIds, string value,
        DateTimeOffset? rotationDueAtUtc, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new VaultException("A secret must have a value.");
        }

        var id = Guid.NewGuid().ToString("N");
        SealedSecret sealedSecret = _protector.Protect(id, value);
        var now = Iso(_clock.UtcNow);

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO vault_item
                (id, provider, locator, purpose, allowed_tool_ids, rotation_due_at_utc, status,
                 nonce, ciphertext, tag, created_at_utc, updated_at_utc)
            VALUES (@id, @prov, @loc, @purpose, @tools, @due, @status, @n, @c, @t, @now, @now);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@prov", LocalProvider);
        command.Parameters.AddWithValue("@loc", $"{LocalProvider}://{id}");
        command.Parameters.AddWithValue("@purpose", purpose);
        command.Parameters.AddWithValue("@tools", string.Join(',', allowedToolIds));
        command.Parameters.AddWithValue("@due", (object?)(rotationDueAtUtc is null ? null : Iso(rotationDueAtUtc.Value)) ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", VaultItemStatus.Active);
        command.Parameters.AddWithValue("@n", sealedSecret.Nonce);
        command.Parameters.AddWithValue("@c", sealedSecret.Ciphertext);
        command.Parameters.AddWithValue("@t", sealedSecret.Tag);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return (await GetReferenceAsync(id, ct).ConfigureAwait(false))!;
    }

    public async Task<SecretReference?> GetReferenceAsync(string secretReferenceId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ReferenceSelect + " WHERE id = @id;";
        command.Parameters.AddWithValue("@id", secretReferenceId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadReference(reader) : null;
    }

    public async Task<EphemeralSecretHandle> LeaseAsync(
        string secretReferenceId, ToolCallRef toolCall, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, provider, locator, purpose, allowed_tool_ids, rotation_due_at_utc, status,
                   nonce, ciphertext, tag
              FROM vault_item WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", secretReferenceId);

        SecretReference reference;
        SealedSecret sealedSecret;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                await AuditAsync(secretReferenceId, toolCall, "vault_lease_unknown", ct).ConfigureAwait(false);
                throw new VaultException("Unknown secret reference.");
            }

            reference = ReadReference(reader);
            sealedSecret = new SealedSecret(
                (byte[])reader["nonce"], (byte[])reader["ciphertext"], (byte[])reader["tag"]);
        }

        // ROTATING still leases: a rotation in progress must not cut off the credential that is
        // still valid. REVOKED and EXPIRED are refusals.
        if (reference.Status is not (VaultItemStatus.Active or VaultItemStatus.Rotating))
        {
            await AuditAsync(secretReferenceId, toolCall, "vault_lease_refused_status", ct).ConfigureAwait(false);
            throw new VaultException($"Secret is {reference.Status}.");
        }

        // Fail closed: a reference with no allowed tools grants nothing.
        if (!reference.AllowedToolIds.Contains(toolCall.ToolId, StringComparer.Ordinal))
        {
            await AuditAsync(secretReferenceId, toolCall, "vault_lease_refused_tool", ct).ConfigureAwait(false);
            throw new VaultException("This tool is not allowed to use that secret.");
        }

        var value = _protector.Unprotect(secretReferenceId, sealedSecret);
        await AuditAsync(secretReferenceId, toolCall, "vault_lease_granted", ct).ConfigureAwait(false);

        return new EphemeralSecretHandle(
            Guid.NewGuid().ToString("N"), secretReferenceId, toolCall,
            _clock.UtcNow.Add(_options.LeaseLifetime), value);
    }

    public async Task<bool> RevokeAsync(string secretReferenceId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE vault_item SET status = @s, updated_at_utc = @now WHERE id = @id AND status <> @s;";
        command.Parameters.AddWithValue("@s", VaultItemStatus.Revoked);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));
        command.Parameters.AddWithValue("@id", secretReferenceId);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<SecretReference> RotateAsync(
        string secretReferenceId, string newValue, DateTimeOffset? rotationDueAtUtc, CancellationToken ct)
    {
        SecretReference? existing = await GetReferenceAsync(secretReferenceId, ct).ConfigureAwait(false)
            ?? throw new VaultException("Unknown secret reference.");

        if (existing.Status == VaultItemStatus.Revoked)
        {
            throw new VaultException("A revoked secret is not rotated; register a new one.");
        }

        SealedSecret sealedSecret = _protector.Protect(secretReferenceId, newValue);

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE vault_item
               SET nonce = @n, ciphertext = @c, tag = @t, status = @active,
                   rotation_due_at_utc = @due, updated_at_utc = @now
             WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@n", sealedSecret.Nonce);
        command.Parameters.AddWithValue("@c", sealedSecret.Ciphertext);
        command.Parameters.AddWithValue("@t", sealedSecret.Tag);
        command.Parameters.AddWithValue("@active", VaultItemStatus.Active);
        command.Parameters.AddWithValue("@due", (object?)(rotationDueAtUtc is null ? null : Iso(rotationDueAtUtc.Value)) ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));
        command.Parameters.AddWithValue("@id", secretReferenceId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return (await GetReferenceAsync(secretReferenceId, ct).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<SecretReference>> RotationOverdueAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Overdue is surfaced, never enforced: silently cutting off a credential because a date
        // passed is its own outage. The operator decides.
        command.CommandText = ReferenceSelect
            + " WHERE rotation_due_at_utc IS NOT NULL AND rotation_due_at_utc < @now AND status <> @revoked;";
        command.Parameters.AddWithValue("@now", Iso(_clock.UtcNow));
        command.Parameters.AddWithValue("@revoked", VaultItemStatus.Revoked);

        var rows = new List<SecretReference>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadReference(reader));
        }

        return rows;
    }

    private const string ReferenceSelect = """
        SELECT id, provider, locator, purpose, allowed_tool_ids, rotation_due_at_utc, status
          FROM vault_item
        """;

    private static SecretReference ReadReference(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6));

    /// <summary>Records the lease attempt. The value is never part of the record (RFC 09 rule 2).</summary>
    private async Task AuditAsync(string secretReferenceId, ToolCallRef toolCall, string outcome, CancellationToken ct)
    {
        Principal principal = _principals.Current;
        await _audit.AppendAsync(
            new AuditEntry(
                principal.ClientId,
                principal.WindowsUser,
                "vault.lease",
                Hashing.Sha256Hex($"{secretReferenceId}\n{toolCall.ToolCallId}\n{toolCall.ToolId}"),
                outcome,
                Via: "vault"),
            ct).ConfigureAwait(false);
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
