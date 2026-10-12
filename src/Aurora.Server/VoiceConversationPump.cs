using Aurora.Adapters.Presence;
using Aurora.Adapters.Reasoning;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<VoiceConversationPump> _log;

    /// <summary>Whether the model answered the last time it was asked. Null before it was.</summary>
    private bool? _modelWasThere;

    /// <summary>When it was last asked.</summary>
    private DateTimeOffset _modelAskedAt = DateTimeOffset.MinValue;

    public VoiceConversationPump(IServiceProvider services, ILogger<VoiceConversationPump> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await PrepareTheModelAsync(stopping).ConfigureAwait(false);

        var empty = 0;

        while (!stopping.IsCancellationRequested)
        {
            var drained = 0;

            await WatchTheModelAsync(stopping).ConfigureAwait(false);

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
    /// cost with no return — and voice is off until an owner turns it on. A pump that refused to
    /// start over a warm-up would be worse than a slow first sentence, so this never throws.
    /// <para>
    /// It does log, and it used to not. The comment here said that if the runtime was absent "the
    /// first real question says so in words", and that was simply untrue: the boundary returns
    /// Silent(ModelUnavailable), the consumer acknowledges it, and nobody hears anything. The
    /// owner met that — Ollama had died between sessions, Aurora sat mute in a channel with six
    /// people in it, and the only way to find out why was to go and look at the process list.
    /// </para>
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
        catch (Exception warm) when (!ct.IsCancellationRequested)
        {
            // A warm-up that failed is a slower first sentence, not a reason for the pump never to
            // run — which is what throwing from here would mean. But it is also the likeliest
            // reason Aurora will answer nothing all evening, so it is said here, where somebody
            // starting her up is looking, rather than inferred from her silence later.
            _log.LogWarning(
                warm,
                "Voice is on but the language model could not be reached, so Aurora will hear "
                + "people and answer nobody. Start it and she picks up on the next thing said.");
        }
    }

    /// <summary>How often the model is asked whether it is still there.</summary>
    /// <remarks>
    /// A minute. Often enough that an owner who turns to the channel finds out before somebody
    /// speaks, and rarely enough that it is not a load the runtime notices — the probe is the same
    /// warm-up call that runs at startup, which an already-loaded model answers immediately.
    /// </remarks>
    private static readonly TimeSpan ModelWatch = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Asks the model whether it is still answering, and says so when that changes.
    /// </summary>
    /// <remarks>
    /// Only on the change, in both directions. Repeating "the model is down" every minute would be
    /// the kind of noise somebody learns to scroll past, and saying nothing when it comes back
    /// would leave the last word on the subject being wrong.
    /// </remarks>
    private async Task WatchTheModelAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _modelAskedAt < ModelWatch)
        {
            return;
        }

        _modelAskedAt = DateTimeOffset.UtcNow;

        bool there;

        try
        {
            using IServiceScope scope = _services.CreateScope();
            IServiceProvider services = scope.ServiceProvider;

            VoiceSettings voice = await services.GetRequiredService<IVoicePolicy>()
                .CurrentAsync(ct).ConfigureAwait(false);

            if (voice.Stopped || !voice.Enabled)
            {
                // Nothing is owed by a voice nobody turned on, and probing a runtime for a channel
                // Aurora is not in would be the cost this has always declined to pay.
                return;
            }

            there = services.GetRequiredService<ILocalLanguageModel>() is not OllamaLanguageModel ollama
                || await ollama.PrepareAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            there = false;
        }

        var first = _modelWasThere is null;

        if (_modelWasThere == there)
        {
            return;
        }

        _modelWasThere = there;

        if (there)
        {
            if (first)
            {
                // A model that was there the first time anybody looked has not recovered from
                // anything, and saying so would be a line about nothing on every ordinary start.
                return;
            }

            _log.LogInformation("The language model is answering again.");
            return;
        }

        _log.LogWarning(
            "The language model has stopped answering, so Aurora will hear people and reply to "
            + "nobody. Her own trail will show her deciding to answer and then nothing.");
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
