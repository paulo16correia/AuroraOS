using Aurora.Adapters.Presence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Server;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Who drains the voice consumer, and how often.
/// </summary>
/// <remarks>
/// The wiring that routes a plugin's report to Aurora's decision was correct and unusable: the only
/// thing that pumped consumers was the heartbeat, which runs every five minutes by default. Somebody
/// would ask Aurora a question in a voice channel and be answered, durably and in order, long after
/// they had left.
/// <para>
/// These are about the arrangement rather than the timing — a test that waits for a quarter of a
/// second to see whether something happened in a quarter of a second is a test that fails on a busy
/// machine. What is worth holding is that the intervals are what the comments say, that the two
/// pumps do not both own the same consumer, and that they agree on the subscription.
/// </para>
/// </remarks>
public sealed class VoiceConversationPumpTests
{
    private sealed class Counting : IEventConsumer
    {
        public Counting(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public IReadOnlyList<string> EventTypes => [];

        public Task<ConsumeResult> ConsumeAsync(DomainEvent domainEvent, CancellationToken ct) =>
            Task.FromResult(new ConsumeResult(ConsumeOutcome.Acked));
    }

    [Fact]
    public void TheVoiceConsumerIsOwnedByThePumpAndNotAlsoByTheHeartbeat()
    {
        // Two subscriptions under two ids are two checkpoints, and two checkpoints over one
        // conversation is Aurora answering the same question twice.
        Subscription byPump = AuroraHeartbeat.Subscribe(
            new Counting(VoiceConversationConsumer.Pumped), by: "pump");

        Subscription byHeartbeat = AuroraHeartbeat.Subscribe(
            new Counting(VoiceConversationConsumer.Pumped));

        Assert.NotEqual(byHeartbeat.Id, byPump.Id);

        // Which is why the heartbeat has to skip it, and the name is a constant precisely so the two
        // sides cannot drift apart.
        Assert.Equal("voice-conversation", VoiceConversationConsumer.Pumped);
        Assert.Equal(VoiceConversationConsumer.Pumped, new VoiceConversationConsumer(null!).Name);
    }

    [Fact]
    public void BothPumpsAgreeOnEverythingExceptWhoIsAsking()
    {
        // The shape comes from one place. How far back to read, how many attempts, and which schema
        // versions are not questions the two should answer differently.
        var consumer = new Counting("whatever");

        Subscription byPump = AuroraHeartbeat.Subscribe(consumer, by: "pump");
        Subscription byHeartbeat = AuroraHeartbeat.Subscribe(consumer);

        // Field by field rather than record equality: EventTypes is a list, and two lists holding
        // the same strings are not the same list.
        Assert.Equal(byHeartbeat.Consumer, byPump.Consumer);
        Assert.Equal(byHeartbeat.EventTypes, byPump.EventTypes);
        Assert.Equal(byHeartbeat.Mode, byPump.Mode);
        Assert.Equal(byHeartbeat.Checkpoint, byPump.Checkpoint);
        Assert.Equal(byHeartbeat.Status, byPump.Status);
        Assert.Equal(byHeartbeat.MaxAttempts, byPump.MaxAttempts);
        Assert.Equal(byHeartbeat.MaxSchemaVersion, byPump.MaxSchemaVersion);
    }

    [Fact]
    public void AConsumerWithNoStatedInterestIsSubscribedToEverythingDeclared()
    {
        // Otherwise a consumer that wanted everything would be subscribed to nothing, which is the
        // quiet version of not being wired up at all.
        Subscription all = AuroraHeartbeat.Subscribe(new Counting("broad"));

        Assert.NotEmpty(all.EventTypes);
        Assert.Contains(EventCatalogue.PluginObservationReported, all.EventTypes);
    }
}
