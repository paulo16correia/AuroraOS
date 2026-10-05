using Aurora.Adapters.Presence;
using Aurora.Adapters.Reasoning;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Server;

/// <summary>
/// Drains what a voice plugin reported, at the speed a conversation happens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <see cref="VoiceConversationConsumer"/> is an
/// <see cref="IEventConsumer"/>, and the only thing that pumped consumers was the heartbeat — which
/// runs every five minutes by default. That made the wiring correct and useless: somebody would ask
/// Aurora a question in a voice channel and be answered, durably and in order, up to five minutes
/// later. A conversation is not a queue that drains eventually.
/// </para>
/// <para>
/// So the same consumer, pumped on its own schedule. The bus is still the path, which is what keeps
/// at-least-once delivery and an event surviving a crash between being published and being acted on;
/// what changes is how long a sentence waits before Aurora looks.
/// </para>
/// <para>
/// <b>Idle is the normal state.</b> Voice is off until an owner enables it, and nobody is in a
/// channel most of the time, so a fixed fast poll would be a tight loop against SQLite for nothing.
/// It goes fast while there is something to drain and slows when there is not, which means the cost
/// is paid during a conversation and not for the rest of the day.
/// </para>
/// </remarks>
public sealed class VoiceConversationPump : BackgroundService
{
    /// <summary>
    /// How long a sentence waits before Aurora looks, while a conversation is happening.
    /// </summary>
    /// <remarks>
    /// Chosen against what it is competing with rather than against a round number. Recognition
    /// takes a few seconds and the model about one; a quarter of a second on top is under the pause
    /// a person leaves before expecting an answer, and well under the time it took to hear them.
    /// Making it smaller would buy nothing anybody could notice.
    /// </remarks>
    private static readonly TimeSpan Talking = TimeSpan.FromMilliseconds(250);

    /// <summary>How often to look when nothing has been said for a while.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many empty rounds before slowing down.
    /// </summary>
    /// <remarks>
    /// Twelve at a quarter of a second is three seconds — longer than the gap between two turns of
    /// the same conversation, so a pause for breath does not cost the next answer the slow interval.
    /// </remarks>
    private const int QuietAfter = 12;

    private readonly IServiceProvider _services;

    public VoiceConversationPump(IServiceProvider services)
    {
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await PrepareTheModelAsync(stopping).ConfigureAwait(false);

        var empty = 0;

        while (!stopping.IsCancellationRequested)
        {
            var drained = 0;

            try
            {
                drained = await DrainAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Not a reason to stop pumping. A broken round is one conversation's turn; a pump
                // that exits is every conversation afterwards, silently.
                //
                // Deliberately silent here: whatever failed has already been recorded by whatever
                // failed, and a log line per quarter-second would bury it.
            }

            empty = drained > 0 ? 0 : empty + 1;

            try
            {
                await Task.Delay(empty >= QuietAfter ? Quiet : Talking, stopping)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Loads the model once, before anybody speaks, when voice is switched on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured here: a cold llama3.1:8b answers in 54 seconds and a loaded one in half a second,
    /// and the conversation boundary waits ten. Without this the first thing said after a restart is
    /// answered with silence — the request times out, the load it triggered finishes anyway, and the
    /// second question works. Somebody who has to ask twice has not been answered.
    /// </para>
    /// <para>
    /// Only when voice is enabled, because loading four gigabytes for a channel nobody is in is a
    /// cost with no return — and voice is off until an owner turns it on. Not awaited for its answer
    /// beyond logging nothing: if the runtime is absent, the first real question says so in words,
    /// and a pump that refused to start over a warm-up would be worse than a slow first sentence.
    /// </para>
    /// </remarks>
    private async Task PrepareTheModelAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _services.CreateScope();
            IServiceProvider services = scope.ServiceProvider;

            VoiceSettings voice = await services.GetRequiredService<IVoicePolicy>()
                .CurrentAsync(ct).ConfigureAwait(false);

            if (voice.Stopped || !voice.Enabled)
            {
                return;
            }

            if (services.GetRequiredService<ILocalLanguageModel>() is OllamaLanguageModel ollama)
            {
                await ollama.PrepareAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A warm-up that failed is a slower first sentence, not a reason for the pump never to
            // run — which is what throwing from here would mean.
        }
    }

    /// <summary>One round: how many events the voice consumer took.</summary>
    private async Task<int> DrainAsync(CancellationToken ct)
    {
        using IServiceScope scope = _services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        IEventConsumer? consumer = services.GetServices<IEventConsumer>()
            .FirstOrDefault(c => c.Name == VoiceConversationConsumer.Pumped);

        if (consumer is null)
        {
            return 0;
        }

        var bus = services.GetRequiredService<IEventBus>();

        // Re-declared every round and idempotent by id, the way the heartbeat does it: a consumer
        // whose interests widened — a plugin installed a minute ago — is subscribed without anything
        // having to notice. The shape comes from one place, because how far back to read and how
        // many attempts to make are not questions this should answer differently.
        await bus.SubscribeAsync(
            AuroraHeartbeat.Subscribe(consumer, by: "pump"), ct).ConfigureAwait(false);

        return await bus.PumpAsync(consumer, ct).ConfigureAwait(false);
    }
}
