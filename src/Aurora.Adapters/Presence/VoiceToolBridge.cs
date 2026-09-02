using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using Aurora.Core.Presence;

namespace Aurora.Adapters.Presence;

/// <summary>
/// Turns one request from the voice interaction layer into one governed Aurora action
/// (docs/adr/0073).
/// </summary>
/// <remarks>
/// <b>The direction of this is the whole design.</b> A plugin cannot call into Aurora — the plugin
/// protocol is one-way on purpose, so a process holding a connection to somewhere else can report
/// what happened and can never ask Aurora to do something. Adding a request frame would hand that
/// ability to every plugin in order to give it to one.
/// <para>
/// So the voice plugin <i>reports</i> that the interaction layer asked for something. This runs
/// inside Aurora, receives that report, and decides. Nothing about the request is trusted: not the
/// action id, not the arguments, not the session it claims to belong to. The session is looked up
/// rather than taken from the message, which is what stops one call's request being decided against
/// another call's authority.
/// </para>
/// <para>
/// Then two gates, in order and both of them real. The session's own grant first — narrower than
/// the person who started the call, and refusing here means the Kernel is never asked. The Kernel
/// second, which is the same Kernel, the same policy and the same approval path as every other
/// action in Aurora. Nothing here can allow something the Kernel would refuse.
/// </para>
/// </remarks>
public sealed class VoiceToolBridge : IVoiceToolBridge
{
    private readonly IVoiceSessionStore _sessions;
    private readonly IVoicePolicy _policy;
    private readonly AuroraKernel _kernel;
    private readonly IClock _clock;
    private readonly Principal _principal;

    public VoiceToolBridge(
        IVoiceSessionStore sessions,
        IVoicePolicy policy,
        AuroraKernel kernel,
        IClock clock,
        Principal principal)
    {
        _sessions = sessions;
        _policy = policy;
        _kernel = kernel;
        _clock = clock;

        // The principal a voice session acts as. Not the caller: somebody on the telephone is not
        // a principal in Aurora, and treating them as one would make caller ID an authentication
        // mechanism. This is Aurora acting on its own account, bounded by the session's grant.
        _principal = principal;
    }

    public async Task<VoiceToolOutcome> RunAsync(VoiceToolContext request, CancellationToken ct)
    {
        VoiceSession? session = await _sessions.FindAsync(request.SessionId, ct).ConfigureAwait(false);

        if (session is null)
        {
            // A request naming a session Aurora does not hold. Refused without looking at anything
            // else in it, because the alternative is deciding an unknown request on some default.
            return Refused(request, VoiceRefusal.NotLive, "no such voice session");
        }

        VoiceSettings settings = await _policy.CurrentAsync(ct).ConfigureAwait(false);

        VoiceDecision decision = VoiceAuthorization.ForTool(
            session, request.ActionId, _clock.UtcNow, settings.Stopped);

        if (!decision.Allowed)
        {
            return Refused(request, decision.Refusal!, decision.Detail!);
        }

        // Charged before it runs, and only if the budget had room. Charging afterwards would let a
        // session that spent its last call and failed try again indefinitely.
        VoiceBudgetUse spent = await _sessions
            .SpendToolCallAsync(session.SessionId, ct).ConfigureAwait(false);

        if (!spent.Spent)
        {
            return Refused(
                request, VoiceRefusal.BudgetSpent,
                $"this session has used its {spent.Limit} requests");
        }

        JsonElement input;

        try
        {
            input = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(request.InputJson) ? "{}" : request.InputJson)
                .RootElement.Clone();
        }
        catch (JsonException)
        {
            // The interaction layer produced arguments that are not JSON. Its problem to fix, and
            // said plainly rather than passed to the Kernel as something to validate.
            return new VoiceToolOutcome(
                request.RequestId, VoiceToolResult.Failed,
                Detail: "the arguments were not readable as JSON");
        }

        ExecuteResponse answer = await _kernel.ExecuteAsync(
            new ExecuteRequest(
                ActionId: request.ActionId,
                Input: input,

                // The correlation id, so every event of one call — the provider's, the session's,
                // each tool request — lands in the audit tied together.
                IdempotencyKey: request.RequestId),
            _principal,
            ct)
            .ConfigureAwait(false);

        return Translate(request, answer);
    }

    /// <summary>
    /// Turns the Kernel's answer into the four things the interaction layer may say.
    /// </summary>
    /// <remarks>
    /// The mapping matters more than it looks. A model asked to narrate an outcome will narrate a
    /// plausible one unless it is handed an unambiguous one, and the case that matters is anything
    /// sent, booked or changed — "I've sent it", said about a request whose outcome is unknown, is
    /// a lie the person on the call has no way to detect.
    /// </remarks>
    private static VoiceToolOutcome Translate(VoiceToolContext request, ExecuteResponse answer) =>
        answer.Status switch
        {
            ExecuteStatus.Completed => new VoiceToolOutcome(
                request.RequestId, VoiceToolResult.Completed,
                ResultJson: answer.Result?.GetRawText()),

            ExecuteStatus.Denied => new VoiceToolOutcome(
                request.RequestId, VoiceToolResult.Refused,
                Refusal: VoiceRefusal.KernelRefused,

                // The Kernel's own words, which say whether it needs an approval or was refused
                // outright. Those are different things to be told on a telephone.
                Detail: answer.Error?.Message),

            // In progress means a reservation this request no longer owns — it may be running. Not
            // a failure, and reporting it as one invites a retry that does the thing twice.
            ExecuteStatus.InProgress => new VoiceToolOutcome(
                request.RequestId, VoiceToolResult.Unknown,
                Detail: "this may already be happening"),

            _ => new VoiceToolOutcome(
                request.RequestId, VoiceToolResult.Failed,
                Detail: answer.Error?.Message ?? "it did not work"),
        };

    private static VoiceToolOutcome Refused(VoiceToolContext request, string refusal, string detail) =>
        new(request.RequestId, VoiceToolResult.Refused, Refusal: refusal, Detail: detail);
}
