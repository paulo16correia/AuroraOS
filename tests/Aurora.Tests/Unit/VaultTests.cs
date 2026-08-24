using Aurora.Adapters.Vault;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class VaultTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Principal Caller = new("c1", "u1");
    private static readonly ToolCallRef MailerCall = new("call-1", "mailer");

    private static (SqliteVault Vault, RecordingAuditStore Audit) New(
        SqliteTestDb db, DateTimeOffset? now = null)
    {
        var audit = new RecordingAuditStore();
        var vault = new SqliteVault(
            db.Factory,
            new AesGcmSecretProtector(Enumerable.Repeat((byte)9, 32).ToArray()),
            new TestClock(now ?? DateTimeOffset.UnixEpoch),
            audit,
            new FakePrincipalAccessor(Caller),
            VaultOptions.Default);
        return (vault, audit);
    }

    private static Task<SecretReference> PutAsync(
        SqliteVault vault, string value = "hunter2-the-real-one", params string[] tools) =>
        vault.PutAsync("smtp password", tools.Length == 0 ? ["mailer"] : tools, value, null, Ct);

    // ---- the value never enters the domain ----

    [Fact]
    public async Task Reference_CarriesNoValue()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);

        SecretReference reference = await PutAsync(vault);

        // Everything needed to decide a lease, nothing that reveals the secret.
        Assert.Equal(VaultItemStatus.Active, reference.Status);
        Assert.Equal("smtp password", reference.Purpose);
        Assert.DoesNotContain("hunter2", reference.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_RedactsItselfWhenPrinted()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference reference = await PutAsync(vault);

        using EphemeralSecretHandle handle = await vault.LeaseAsync(reference.Id, MailerCall, Ct);

        // A handle that can print a secret will eventually print one into a log.
        Assert.DoesNotContain("hunter2", handle.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", handle.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handle_DeliversTheValueOnce()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference reference = await PutAsync(vault);

        using EphemeralSecretHandle handle = await vault.LeaseAsync(reference.Id, MailerCall, Ct);

        Assert.Equal("hunter2-the-real-one", handle.Use(s => new string(s)));
        Assert.True(handle.IsSpent);

        // A leaked handle must not be replayable later in the process.
        Assert.Throws<VaultException>(() => handle.Use(s => new string(s)));
    }

    [Fact]
    public async Task DisposedHandle_NoLongerYieldsAnything()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference reference = await PutAsync(vault);

        EphemeralSecretHandle handle = await vault.LeaseAsync(reference.Id, MailerCall, Ct);
        handle.Dispose();

        Assert.Throws<ObjectDisposedException>(() => handle.Use(s => new string(s)));
    }

    // ---- encrypted at rest ----

    [Fact]
    public async Task ValueIsEncryptedAtRest()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        await PutAsync(vault);

        using var connection = db.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ciphertext FROM vault_item;";
        var ciphertext = (byte[])command.ExecuteScalar()!;

        Assert.DoesNotContain(
            "hunter2",
            System.Text.Encoding.UTF8.GetString(ciphertext),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CiphertextIsBoundToItsOwnReference()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference first = await PutAsync(vault, "first-secret-value");
        SecretReference second = await PutAsync(vault, "second-secret-value");

        // Swap the blobs. The secret's id is the associated data, so authentication must fail
        // rather than handing the tool the wrong credential.
        using (var connection = db.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE vault_item SET
                    nonce = (SELECT nonce FROM vault_item WHERE id = @b),
                    ciphertext = (SELECT ciphertext FROM vault_item WHERE id = @b),
                    tag = (SELECT tag FROM vault_item WHERE id = @b)
                WHERE id = @a;
                """;
            command.Parameters.AddWithValue("@a", first.Id);
            command.Parameters.AddWithValue("@b", second.Id);
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => vault.LeaseAsync(first.Id, MailerCall, Ct));
    }

    // ---- who may lease ----

    [Fact]
    public async Task AToolNotOnTheListIsRefused()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference reference = await PutAsync(vault, tools: "mailer");

        await Assert.ThrowsAsync<VaultException>(
            () => vault.LeaseAsync(reference.Id, new ToolCallRef("call-2", "browser"), Ct));
    }

    [Fact]
    public async Task AnEmptyAllowListGrantsNothing()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference reference = await vault.PutAsync("smtp", [], "value-goes-here", null, Ct);

        await Assert.ThrowsAsync<VaultException>(() => vault.LeaseAsync(reference.Id, MailerCall, Ct));
    }

    [Fact]
    public async Task AnUnknownReferenceIsRefused()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);

        await Assert.ThrowsAsync<VaultException>(() => vault.LeaseAsync("nope", MailerCall, Ct));
    }

    [Fact]
    public async Task ARevokedSecretIsRefused()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference reference = await PutAsync(vault);

        Assert.True(await vault.RevokeAsync(reference.Id, Ct));

        await Assert.ThrowsAsync<VaultException>(() => vault.LeaseAsync(reference.Id, MailerCall, Ct));
    }

    // ---- rotation ----

    [Fact]
    public async Task RotationReplacesTheValue()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference reference = await PutAsync(vault, "old-secret-value");

        await vault.RotateAsync(reference.Id, "new-secret-value", null, Ct);

        using EphemeralSecretHandle handle = await vault.LeaseAsync(reference.Id, MailerCall, Ct);
        Assert.Equal("new-secret-value", handle.Use(s => new string(s)));
    }

    [Fact]
    public async Task ARevokedSecretIsNotRotated()
    {
        using var db = new SqliteTestDb();
        var (vault, _) = New(db);
        SecretReference reference = await PutAsync(vault);
        await vault.RevokeAsync(reference.Id, Ct);

        await Assert.ThrowsAsync<VaultException>(
            () => vault.RotateAsync(reference.Id, "another-value-here", null, Ct));
    }

    [Fact]
    public async Task OverdueRotationIsSurfacedButNotEnforced()
    {
        using var db = new SqliteTestDb();
        var start = DateTimeOffset.UnixEpoch;
        var (vault, _) = New(db, start);
        SecretReference reference = await vault.PutAsync(
            "smtp", ["mailer"], "value-goes-here", start.AddDays(1), Ct);

        var (later, _) = New(db, start.AddDays(2));

        Assert.Single(await later.RotationOverdueAsync(Ct));

        // Cutting off a credential because a date passed is its own outage; the operator decides.
        using EphemeralSecretHandle handle = await later.LeaseAsync(reference.Id, MailerCall, Ct);
        Assert.Equal("value-goes-here", handle.Use(s => new string(s)));
    }

    // ---- auditing without recording the value ----

    [Fact]
    public async Task LeaseIsAudited_WithoutTheSecret()
    {
        using var db = new SqliteTestDb();
        var (vault, audit) = New(db);
        SecretReference reference = await PutAsync(vault);

        using EphemeralSecretHandle handle = await vault.LeaseAsync(reference.Id, MailerCall, Ct);
        handle.Use(s => new string(s));

        AuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal("vault.lease", entry.ActionId);
        Assert.Equal("vault_lease_granted", entry.Outcome);

        var serialised = string.Join('|', audit.Entries.Select(e => string.Join('|',
            e.ActionId, e.Outcome, e.InputHash, e.Via ?? string.Empty, e.Reason ?? string.Empty)));
        Assert.DoesNotContain("hunter2", serialised, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARefusedLeaseIsAuditedToo()
    {
        using var db = new SqliteTestDb();
        var (vault, audit) = New(db);
        SecretReference reference = await PutAsync(vault, tools: "mailer");

        await Assert.ThrowsAsync<VaultException>(
            () => vault.LeaseAsync(reference.Id, new ToolCallRef("call-2", "browser"), Ct));

        Assert.Equal("vault_lease_refused_tool", Assert.Single(audit.Entries).Outcome);
    }
}
