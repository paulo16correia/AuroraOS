using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Presence;

namespace Aurora.Adapters.Presence;

/// <summary>
/// Drives a voice conversation: the part that decides, on Aurora's side of the pipe
/// (docs/adr/0073).
/// </summary>
/// <remarks>
/// The plugin carries words. This decides what they mean and what may be done about them, and it
/// runs inside Aurora because that is where the Kernel is. Between them the direction never
/// reverses: the plugin reports and Aurora acts, so nothing outside this process ever asks Aurora
/// for anything.
/// <para>
/// Everything it needs already existed and none of it is duplicated here. Sessions are
/// <see cref="IVoiceSessionStore"/>. Whether voice runs at all is <see cref="IVoicePolicy"/>.
/// Whether a request may proceed is <see cref="VoiceAuthorization"/> and then the Kernel, both
/// through <see cref="IVoiceToolBridge"/>. Who Aurora is, is
/// <see cref="VoiceIdentity"/> reading the personality profile. What this adds is the sequence.
/// </para>
/// <para>
/// <b>Pumped rather than pushed.</b> The plugin queues what the interaction layer said and Aurora
/// drains it, which is how the Discord plugin's pending turns already work — the one voice design
/// in this repository that has met a real service. A callback would need the plugin to call into
/// Aurora, which is the thing the plugin protocol exists to prevent.
/// </para>
/// </remarks>
public sealed class VoiceRuntime
{
    private readonly IVoiceSessionStore _sessions;
    private readonly IVoicePolicy _policy;
    private readonly IVoiceToolBridge _bridge;
    private readonly IPluginRegistry _plugins;
    private readonly IPersonalityService _personality;
    private readonly IClock _clock;
    private readonly Principal _principal;

    public VoiceRuntime(
        IVoiceSessionStore sessions,
        IVoicePolicy policy,
        IVoiceToolBridge bridge,
        IPluginRegistry plugins,
        IPersonalityService personality,
        IClock clock,
        Principal principal)
    {
        _sessions = sessions;
        _policy = policy;
        _bridge = bridge;
        _plugins = plugins;
        _personality = personality;
        _clock = clock;
        _principal = principal;
    }

    private const string PluginId = "plugin/voice";

    /// <summary>
    /// Answers a call somebody made: validates the provider's event, then decides whether there is
    /// to be a conversation at all.
    /// </summary>
    /// <remarks>
    /// The order matters. The event is validated by the plugin — signature, freshness, replay —
    /// before Aurora reads anything in it, and Aurora decides about the session before anything is
    /// started. A provider event is a claim that a telephone rang, not an instruction to answer.
    /// </remarks>
    public async Task<VoiceOutcome> AnswerAsync(
        VoiceInboundEvent inbound, VoiceGrant grant, CancellationToken ct)
    {
        VoiceSettings settings = await _policy.CurrentAsync(ct).ConfigureAwait(false);

        if (settings.Stopped)
        {
            return VoiceOutcome.Refused(VoiceRefusal.VoiceStopped, "voice is stopped");
        }

        if (!settings.InboundEnabled)
        {
            // Off until somebody turns it on. An installation that answered the telephone before
            // its owner decided it should is one that decided on their behalf.
            return VoiceOutcome.Refused(
                VoiceRefusal.NotInGrant, "this installation does not answer calls");
        }

        IReadOnlyList<VoiceSession> live = await _sessions.LiveAsync(ct).ConfigureAwait(false);

        if (live.Count >= settings.MaxConcurrentSessions)
        {
            return VoiceOutcome.Refused(
                VoiceRefusal.BudgetSpent,
                $"{live.Count} voice sessions are already live");
        }

        PluginResult validated = await CallAsync(
            "voice.inbound",
            new
            {
                form = inbound.Form,
                signature = inbound.Signature,
                url = inbound.Url,
                event_id = inbound.EventId,
                timestamp = inbound.Timestamp,
            },
            ct)
            .ConfigureAwait(false);

        if (!validated.Ok)
        {
            // A provider event that does not verify is not a call. Nothing is created and nothing
            // is answered.
            return VoiceOutcome.Refused(
                validated.Refusal ?? "voice_bad_event", validated.Detail ?? "the event was refused");
        }

        JsonNode details = JsonNode.Parse(validated.OutputJson!)!;
        var externalRef = details["external_ref"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");

        VoiceSession? existing = await _sessions
            .FindByExternalAsync("phone", externalRef, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            // The provider delivered the same event twice, which is ordinary. Resumed rather than
            // duplicated: a second session would mean a second budget for one call.
            return VoiceOutcome.Resumed(existing);
        }

        var session = new VoiceSession(
            SessionId: Guid.NewGuid().ToString("N"),
            Channel: VoiceChannel.Phone,
            Provider: "phone",
            Direction: VoiceCallDirection.Inbound,
            Participant: new VoiceParticipant(
                details["claimed_from"]?.GetValue<string>() ?? "unknown",
                Verification: ParticipantVerification.ChannelAsserted),
            Grant: grant,
            State: VoiceSessionState.Connecting,
            StartedAtUtc: _clock.UtcNow.ToString("O"),
            CorrelationId: Guid.NewGuid().ToString("N"),
            ExternalRef: externalRef);

        await _sessions.OpenAsync(session, ct).ConfigureAwait(false);

        return await StartAsync(session, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Places a call Aurora decided to make, which needs a reason somebody approved.
    /// </summary>
    public async Task<VoiceOutcome> CallAsync(
        OutboundCallIntent intent, string fromNumber, CancellationToken ct)
    {
        VoiceSettings settings = await _policy.CurrentAsync(ct).ConfigureAwait(false);
        IReadOnlyList<VoiceSession> live = await _sessions.LiveAsync(ct).ConfigureAwait(false);

        // The whole outbound rule, in the one place that already held it. A mission may have
        // produced a goal and a planner a task; neither arrives here with an approval, and neither
        // gets a call.
        VoiceDecision decision = VoiceAuthorization.ForOutboundCall(
            intent, _clock.UtcNow, settings.Stopped, settings.OutboundEnabled,
            settings.AllowedDestinations, live.Count, settings.MaxConcurrentSessions);

        if (!decision.Allowed)
        {
            return VoiceOutcome.Refused(decision.Refusal!, decision.Detail!);
        }

        var session = new VoiceSession(
            SessionId: Guid.NewGuid().ToString("N"),
            Channel: VoiceChannel.Phone,
            Provider: "phone",
            Direction: VoiceCallDirection.Outbound,
            Participant: intent.Target,
            Grant: intent.Grant,
            State: VoiceSessionState.Connecting,
            StartedAtUtc: _clock.UtcNow.ToString("O"),
            CorrelationId: Guid.NewGuid().ToString("N"),
            Intent: intent);

        await _sessions.OpenAsync(session, ct).ConfigureAwait(false);

        PluginResult placed = await CallAsync(
            "voice.outbound",
            new { session_id = session.SessionId, to = intent.Target.Handle, from = fromNumber },
            ct)
            .ConfigureAwait(false);

        if (!placed.Ok)
        {
            await _sessions.AdvanceAsync(
                session.SessionId, VoiceSessionState.Failed, placed.Detail, ct).ConfigureAwait(false);

            return VoiceOutcome.Refused(
                placed.Refusal ?? "voice_provider_failed", placed.Detail ?? "the call was not placed");
        }

        return await StartAsync(session, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the interaction, with instructions composed from Aurora's own identity.
    /// </summary>
    private async Task<VoiceOutcome> StartAsync(VoiceSession session, CancellationToken ct)
    {
        ResolvedProfile profile = await _personality
            .ResolveAsync(_principal.ClientId, "voice", _clock.UtcNow, ct).ConfigureAwait(false);

        // Composed from the active personality, not written here. The whole reason a voice adapter
        // does not get to have opinions about who Aurora is.
        var instructions = VoiceIdentity.Compose(
            profile.Profile with { Voice = profile.EffectiveVoice },
            session,
            context: []);

        PluginResult started = await CallAsync(
            "voice.session.start",
            new
            {
                session_id = session.SessionId,
                instructions,
                tools = session.Grant.AllowedActions.Select(a => new
                {
                    type = "function",
                    name = a.Replace(".", "__", StringComparison.Ordinal),
                    description = "An Aurora capability. Aurora decides whether it runs.",
                    parameters = new { type = "object", properties = new { } },
                }),
                participant = new { handle = session.Participant.Handle },
                locale = profile.Locale,
            },
            ct)
            .ConfigureAwait(false);

        if (!started.Ok)
        {
            await _sessions.AdvanceAsync(
                session.SessionId, VoiceSessionState.Failed, started.Detail, ct).ConfigureAwait(false);

            // A layer that would not start is not a conversation, and saying otherwise would leave
            // a session in the store that nobody is on.
            return VoiceOutcome.Refused(
                started.Refusal ?? "voice_start_failed",
                started.Detail ?? "the interaction layer did not start");
        }

        VoiceSession active = await _sessions
            .AdvanceAsync(session.SessionId, VoiceSessionState.Active, null, ct).ConfigureAwait(false);

        return VoiceOutcome.Started(active);
    }

    /// <summary>
    /// Hands a slice of microphone audio to the speech layer.
    /// </summary>
    /// <remarks>
    /// Only the local stack needs this. With a remote interaction layer the audio travels between
    /// the telephone company and the provider and never enters this process at all; with the
    /// recogniser on this machine there is no telephone, so somebody has to carry the sound, and
    /// the only party allowed to call the plugin is Aurora.
    /// <para>
    /// What arrives is sound, and sound becomes words, and words are a request. Nothing about
    /// having been spoken aloud gives them standing — the capability the words ask for still goes
    /// through <see cref="PumpAsync"/>, the grant and the Kernel, exactly as before.
    /// </para>
    /// </remarks>
    public async Task<bool> ListenAsync(string sessionId, string base64Pcm16, CancellationToken ct)
    {
        VoiceSession? session = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);

        if (session is null || !session.IsLive)
        {
            // A session that is over is not listening. Checked here rather than in the plugin, so
            // that a stopped conversation stops hearing without depending on the plugin agreeing.
            return false;
        }

        PluginResult heard = await CallAsync(
            "voice.listen", new { session_id = sessionId, audio = base64Pcm16 }, ct)
            .ConfigureAwait(false);

        return heard.Ok;
    }

    /// <summary>
    /// Drains one round of what the interaction layer said, and answers what it asked for.
    /// </summary>
    /// <remarks>
    /// Every tool request goes through <see cref="IVoiceToolBridge"/>, which checks the session's
    /// grant and then the real Kernel. This method never executes anything itself, and there is no
    /// branch in which it invents an outcome — a request whose result it cannot obtain is answered
    /// with a failure, which the interaction layer is told to say plainly.
    /// </remarks>
    public async Task<VoicePump> PumpAsync(string sessionId, CancellationToken ct)
    {
        VoiceSession? session = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);

        if (session is null || !session.IsLive)
        {
            // A session that is over asks for nothing. Checked before the plugin is troubled, so a
            // stop takes effect on the next round rather than the next call.
            return new VoicePump(0, 0, Stopped: true);
        }

        PluginResult polled = await CallAsync(
            "voice.poll", new { session_id = sessionId }, ct).ConfigureAwait(false);

        if (!polled.Ok)
        {
            return new VoicePump(0, 0, Stopped: true);
        }

        JsonNode drained = JsonNode.Parse(polled.OutputJson!)!;
        JsonArray requests = drained["tool_requests"]?.AsArray() ?? [];

        // What Aurora said this round, for whoever is playing it. With a remote interaction layer
        // this is empty — the provider plays its own audio down the call — and with the local
        // stack it is the only way the sound gets out of the plugin.
        var spoken = (drained["audio"]?.AsArray() ?? [])
            .Select(a => a?.GetValue<string>())
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a => a!)
            .ToArray();

        var handled = 0;
        var refused = 0;

        foreach (JsonNode? request in requests)
        {
            if (request is null)
            {
                continue;
            }

            var requestId = request["request_id"]?.GetValue<string>() ?? string.Empty;
            var actionId = request["action_id"]?.GetValue<string>() ?? string.Empty;
            var input = request["input_json"]?.GetValue<string>() ?? "{}";

            VoiceToolOutcome outcome = await _bridge.RunAsync(
                new VoiceToolContext(sessionId, session.CorrelationId, requestId, actionId, input),
                ct)
                .ConfigureAwait(false);

            if (outcome.Outcome != VoiceToolResult.Completed)
            {
                refused++;
            }

            handled++;

            await CallAsync(
                "voice.tool_result",
                new
                {
                    session_id = sessionId,
                    request_id = requestId,
                    outcome = outcome.Outcome.ToString(),
                    result_json = outcome.ResultJson,
                    detail = outcome.Detail ?? outcome.Refusal,
                },
                ct)
                .ConfigureAwait(false);
        }

        return new VoicePump(handled, refused, Stopped: false, spoken);
    }

    /// <summary>Ends a session, in the store and on the wire.</summary>
    public async Task<VoiceSession?> HangUpAsync(
        string sessionId, string reason, CancellationToken ct)
    {
        await CallAsync("voice.hangup", new { session_id = sessionId, reason }, ct)
            .ConfigureAwait(false);

        VoiceSession? session = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);

        if (session is null || !session.IsLive)
        {
            return session;
        }

        return await _sessions
            .AdvanceAsync(sessionId, VoiceSessionState.Ended, reason, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops voice everywhere, and hangs up what is running.
    /// </summary>
    /// <remarks>
    /// The policy ends the sessions in the store; this also tells the plugin to let go of their
    /// transports, because a session marked cancelled while a telephone line is still open is only
    /// half a stop.
    /// </remarks>
    public async Task StopAsync(string actor, string reason, CancellationToken ct)
    {
        IReadOnlyList<VoiceSession> live = await _sessions.LiveAsync(ct).ConfigureAwait(false);

        await _policy.StopAsync(actor, reason, ct).ConfigureAwait(false);

        foreach (VoiceSession session in live)
        {
            await CallAsync(
                "voice.hangup", new { session_id = session.SessionId, reason }, ct)
                .ConfigureAwait(false);
        }
    }

    private Task<PluginResult> CallAsync(string capability, object input, CancellationToken ct) =>
        _plugins.InvokeAsync(
            new PluginInvocation(
                PluginId, capability, JsonSerializer.Serialize(input), Sensitivity.Private),
            ct);
}

/// <summary>A provider event, exactly as it arrived and not yet believed.</summary>
public sealed record VoiceInboundEvent(
    IReadOnlyDictionary<string, string> Form,
    string Signature,
    string Url,
    string EventId,
    string? Timestamp = null);

/// <summary>What happened when Aurora tried to start or resume a conversation.</summary>
public sealed record VoiceOutcome(
    VoiceSession? Session, bool IsNew, string? Refusal = null, string? Detail = null)
{
    /// <summary>A conversation that did not exist a moment ago and does now.</summary>
    public static VoiceOutcome Started(VoiceSession session) => new(session, true);

    /// <summary>
    /// The same call arriving twice. Resumed rather than duplicated: a second session would mean
    /// a second budget for one conversation.
    /// </summary>
    public static VoiceOutcome Resumed(VoiceSession session) => new(session, false);

    public static VoiceOutcome Refused(string refusal, string detail) =>
        new(null, false, refusal, detail);
}

/// <summary>One round of draining a conversation.</summary>
public sealed record VoicePump(
    int Handled, int Refused, bool Stopped, IReadOnlyList<string>? Audio = null)
{
    /// <summary>What Aurora said this round, as base64 PCM16 at 24 kHz mono.</summary>
    public IReadOnlyList<string> Audio { get; init; } = Audio ?? [];
}
