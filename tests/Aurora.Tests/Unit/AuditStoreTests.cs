using Aurora.Adapters.Persistence;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class AuditStoreTests
{
    [Fact]
    public async Task Append_LinksChain_AndVerifies()
    {
        using var db = new SqliteTestDb();
        var store = new SqliteAuditStore(db.Factory, new TestClock(DateTimeOffset.UnixEpoch));

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
        var store = new SqliteAuditStore(db.Factory, new TestClock(DateTimeOffset.UnixEpoch));
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
}
