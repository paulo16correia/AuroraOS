using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// One running service plugin, and the conversation Aurora has with it.
/// </summary>
/// <remarks>
/// The protocol is JSON, one object per line, in both directions. Aurora writes calls and reads
/// whatever the plugin writes back: an answer to a call it made, or something the plugin says
/// happened on its own. The two are told apart by a <c>kind</c> field and never by ordering, so a
/// plugin that reports an event in the middle of a call does not corrupt the call.
/// <para>
/// Line-delimited rather than length-prefixed because a plugin author writes this in whatever
/// language they like, and <c>print(json.dumps(x))</c> is a protocol anybody can implement
/// correctly on the first try. A frame that is not valid JSON is dropped rather than reasoned
/// about.
/// </para>
/// </remarks>
internal sealed class ServiceProcess : IAsyncDisposable
{
    private readonly PluginManifest _manifest;
    private readonly SandboxPlan _plan;
    private readonly string _executable;
    private readonly string _working;
    private readonly IPluginObservationSink _observations;
    private readonly IClock _clock;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _waiting =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _writing = new(1, 1);

    private Process? _process;
    private Task? _reading;

    internal ServiceProcess(
        PluginManifest manifest, SandboxPlan plan, string executable, string working,
        IPluginObservationSink observations, IClock clock, int failures)
    {
        _manifest = manifest;
        _plan = plan;
        _executable = executable;
        _working = working;
        _observations = observations;
        _clock = clock;
        Failures = failures;

        State = new PluginServiceState(manifest.PluginId, PluginServiceStatus.Stopped, failures);
    }

    public PluginServiceState State { get; private set; }

    public int Failures { get; private set; }

    public bool IsReady =>
        State.Status == PluginServiceStatus.Ready && _process is { HasExited: false };

    /// <summary>A placeholder for a service that could not be started at all.</summary>
    internal static ServiceProcess NeverStarted(PluginServiceState state) =>
        new(state);

    private ServiceProcess(PluginServiceState state)
    {
        _manifest = null!;
        _plan = null!;
        _executable = string.Empty;
        _working = string.Empty;
        _observations = null!;
        _clock = null!;
        State = state;
        Failures = state.ConsecutiveFailures;
    }

    // ---- starting ----

    internal async Task<PluginServiceState> StartAsync(
        JsonObject secrets, TimeSpan startTimeout, CancellationToken ct)
    {
        State = State with { Status = PluginServiceStatus.Starting };

        var start = new ProcessStartInfo
        {
            FileName = _plan.FileName,
            WorkingDirectory = _working,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in _plan.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (_plan.FileName != _executable)
        {
            // Under a wrapper the plugin's own path is the wrapper's last argument. Unconfined,
            // the plan already names the plugin and adding it again would pass it to itself.
            start.ArgumentList.Add(_executable);
        }

        // The same cleared environment as a one-shot plugin. Nothing about the owner's shell
        // travels into a plugin, and no secret travels this way either.
        start.Environment.Clear();
        start.Environment["AURORA_PLUGIN_ID"] = _manifest.PluginId;
        start.Environment["AURORA_MODE"] = "service";
        start.Environment["PATH"] = OperatingSystem.IsWindows()
            ? "C:\\Windows\\System32"
            : "/usr/bin:/bin";

        try
        {
            _process = new Process { StartInfo = start };
            _process.Start();
        }
        catch (Exception cannotStart)
            when (cannotStart is System.ComponentModel.Win32Exception or IOException)
        {
            // Dropped, so nothing later asks an un-started Process whether it has exited — which
            // throws, and turns a clean "could not start" into an unhandled exception somewhere
            // else entirely.
            _process?.Dispose();
            _process = null;

            return Failed($"could not start: {cannotStart.GetType().Name}");
        }

        _reading = Task.Run(() => ReadAsync(_stopping.Token), CancellationToken.None);

        // Everything the plugin needs to exist, in one frame, before anything else is said.
        var ready = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting["@ready"] = ready;

        await WriteAsync(
            new JsonObject
            {
                ["kind"] = "hello",
                ["plugin_id"] = _manifest.PluginId,
                ["capabilities"] = new JsonArray(
                    [.. _manifest.Capabilities.Select(c => (JsonNode)c.Key!)]),
                ["endpoints"] = new JsonArray(
                    [.. _manifest.NetworkEndpoints.Select(e => (JsonNode)e!)]),
                ["secrets"] = secrets,
            },
            ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(startTimeout);

        try
        {
            await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _waiting.TryRemove("@ready", out _);
            return Failed($"did not report ready within {startTimeout.TotalSeconds:F0}s");
        }

        Failures = 0;
        State = new PluginServiceState(
            _manifest.PluginId, PluginServiceStatus.Ready, 0, null, Iso(_clock.UtcNow));

        return State;
    }

    private PluginServiceState Failed(string detail)
    {
        Failures++;

        var ceiling = _manifest?.Service?.MaxConsecutiveFailures ?? 5;

        // Past the ceiling it is held rather than started again. A service that will not stay up is
        // not fixed by starting it once more, and a restart loop nobody sees looks like working.
        State = new PluginServiceState(
            _manifest?.PluginId ?? "unknown",
            Failures >= ceiling ? PluginServiceStatus.Failed : PluginServiceStatus.Restarting,
            Failures,
            detail);

        return State;
    }

    // ---- calling ----

    internal async Task<PluginResult> CallAsync(
        PluginInvocation invocation, PluginCapability? capability, TimeSpan timeout,
        Stopwatch stopwatch, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;

        try
        {
            await WriteAsync(
                new JsonObject
                {
                    ["kind"] = "call",
                    ["id"] = id,
                    ["capability"] = invocation.CapabilityKey,
                    ["idempotency_key"] = invocation.IdempotencyKey,
                    ["input"] = JsonNode.Parse(invocation.InputJson),
                },
                ct).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            deadline.CancelAfter(timeout);

            JsonNode frame = await answer.Task.WaitAsync(deadline.Token).ConfigureAwait(false);

            return Interpret(frame, capability, stopwatch);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AmbiguousOrFailed(
                capability, $"no answer within {timeout.TotalSeconds:F0}s", stopwatch);
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    private static PluginResult AmbiguousOrFailed(
        PluginCapability? capability, string detail, Stopwatch stopwatch) =>
        capability is { Effects.Count: > 0 }
            ? new PluginResult(
                false, null, PluginRefusal.AmbiguousOutcome,
                $"{detail}; it may or may not have happened", stopwatch.ElapsedMilliseconds)
            : new PluginResult(false, null, "timed_out", detail, stopwatch.ElapsedMilliseconds);

    /// <summary>Turns the plugin's answer into a result, believing none of it about itself.</summary>
    private static PluginResult Interpret(
        JsonNode frame, PluginCapability? capability, Stopwatch stopwatch)
    {
        var ok = frame["ok"]?.GetValue<bool>() ?? false;
        var outcome = frame["outcome"]?.GetValue<string>();

        // A plugin is allowed to say it does not know. That is the whole reason the state exists,
        // and refusing to hear it would push authors towards guessing.
        if (string.Equals(outcome, PluginOutcome.Unknown, StringComparison.Ordinal))
        {
            return new PluginResult(
                false, null, PluginRefusal.AmbiguousOutcome,
                Detail(frame) ?? "the plugin reported an unknown outcome",
                stopwatch.ElapsedMilliseconds);
        }

        if (!ok)
        {
            return new PluginResult(
                false, null, frame["refusal"]?.GetValue<string>() ?? "plugin_failed",
                Detail(frame) ?? "no detail", stopwatch.ElapsedMilliseconds);
        }

        return new PluginResult(
            true, frame["output"]?.ToJsonString() ?? "{}", null,
            PluginOutcome.Completed, stopwatch.ElapsedMilliseconds);
    }

    private static string? Detail(JsonNode frame)
    {
        var detail = frame["detail"]?.GetValue<string>();

        // Truncated, because it is written by the plugin and travels into Aurora's records.
        return detail is null ? null : detail[..Math.Min(detail.Length, 500)];
    }

    // ---- reading ----

    private async Task ReadAsync(CancellationToken ct)
    {
        StreamReader? output = _process?.StandardOutput;

        if (output is null)
        {
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await output.ReadLineAsync(ct).ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                Dispatch(line, ct);
            }
        }
        catch (Exception ending) when (ending is OperationCanceledException or IOException
                                           or ObjectDisposedException)
        {
            // The process went away. Whoever is waiting finds out by their own deadline.
        }
        finally
        {
            // Nothing more will arrive, so nobody should wait out a full timeout for it.
            foreach (TaskCompletionSource<JsonNode> waiter in _waiting.Values)
            {
                waiter.TrySetCanceled(CancellationToken.None);
            }
        }
    }

    private void Dispatch(string line, CancellationToken ct)
    {
        JsonNode? frame;

        try
        {
            frame = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            // A plugin's stdout is not a structured stream by nature: a stray print, a warning from
            // an interpreter, a stack trace. Dropped rather than reasoned about.
            return;
        }

        if (frame is null)
        {
            return;
        }

        switch (frame["kind"]?.GetValue<string>())
        {
            case "ready":
                if (_waiting.TryRemove("@ready", out TaskCompletionSource<JsonNode>? ready))
                {
                    ready.TrySetResult(frame);
                }

                break;

            case "result":
                var id = frame["id"]?.GetValue<string>();

                if (id is not null
                    && _waiting.TryRemove(id, out TaskCompletionSource<JsonNode>? waiter))
                {
                    waiter.TrySetResult(frame);
                }

                break;

            case "event":
                _ = ObserveAsync(frame, ct);
                break;

            default:
                // An unknown kind is a plugin speaking a protocol Aurora does not know. Ignored,
                // because guessing at it is how a plugin gets to define its own contract.
                break;
        }
    }

    private async Task ObserveAsync(JsonNode frame, CancellationToken ct)
    {
        try
        {
            var kind = frame["type"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(kind))
            {
                return;
            }

            // Clamped to what the plugin was allowed to be handed. A plugin cannot label its own
            // output as more sensitive than the ceiling it was installed under, and cannot label it
            // as less to slip past a rule either — the ceiling is the ceiling.
            var declared = frame["sensitivity"]?.GetValue<string>() ?? _manifest.MaxDataClass;

            var sensitivity = Sensitivity.IsKnown(declared)
                && Sensitivity.Rank(declared) <= Sensitivity.Rank(_manifest.MaxDataClass)
                    ? declared
                    : _manifest.MaxDataClass;

            await _observations.ReceiveAsync(
                new PluginObservation(
                    _manifest.PluginId, kind,
                    frame["payload"]?.ToJsonString() ?? "{}", sensitivity),
                ct).ConfigureAwait(false);
        }
        catch (Exception ignored) when (ignored is not OperationCanceledException)
        {
            // A plugin's report failing to land is not a reason to lose the connection it came in
            // on. It is recorded where the sink records things and the service keeps running.
        }
    }

    private async Task WriteAsync(JsonObject frame, CancellationToken ct)
    {
        StreamWriter? input = _process?.StandardInput;

        if (input is null)
        {
            throw new IOException("the service has no input stream");
        }

        await _writing.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // One line, one object, and flushed: a frame sitting in a buffer is a call that never
            // happened as far as the plugin is concerned.
            await input.WriteLineAsync(frame.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    private static string Iso(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_process is { HasExited: false })
        {
            try
            {
                // Asked first, so a plugin holding a connection can close it politely.
                await WriteAsync(new JsonObject { ["kind"] = "shutdown" }, CancellationToken.None)
                    .ConfigureAwait(false);

                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token)
                    .ConfigureAwait(false);
            }
            catch (Exception unresponsive) when (unresponsive is not OutOfMemoryException)
            {
                // It had its chance.
            }
        }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception gone) when (gone is InvalidOperationException or NotSupportedException)
        {
            // Already dead.
        }

        _process?.Dispose();
        _process = null;

        State = State with { Status = PluginServiceStatus.Stopped };

        _stopping.Dispose();
        _writing.Dispose();
    }
}
