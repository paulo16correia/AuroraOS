using Aurora.Core.Contracts;
using Aurora.Server.Api;
using Aurora.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// What a write command's key says after the command itself failed (RFC 10 rule 1).
/// </summary>
public sealed class ApiIdempotencyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Principal Caller = new("local-mcp-client", "paulo");

    [Fact]
    public async Task ACommandThatFailedIsNotReportedAsStillRunning()
    {
        var store = new InMemoryIdempotencyStore();
        var body = new { id = "memory-1" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiIdempotency.RunAsync<object>(
                store, Caller, "key-1", body, "corr-1",
                _ => throw new InvalidOperationException("the store went away"), Ct));

        // The reservation was EXECUTING when the command threw, so whether it took effect is not
        // known. Nothing is running either, and the caller is told exactly that.
        IResult again = await ApiIdempotency.RunAsync<object>(
            store, Caller, "key-1", body, "corr-2", _ => Task.FromResult<object>("ran twice"), Ct);

        var refused = Assert.IsType<JsonHttpResult<ApiEnvelope<object>>>(again);
        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
        Assert.Contains("not known", refused.Value!.Errors.Single().Message, StringComparison.Ordinal);
        Assert.False(refused.Value.Errors.Single().Retryable);
    }
}
