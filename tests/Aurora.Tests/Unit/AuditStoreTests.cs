using Aurora.Adapters.Persistence;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class AuditStoreTests
{
    /// <summary>A store with a throwaway signing key and anchor, isolated per test.</summary>
    private static SqliteAuditStore NewStore(SqliteTestDb db, out string anchorPath)
    {
        anchorPath = Path.Combine(Path.GetTempPath(), $"aurora-anchor-{Guid.NewGuid():N}");
        return new SqliteAuditStore(
            db.Factory,
            new TestClock(DateTimeOffset.UnixEpoch),
            new byte[32],
            new AuditAnchorFile(anchorPath));
    }

    [Fact]
    public async Task Append_LinksChain_AndVerifies()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db, out _);

        var h1 = await store.AppendAsync("c1", "u1", "echo.say", "ih1", "completed", CancellationToken.None);
        var h2 = await store.AppendAsync("c1", "u1", "clock.now", "ih2", "completed", CancellationToken.None);
        var h3 = await store.AppendAsync("c1", "u1", "echo.say", "ih3", "denied", CancellationToken.None);

        Assert.NotEqual(h1, h2);
        Assert.NotEqual(h2, h3);
        Assert.False(string.IsNullOrEmpty(h1));

        var verification = await store.VerifyChainAsync(CancellationToken.None);
        Assert.True(verification.Ok);
        Assert.Null(verification.BrokenSequence);
    }

    [Fact]
    public async Task Tampering_BreaksVerification()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db, out _);
        await store.AppendAsync("c1", "u1", "echo.say", "ih1", "completed", CancellationToken.None);
        await store.AppendAsync("c1", "u1", "echo.say", "ih2", "completed", CancellationToken.None);

        // Silently alter the first record's outcome, leaving its record_hash stale.
        using (var connection = db.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE audit_record SET outcome = 'denied' WHERE sequence = 1;";
            command.ExecuteNonQuery();
        }

        var verification = await store.VerifyChainAsync(CancellationToken.None);
        Assert.False(verification.Ok);
        Assert.Equal(1, verification.BrokenSequence);
    }

    // ---- It.3 hardening: keyed chain + external anchor (design/0005) ----

    private static SqliteAuditStore StoreWith(SqliteTestDb db, byte[] key, string anchorPath) =>
        new(db.Factory, new TestClock(DateTimeOffset.UnixEpoch), key, new AuditAnchorFile(anchorPath));

    [Fact]
    public async Task Truncation_IsDetectedByTheAnchor_EvenThoughTheChainStaysValid()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db, out var anchorPath);
        for (var i = 1; i <= 3; i++)
        {
            await store.AppendAsync("c1", "u1", "echo.say", $"ih{i}", "completed", CancellationToken.None);
        }

        // Drop the newest record. What remains is a perfectly self-consistent chain of two.
        using (var connection = db.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM audit_record WHERE sequence = 3;";
            command.ExecuteNonQuery();
        }

        var verification = await store.VerifyChainAsync(CancellationToken.None);

        Assert.False(verification.Ok);
        Assert.Equal(3, verification.BrokenSequence);
        Assert.Contains("removed", verification.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WholesaleRewrite_FailsWithoutTheSigningKey()
    {
        using var db = new SqliteTestDb();
        var anchorPath = Path.Combine(Path.GetTempPath(), $"aurora-anchor-{Guid.NewGuid():N}");

        var real = StoreWith(db, RandomKey(1), anchorPath);
        await real.AppendAsync("c1", "u1", "echo.say", "ih1", "completed", CancellationToken.None);
        await real.AppendAsync("c1", "u1", "echo.say", "ih2", "completed", CancellationToken.None);

        // An attacker with full write access rebuilds the chain, but signs with the wrong key.
        var forged = StoreWith(db, RandomKey(2), anchorPath);
        var verification = await forged.VerifyChainAsync(CancellationToken.None);

        Assert.False(verification.Ok);
        Assert.Equal(1, verification.BrokenSequence);
    }

    [Fact]
    public async Task AnchorMismatchAtTheSameSequence_IsDetected()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db, out var anchorPath);
        await store.AppendAsync("c1", "u1", "echo.say", "ih1", "completed", CancellationToken.None);

        File.WriteAllText(anchorPath, "1 " + new string('0', 64));

        var verification = await store.VerifyChainAsync(CancellationToken.None);

        Assert.False(verification.Ok);
        Assert.Contains("anchor", verification.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyLog_WithNoAnchor_Verifies()
    {
        using var db = new SqliteTestDb();
        var store = NewStore(db, out _);

        var verification = await store.VerifyChainAsync(CancellationToken.None);

        Assert.True(verification.Ok);
    }

    private static byte[] RandomKey(byte seed) => Enumerable.Repeat(seed, 32).ToArray();

    [Fact]
    public void Anchor_NeverMovesBackwards()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aurora-anchor-{Guid.NewGuid():N}");
        var anchor = new AuditAnchorFile(path);

        anchor.Advance(5, "hash-5");
        anchor.Advance(2, "hash-2");

        // A stale writer must not be able to rewind the anchor and hide a truncation.
        Assert.Equal(5, anchor.Read()!.Sequence);
        Assert.Equal("hash-5", anchor.Read()!.RecordHash);

        File.Delete(path);
    }

    [Fact]
    public void AuditKey_IsCreatedOnceAndReused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aurora-key-{Guid.NewGuid():N}");

        var first = AuditKeyFile.LoadOrCreate(path);
        var second = AuditKeyFile.LoadOrCreate(path);

        Assert.Equal(AuditKeyFile.KeyLengthBytes, first.Length);
        Assert.Equal(first, second);

        File.Delete(path);
    }

    [Fact]
    public void AuditKey_RefusesAWrongLengthFileRatherThanRegenerating()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aurora-key-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, new byte[8]);

        // Regenerating would silently invalidate every existing record and destroy the evidence.
        Assert.Throws<InvalidOperationException>(() => AuditKeyFile.LoadOrCreate(path));

        File.Delete(path);
    }
}
