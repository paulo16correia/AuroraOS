using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Presence;

/// <summary>
/// Whether voice runs at all, and the stop that turns it off (docs/adr/0073).
/// </summary>
/// <remarks>
/// The settings come from configuration and do not change while Aurora runs; the stop is held in
/// memory and changes the moment somebody uses it. That split is deliberate. Reconfiguring what
/// voice may do is an edit somebody makes deliberately and restarts into. Stopping it is something
/// somebody does because a call is happening right now that should not be, and it has to take
/// effect on the next decision rather than the next restart.
/// <para>
/// Stopping also ends every live session. A stop that prevented new calls while leaving the current
/// one talking would be the wrong half of the job.
/// </para>
/// </remarks>
public sealed class VoicePolicyService : IVoicePolicy
{
    private readonly VoiceSettings _configured;
    private readonly IVoiceSessionStore _sessions;
    private readonly IAuditStore _audit;

    private volatile bool _stopped;

    public VoicePolicyService(
        VoiceSettings configured, IVoiceSessionStore sessions, IAuditStore audit)
    {
        _configured = configured;
        _sessions = sessions;
        _audit = audit;
    }

    public Task<VoiceSettings> CurrentAsync(CancellationToken ct) =>
        Task.FromResult(_configured with { Stopped = _stopped });

    public async Task StopAsync(string actor, string reason, CancellationToken ct)
    {
        // Set before the sessions are ended, not after. In the other order a call could start in
        // the gap between ending the list and closing the door.
        _stopped = true;

        var ended = await _sessions.EndAllAsync($"voice stopped: {reason}", ct).ConfigureAwait(false);

        await RecordAsync(
            "voice.stopped", $"actor={actor}; reason={reason}; sessions_ended={ended}", ct)
            .ConfigureAwait(false);
    }

    public async Task ResumeAsync(string actor, CancellationToken ct)
    {
        _stopped = false;

        await RecordAsync("voice.resumed", $"actor={actor}", ct).ConfigureAwait(false);
    }

    private async Task RecordAsync(string action, string detail, CancellationToken ct)
    {
        // Through the existing audit rather than a log of its own. Turning voice off and on is a
        // decision about what Aurora may do, and it belongs in the same chain as the others.
        await _audit.AppendAsync(
            new AuditEntry(
                PrincipalClientId: "operator",
                PrincipalOsUser: Environment.UserName,
                ActionId: action,
                InputHash: string.Empty,
                Outcome: "recorded",
                Reason: detail),
            ct)
            .ConfigureAwait(false);
    }
}
