using Aurora.Adapters.Plugins;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The bound on a plugin protocol frame (F-4, docs/adr/0079).
/// </summary>
/// <remarks>
/// The reader must accept a normal line, accept one exactly at the limit, refuse one past it
/// without ever holding all of it, and — the property that keeps a service usable — stay framed
/// afterwards, so the line after an oversized one is read normally.
/// </remarks>
public sealed class BoundedLineReaderTests
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    private static BoundedLineReader Over(string text, int maxChars) =>
        new(new StringReader(text), maxChars);

    [Fact]
    public async Task ANormalLineIsReturned()
    {
        BoundedLineReader reader = Over("hello\n", maxChars: 100);

        LineResult result = await reader.ReadLineAsync(Ct);

        Assert.Equal(LineStatus.Line, result.Status);
        Assert.Equal("hello", result.Line);
    }

    [Fact]
    public async Task ALineExactlyAtTheLimitIsAccepted()
    {
        var line = new string('x', 64);
        BoundedLineReader reader = Over(line + "\n", maxChars: 64);

        LineResult result = await reader.ReadLineAsync(Ct);

        Assert.Equal(LineStatus.Line, result.Status);
        Assert.Equal(line, result.Line);
    }

    [Fact]
    public async Task ALineOnePastTheLimitIsRefused()
    {
        var line = new string('x', 65);
        BoundedLineReader reader = Over(line + "\n", maxChars: 64);

        LineResult result = await reader.ReadLineAsync(Ct);

        Assert.Equal(LineStatus.OverLimit, result.Status);
        Assert.Null(result.Line);
    }

    [Fact]
    public async Task TheReaderStaysFramedAfterAnOversizedLine()
    {
        // The line the whole design protects: a huge frame, then a normal one. The huge one is
        // refused and the normal one that follows is read as if nothing happened.
        var huge = new string('x', 10_000);
        BoundedLineReader reader = Over(huge + "\n" + "{\"kind\":\"ok\"}\n", maxChars: 1024);

        LineResult oversized = await reader.ReadLineAsync(Ct);
        LineResult next = await reader.ReadLineAsync(Ct);

        Assert.Equal(LineStatus.OverLimit, oversized.Status);
        Assert.Equal(LineStatus.Line, next.Status);
        Assert.Equal("{\"kind\":\"ok\"}", next.Line);
    }

    [Fact]
    public async Task CarriageReturnsAreStrippedSoCrlfAndLfFrameAlike()
    {
        BoundedLineReader reader = Over("one\r\ntwo\n", maxChars: 100);

        Assert.Equal("one", (await reader.ReadLineAsync(Ct)).Line);
        Assert.Equal("two", (await reader.ReadLineAsync(Ct)).Line);
    }

    [Fact]
    public async Task EndOfStreamIsReported()
    {
        BoundedLineReader reader = Over("only\n", maxChars: 100);

        Assert.Equal(LineStatus.Line, (await reader.ReadLineAsync(Ct)).Status);
        Assert.Equal(LineStatus.End, (await reader.ReadLineAsync(Ct)).Status);
    }

    [Fact]
    public async Task ATrailingLineWithoutANewlineIsStillReturned()
    {
        BoundedLineReader reader = Over("no newline", maxChars: 100);

        LineResult result = await reader.ReadLineAsync(Ct);

        Assert.Equal(LineStatus.Line, result.Status);
        Assert.Equal("no newline", result.Line);
    }

    [Fact]
    public async Task ManyLinesInOneUnderlyingReadAreSplit()
    {
        // Exercises the scratch-buffer boundary handling: several frames arrive in one block.
        BoundedLineReader reader = Over("a\nb\nc\n", maxChars: 100);

        Assert.Equal("a", (await reader.ReadLineAsync(Ct)).Line);
        Assert.Equal("b", (await reader.ReadLineAsync(Ct)).Line);
        Assert.Equal("c", (await reader.ReadLineAsync(Ct)).Line);
        Assert.Equal(LineStatus.End, (await reader.ReadLineAsync(Ct)).Status);
    }

    [Fact]
    public async Task AnOversizedTrailingLineAtEndOfStreamIsRefusedNotReturned()
    {
        var huge = new string('x', 5000);
        BoundedLineReader reader = Over(huge, maxChars: 1024);

        LineResult result = await reader.ReadLineAsync(Ct);

        Assert.Equal(LineStatus.OverLimit, result.Status);
    }
}
