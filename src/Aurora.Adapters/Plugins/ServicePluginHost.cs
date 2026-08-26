using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// Runs a plugin that holds a connection, and invokes it over the connection it holds.
/// </summary>
/// <remarks>
/// <see cref="SubprocessPluginHost"/> runs one process per call: start, write, read, exit. That is
/// right for a plugin that answers a question and wrong for one that keeps a socket open — a
/// gateway with heartbeats cannot be re-established for every call, and an audio stream cannot be
/// torn down after one.
/// <para>
/// So a service is started once and supervised: watched, restarted with backoff when it dies, and
/// held when it will not stay up. Calls are multiplexed over its stdin and stdout as JSON, one
/// object per line, correlated by an id Aurora chooses.
/// </para>
/// <para>
/// Everything the process says is data. It is the same subprocess under the same sandbox as any
/// other plugin, and holding a connection earns it nothing: it is still refused a capability
/// outside its manifest, still cannot be handed data above its ceiling, and what it reports arrives
/// as an observation rather than an instruction.
/// </para>
/// </remarks>
public sealed class ServicePluginHost : IPluginHost, IPluginServiceSupervisor, IAsyncDisposable
{
    private readonly string _root;
    private readonly IPluginSandbox _sandbox;
    private readonly IPluginSecretSource _secrets;
    private readonly IPluginObservationSink _observations;
    private readonly IClock _clock;
    private readonly bool _allowUnconfined;

    private readonly ConcurrentDictionary<string, ServiceProcess> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _starting = new(1, 1);

    public ServicePluginHost(
        string root, IPluginSandbox sandbox, IPluginSecretSource secrets,
        IPluginObservationSink observations, IClock clock, bool allowUnconfined = false)
    {
        _root = root;
        _sandbox = sandbox;
        _secrets = secrets;
        _observations = observations;
        _clock = clock;
        _allowUnconfined = allowUnconfined;
    }

    /// <summary>How long to wait between restarts, growing with each consecutive failure.</summary>
    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))));

    // ---- invoking ----

    public async Task<PluginResult> InvokeAsync(
        PluginManifest manifest, PluginInvocation invocation, CancellationToken ct)
    {
        if (manifest.Service is null)
        {
            throw new PluginException($"{manifest.PluginId} is not a service plugin.");
        }

        var stopwatch = Stopwatch.StartNew();

        PluginServiceState state =
            await EnsureRunningAsync(manifest, invocation.NetworkGranted, ct).ConfigureAwait(false);

        if (state.Status != PluginServiceStatus.Ready
            || !_running.TryGetValue(manifest.PluginId, out ServiceProcess? service))
        {
            return new PluginResult(
                false, null, PluginRefusal.ServiceUnavailable,
                state.Detail ?? state.Status, stopwatch.ElapsedMilliseconds);
        }

        PluginCapability? capability = manifest.Capabilities
            .FirstOrDefault(c => string.Equals(c.Key, invocation.CapabilityKey, StringComparison.Ordinal));

        TimeSpan timeout = capability?.Timeout ?? TimeSpan.FromSeconds(30);

        try
        {
            return await service
                .CallAsync(invocation, capability, timeout, stopwatch, ct)
                .ConfigureAwait(false);
        }
        catch (Exception broken) when (broken is IOException or ObjectDisposedException
                                           or InvalidOperationException)
        {
            // The pipe went away mid-call. Whether the plugin acted before it died is exactly what
            // nobody knows, so this follows the same rule as a timeout.
            await StopAsync(manifest.PluginId, CancellationToken.None).ConfigureAwait(false);

            return Ambiguous(capability, "the service stopped mid-call", stopwatch);
        }
    }

    /// <summary>
    /// What a call whose answer never arrived is worth reporting as.
    /// </summary>
    /// <remarks>
    /// A read that timed out is a failure: nothing happened and asking again is free. A write that
    /// timed out is not — the message may be in the channel. Calling it failed invites a retry that
    /// sends it twice, and there is no way for Aurora to tell the difference from here, so it says
    /// so instead of guessing.
    /// </remarks>
    private static PluginResult Ambiguous(
        PluginCapability? capability, string detail, Stopwatch stopwatch) =>
        capability is { Effects.Count: > 0 }
            ? new PluginResult(
                false, null, PluginRefusal.AmbiguousOutcome,
                $"{detail}; it may or may not have happened", stopwatch.ElapsedMilliseconds)
            : new PluginResult(
                false, null, "timed_out", detail, stopwatch.ElapsedMilliseconds);

    // ---- supervising ----

    public async Task<PluginServiceState> EnsureRunningAsync(
        PluginManifest manifest, bool networkGranted, CancellationToken ct)
    {
        if (manifest.Service is null)
        {
            return new PluginServiceState(
                manifest.PluginId, PluginServiceStatus.Stopped, 0, "not a service plugin");
        }

        if (_running.TryGetValue(manifest.PluginId, out ServiceProcess? existing)
            && existing.IsReady)
        {
            return existing.State;
        }

        await _starting.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Checked again inside the gate: two calls arriving together must not start two.
            if (_running.TryGetValue(manifest.PluginId, out existing) && existing.IsReady)
            {
                return existing.State;
            }

            if (existing is { State.Status: PluginServiceStatus.Failed })
            {
                return existing.State;
            }

            if (existing is not null)
            {
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            return await StartAsync(manifest, networkGranted, existing?.Failures ?? 0, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _starting.Release();
        }
    }

    private async Task<PluginServiceState> StartAsync(
        PluginManifest manifest, bool networkGranted, int failures, CancellationToken ct)
    {
        PluginService declared = manifest.Service!;

        // Every secret the manifest names, or the service does not start. Starting one that is
        // going to fail its first call for want of a credential wastes a process and produces a
        // failure that reads like the plugin being broken.
        var secrets = new JsonObject();

        foreach (PluginSecretRequirement required in manifest.RequiredSecrets ?? [])
        {
            var value = await _secrets
                .FindAsync(manifest.PluginId, required.Name, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(value))
            {
                var state = new PluginServiceState(
                    manifest.PluginId, PluginServiceStatus.Failed, failures,
                    $"{PluginRefusal.SecretMissing}: no value for '{required.Name}'");

                _running[manifest.PluginId] = ServiceProcess.NeverStarted(state);
                return state;
            }

            secrets[required.Name] = value;
        }

        var directory = Path.Combine(_root, Slug(manifest.PluginId));
        var executable = Path.Combine(directory, declared.Executable);
        var working = Path.Combine(directory, "work");
        Directory.CreateDirectory(working);

        SandboxPlan plan = _sandbox.Plan(
            new SandboxRequest(manifest.PluginId, executable, working, networkGranted));

        if (plan.Level != SandboxLevel.Confined && !_allowUnconfined)
        {
            var state = new PluginServiceState(
                manifest.PluginId, PluginServiceStatus.Failed, failures,
                PluginRefusal.SandboxUnavailable);

            _running[manifest.PluginId] = ServiceProcess.NeverStarted(state);
            return state;
        }

        var service = new ServiceProcess(
            manifest, plan, executable, working, _observations, _clock, failures);

        _running[manifest.PluginId] = service;

        // The secrets go over the pipe in the opening frame rather than through the environment.
        // A child's environment is readable from outside the process on most systems and a pipe
        // held by two processes is not, so this is the cheaper of the two by a wide margin.
        PluginServiceState started =
            await service.StartAsync(secrets, declared.StartTimeout, ct).ConfigureAwait(false);

        return started;
    }

    public async Task StopAsync(string pluginId, CancellationToken ct)
    {
        if (_running.TryRemove(pluginId, out ServiceProcess? service))
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    public IReadOnlyList<PluginServiceState> Running() =>
        [.. _running.Values.Select(s => s.State)];

    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProcess service in _running.Values)
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }

        _running.Clear();
        _starting.Dispose();
    }

    private static string Slug(string pluginId) =>
        pluginId.Replace('/', '-').Replace('\\', '-');
}
