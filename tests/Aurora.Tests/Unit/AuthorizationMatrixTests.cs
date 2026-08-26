using Aurora.Adapters.Capabilities;
using Aurora.Adapters.Events;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Condition 5 of the architecture review: event contracts and authorization matrices, published
/// per capability — and kept true.
/// </summary>
public sealed class AuthorizationMatrixTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>The committed reference page, found from the test assembly rather than the cwd.</summary>
    private static string MatrixPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "docs", "reference", "capability-authorization.md");
    }

    private static readonly FakePrincipalAccessor Principals = new(new Principal("c1", "u1"));

    private static IReadOnlyList<CapabilityDescriptor> Capabilities()
    {
        // Every capability the server can register, including the frozen ones: the matrix documents
        // what each is authorized to do, and a capability left out of the table because it happens
        // to be switched off is exactly the one somebody turns on without reading anything.
        var registry = new StaticCapabilityRegistry(
        [
            new ClockNowCapability(new Adapters.Time.SystemClock()),
            new EchoSayCapability(),
            new RememberNoteCapability(new NullNoteStore(), Principals),
            new RecallNotesCapability(new NullNoteStore(), Principals),
            new WriteSandboxFileCapability(new NullSandboxWriter()),
            new ReadSandboxFileCapability(new NullSandboxReader()),
            new OrganiseSandboxCapability(new NullSandboxIndex(), new NullSandboxMover()),
        ]);

        return registry.List(null);
    }

    [Fact]
    public void ThePublishedMatrixMatchesTheCode()
    {
        var expected = AuthorizationMatrix.Render(Capabilities(), EventCatalogue.Declared);
        var path = MatrixPath();

        // Regenerated on demand rather than by hand: AURORA_WRITE_REFERENCE=1 dotnet test.
        if (Environment.GetEnvironmentVariable("AURORA_WRITE_REFERENCE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
        }

        var committed = File.ReadAllText(path).ReplaceLineEndings();

        // A matrix that drifts from the code is worse than none: it is the document somebody
        // reaches for when deciding whether something is safe.
        Assert.Equal(expected.ReplaceLineEndings(), committed);
    }

    [Fact]
    public void EveryCapabilityAppearsInTheMatrix()
    {
        var rendered = AuthorizationMatrix.Render(Capabilities(), EventCatalogue.Declared);

        foreach (CapabilityDescriptor capability in Capabilities())
        {
            Assert.Contains($"`{capability.ActionId}`", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDeclaredEventNamesItsProducerAndItsConsumers()
    {
        foreach (EventContract contract in EventCatalogue.Declared)
        {
            Assert.False(string.IsNullOrWhiteSpace(contract.Producer));
            Assert.False(string.IsNullOrWhiteSpace(contract.Payload));
            Assert.True(Sensitivity.IsKnown(contract.SensitivityClass));

            // LAW-007: each consumer declares its subscription. A declared event nobody consumes is
            // either a gap in the declaration or an event nobody needed.
            Assert.NotEmpty(contract.Consumers);
        }
    }

    [Fact]
    public void OnlyOneProducerIsReachableFromOutsideAuroraAndItMayEmitOneType()
    {
        IReadOnlyList<EventContract> ingress = EventCatalogue.For(EventCatalogue.Producers.Api);

        // A surface outside Aurora choosing its own event type is a surface that can assert
        // anything about anything.
        EventContract only = Assert.Single(ingress);
        Assert.Equal(EventCatalogue.ExternalObservationReported, only.Type);
    }

    [Fact]
    public async Task AnUndeclaredEventCannotBePublishedByAnyone()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var bus = new Adapters.Events.SqliteEventBus(
            db.Factory, new SqliteOutbox(new DeclaredEventCatalogue(), clock), clock);

        await Assert.ThrowsAsync<EventContractException>(() =>
            bus.PublishAsync(
                new OutboxWrite(
                    "SomethingNobodyDeclared", 1, EventCatalogue.Producers.Kernel, "c-1",
                    Sensitivity.Private, PayloadJson: "{}"),
                Ct));
    }

    [Fact]
    public async Task AProducerCannotEmitAnotherProducerSEvents()
    {
        using var db = new SqliteTestDb();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var bus = new Adapters.Events.SqliteEventBus(
            db.Factory, new SqliteOutbox(new DeclaredEventCatalogue(), clock), clock);

        // This is how a component starts speaking for a part of the system it does not own.
        EventContractException refused = await Assert.ThrowsAsync<EventContractException>(() =>
            bus.PublishAsync(
                new OutboxWrite(
                    EventCatalogue.JobDue, 1, EventCatalogue.Producers.Api, "c-1",
                    Sensitivity.Private, PayloadJson: "{}"),
                Ct));

        Assert.Contains("scheduler", refused.Message, StringComparison.Ordinal);
    }

    private sealed class NullNoteStore : INoteStore
    {
        public Task<RememberedNote> SaveAsync(Principal principal, string note, CancellationToken ct) =>
            Task.FromResult(new RememberedNote("note/1", principal.ClientId, note, "2026-01-01T00:00:00.0000000+00:00"));

        public Task<IReadOnlyList<RememberedNote>> ListAsync(Principal principal, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RememberedNote>>([]);
    }

    private sealed class NullSandboxWriter : ISandboxFileWriter
    {
        public Task<SandboxWriteResult> WriteAsync(string relativePath, string content, CancellationToken ct) =>
            Task.FromResult(new SandboxWriteResult(relativePath, 0, false));
    }

    private sealed class NullSandboxReader : ISandboxFileReader
    {
        public Task<SandboxReadResult> ReadAsync(string relativePath, CancellationToken ct) =>
            Task.FromResult(new SandboxReadResult(relativePath, string.Empty, 0));
    }

    private sealed class NullSandboxIndex : ISandboxFileIndex
    {
        public Task<IReadOnlyList<SandboxEntry>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SandboxEntry>>([]);
    }

    private sealed class NullSandboxMover : ISandboxFileMover
    {
        public Task<SandboxMoveResult> MoveAsync(string from, string to, CancellationToken ct) =>
            Task.FromResult(new SandboxMoveResult(from, to));
    }

    [Fact]
    public void TheMatrixCoversEveryCapabilityThatExists()
    {
        var listed = Capabilities().Select(c => c.ActionId).ToHashSet(StringComparer.Ordinal);

        var implemented = typeof(EchoSayCapability).Assembly.GetTypes()
            .Where(t => typeof(ICapability).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => t.Name)
            .ToList();

        // The list above is written by hand, which is fine right up until somebody adds a
        // capability and does not touch it. Then the matrix documents everything except the newest
        // thing — which is the one a reader most needs the table for.
        Assert.Equal(implemented.Count, listed.Count);
    }
}
