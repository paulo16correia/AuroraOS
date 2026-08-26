using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aurora.Server;

/// <summary>
/// The only thing in Aurora that happens without being asked.
/// </summary>
/// <remarks>
/// Until this existed, nothing ran on a timer at all: signals never expired, needs never decayed,
/// indeterminate calls were never reconciled, incidents were never raised, and events sat in the
/// outbox undelivered — unless somebody happened to POST to <c>/v1/maintenance</c>. A system whose
/// upkeep only runs when poked has no upkeep.
/// <para>
/// What it is allowed to do is deliberately narrow, and none of it is acting on Aurora's own
/// behalf. It runs the maintenance pass, which surfaces and never executes; and it pumps the event
/// bus, which delivers facts to consumers that were already subscribed. Anything with an effect
/// outside Aurora still goes through the kernel, a policy decision and, where required, a person.
/// LAW-006 is not softened by there being a clock.
/// </para>
/// </remarks>
public sealed class AuroraHeartbeat : BackgroundService
{
    /// <summary>
    /// How long after starting before the first beat.
    /// </summary>
    /// <remarks>
    /// Long enough that a restart loop does not run maintenance on every attempt, and short enough
    /// that somebody starting Aurora to watch it does not conclude nothing happens.
    /// </remarks>
    private static readonly TimeSpan FirstBeat = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly TimeSpan _interval;

    public AuroraHeartbeat(IServiceProvider services, AuroraServerOptions options)
    {
        _services = services;
        _interval = options.HeartbeatInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(FirstBeat, stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);

        do
        {
            await BeatAsync(stopping).ConfigureAwait(false);
        }
        while (await SafeWaitAsync(timer, stopping).ConfigureAwait(false));
    }

    /// <summary>
    /// One beat: upkeep, then delivery.
    /// </summary>
    /// <remarks>
    /// Each half is isolated from the other. A maintenance pass that threw used to be able to take
    /// the whole loop with it, and then nothing would ever run again for the life of the process —
    /// which is the failure mode a background loop has that a request does not, because nobody is
    /// waiting for an answer that never comes.
    /// </remarks>
    private async Task BeatAsync(CancellationToken ct)
    {
        using IServiceScope scope = _services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        await RunAsync(
            () => services.GetRequiredService<IMaintenanceService>()
                .RunAsync(new SituationContext(TimeZoneInfo.Local.Id), ct),
            ct).ConfigureAwait(false);

        var bus = services.GetRequiredService<IEventBus>();

        foreach (IEventConsumer consumer in services.GetServices<IEventConsumer>())
        {
            await RunAsync(
                async () =>
                {
                    // Re-declared every beat, and idempotent by id. A consumer whose interests
                    // widened between two beats — a plugin installed since the last one — is
                    // subscribed to the new types without anything having to notice.
                    await bus.SubscribeAsync(Subscribe(consumer), ct).ConfigureAwait(false);
                    await bus.PumpAsync(consumer, ct).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>One consumer's standing interest in the bus.</summary>
    /// <remarks>
    /// At-least-once, because the alternative loses an event on a crash between delivery and
    /// acknowledgement — and every consumer here is written to tolerate seeing one twice.
    /// </remarks>
    private static Subscription Subscribe(IEventConsumer consumer) => new(
        $"heartbeat:{consumer.Name}",
        consumer.Name,
        consumer.EventTypes.Count > 0
            ? consumer.EventTypes
            : [.. EventCatalogue.Declared.Select(c => c.Type).Distinct(StringComparer.Ordinal)],
        FilterRef: null,
        DeliveryMode.AtLeastOnce,
        Checkpoint: 0,
        SubscriptionStatus.Active,
        MaxAttempts: 3,

        // The highest version Aurora itself publishes. A consumer paused by a version it cannot
        // read is RFC 050 rule 2 working, and it should happen when the schema moves ahead of the
        // code — not because this number was written down too small.
        MaxSchemaVersion: EventCatalogue.Declared.Max(c => c.SchemaVersion));

    private static async Task RunAsync(Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Swallowed on purpose, and not silently: whatever went wrong has already been
            // recorded by the component it went wrong in — a failed pump dead-letters, a failed
            // maintenance pass is the one thing that would otherwise stop every later one.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
