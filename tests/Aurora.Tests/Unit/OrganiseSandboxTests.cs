using System.Text.Json;
using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Files;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Xunit;
using Aurora.Tests.Support;

namespace Aurora.Tests.Unit;

/// <summary>
/// The reference capability (docs/adr/0060), against a real sandbox on disk.
/// </summary>
/// <remarks>
/// Against the real filesystem rather than a fake one, because half of what this capability is for
/// is what happens when a move fails: a stub that always succeeds would let every one of those
/// paths pass without being exercised.
/// </remarks>
public sealed class OrganiseSandboxTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _root =
        TestTemp.Path("organise");

    public OrganiseSandboxTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is not a failed test.
        }
    }

    private OrganiseSandboxCapability Capability() =>
        new(new SandboxFileIndex(_root), new SandboxFileMover(_root));

    private void Given(params string[] relativePaths)
    {
        foreach (var path in relativePaths)
        {
            var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private bool Exists(string relativePath) =>
        File.Exists(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static IReadOnlyList<(string From, string To)> Pairs(JsonElement result, string name) =>
        result.GetProperty(name).EnumerateArray()
            .Select(e => (e.GetProperty("from").GetString()!, e.GetProperty("to").GetString()!))
            .ToList();

    [Fact]
    public async Task ADryRunReturnsThePlanAndMovesNothing()
    {
        Given("notes.md", "photo.png");

        JsonElement result = await Capability().ExecuteAsync(
            Input("""{"rules":[{"match":"*.md","into":"documents"}],"dry_run":true}"""), Ct);

        // The plan is the same shape whether or not it is carried out, which is the whole point of
        // being able to look at it first.
        Assert.True(result.GetProperty("dry_run").GetBoolean());
        Assert.Equal(1, result.GetProperty("planned").GetInt32());
        Assert.Equal(0, result.GetProperty("moved").GetInt32());
        Assert.Equal([("notes.md", "documents/notes.md")], Pairs(result, "plan"));

        Assert.True(Exists("notes.md"));
        Assert.False(Exists("documents/notes.md"));
    }

    [Fact]
    public async Task ARealRunMovesTheMatchesAndLeavesTheRestAlone()
    {
        Given("notes.md", "diary.md", "photo.png");

        JsonElement result = await Capability().ExecuteAsync(
            Input("""{"rules":[{"match":"*.md","into":"documents"}]}"""), Ct);

        Assert.Equal(2, result.GetProperty("moved").GetInt32());
        Assert.True(Exists("documents/notes.md"));
        Assert.True(Exists("documents/diary.md"));

        // A file no rule matched is not touched, and not mentioned.
        Assert.True(Exists("photo.png"));
    }

    [Fact]
    public async Task TheResultCarriesAnUndoThatActuallyUndoesIt()
    {
        Given("notes.md", "diary.md");
        OrganiseSandboxCapability capability = Capability();

        JsonElement result = await capability.ExecuteAsync(
            Input("""{"rules":[{"match":"*.md","into":"documents"}]}"""), Ct);

        var mover = new SandboxFileMover(_root);

        // "Reversible" is something the caller holds rather than something Aurora asserts, so the
        // test replays it rather than checking that the field is present.
        foreach ((var from, var to) in Pairs(result, "undo"))
        {
            await mover.MoveAsync(from, to, Ct);
        }

        Assert.True(Exists("notes.md"));
        Assert.True(Exists("diary.md"));
        Assert.False(Exists("documents/notes.md"));
    }

    [Fact]
    public async Task RunningItTwiceMovesNothingTheSecondTime()
    {
        Given("notes.md");
        OrganiseSandboxCapability capability = Capability();
        var input = """{"rules":[{"match":"**/*.md","into":"documents"}]}""";

        await capability.ExecuteAsync(Input(input), Ct);
        JsonElement again = await capability.ExecuteAsync(Input(input), Ct);

        // Idempotent, and it says why rather than silently doing nothing: the file is reported as
        // already placed instead of being moved onto itself.
        Assert.Equal(0, again.GetProperty("moved").GetInt32());
        Assert.Equal(
            ["documents/notes.md"],
            again.GetProperty("already_placed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task AFileMatchedByTwoRulesStopsTheRun()
    {
        Given("notes.md", "photo.png");

        SandboxViolationException refused = await Assert.ThrowsAsync<SandboxViolationException>(
            () => Capability().ExecuteAsync(
                Input("""
                    {"rules":[{"match":"*.md","into":"documents"},
                              {"match":"notes.*","into":"personal"}]}
                    """), Ct).AsTask());

        // Refused rather than resolved: picking the first would make the outcome depend on the
        // order the rules were written in.
        Assert.Contains("matches 2 rules", refused.Message, StringComparison.Ordinal);

        // And nothing moved, including the file that was unambiguous.
        Assert.True(Exists("notes.md"));
        Assert.True(Exists("photo.png"));
    }

    [Fact]
    public async Task TwoFilesThatWouldCollideAreCaughtWhileNothingHasMovedYet()
    {
        Given("a/notes.md", "b/notes.md");

        SandboxViolationException refused = await Assert.ThrowsAsync<SandboxViolationException>(
            () => Capability().ExecuteAsync(
                Input("""{"rules":[{"match":"**/*.md","into":"documents"}]}"""), Ct).AsTask());

        Assert.Contains("would both become", refused.Message, StringComparison.Ordinal);

        // Found while planning. Half of them landing and the other half failing is the outcome
        // this exists to prevent.
        Assert.True(Exists("a/notes.md"));
        Assert.True(Exists("b/notes.md"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc")]
    [InlineData(@"documents\\..\\..\\outside")]
    public async Task ADestinationThatLeavesTheSandboxIsRefused(string into)
    {
        Given("notes.md");

        SandboxViolationException refused = await Assert.ThrowsAsync<SandboxViolationException>(
            () => Capability().ExecuteAsync(
                Input($$"""{"rules":[{"match":"*.md","into":"{{into}}"}]}"""), Ct).AsTask());

        // Refused against the rule, so the caller is told which rule was wrong rather than which
        // file failed. The mover would refuse it too; this is the better message, not the only one.
        Assert.Contains("not a folder inside the sandbox", refused.Message, StringComparison.Ordinal);
        Assert.True(Exists("notes.md"));
    }

    [Fact]
    public async Task AGlobIsAPatternAndNotARegex()
    {
        Given("report(final).md", "reportXfinalY.md");

        JsonElement result = await Capability().ExecuteAsync(
            Input("""{"rules":[{"match":"report(final).md","into":"documents"}],"dry_run":true}"""),
            Ct);

        // The parentheses are literal. Escaping the whole piece before substituting the wildcards
        // is what keeps a rule from becoming a pattern its author did not write.
        Assert.Equal([("report(final).md", "documents/report(final).md")], Pairs(result, "plan"));
    }

    [Fact]
    public async Task TheDoubleStarCrossesFoldersAndTheSingleStarDoesNot()
    {
        // Different names on purpose: with the same name the deep run would collide, which is a
        // true refusal and would hide whether the pattern matched at all.
        Given("notes.md", "deep/nested/diary.md");

        JsonElement shallow = await Capability().ExecuteAsync(
            Input("""{"rules":[{"match":"*.md","into":"documents"}],"dry_run":true}"""), Ct);

        JsonElement deep = await Capability().ExecuteAsync(
            Input("""{"rules":[{"match":"**/*.md","into":"documents"}],"dry_run":true}"""), Ct);

        Assert.Equal(1, shallow.GetProperty("planned").GetInt32());
        Assert.Equal(2, deep.GetProperty("planned").GetInt32());
    }

    [Fact]
    public async Task ALinkedFileIsNotTreatedAsTheSandboxS()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Given("notes.md");

        var outside = TestTemp.Path("outside") + ".md";
        await File.WriteAllTextAsync(outside, "not yours", Ct);
        File.CreateSymbolicLink(Path.Combine(_root, "linked.md"), outside);

        try
        {
            JsonElement result = await Capability().ExecuteAsync(
                Input("""{"rules":[{"match":"*.md","into":"documents"}],"dry_run":true}"""), Ct);

            // A listing is disclosure, and a link pointing out of the sandbox is not the sandbox's
            // file whatever its name suggests. It is not planned and not reported.
            Assert.Equal([("notes.md", "documents/notes.md")], Pairs(result, "plan"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task TheDescriptorSaysWhatItDoesBeforeItIsAsked()
    {
        CapabilityDescriptor descriptor = Capability().Descriptor;

        // Rearranging a directory by rule is not what somebody pictures when they approve a file
        // write, so it is HIGH and it is approval-gated. Being in the catalogue is not permission.
        Assert.Equal(RiskLevel.High, descriptor.Risk);
        Assert.True(descriptor.ApprovalRequired);

        // Every effect declared, including the one a reader would otherwise infer from the other
        // two and get wrong: a move is not a write followed by a read.
        Assert.Equal(["files.read", "files.write", "files.move"], descriptor.Effects);

        // And the claim the policy engine reads. Without it a HIGH capability is denied outright,
        // which is the right default: if it goes wrong somebody has to be able to put it back.
        Assert.True(descriptor.Reversible);

        await Task.CompletedTask;
    }
}
