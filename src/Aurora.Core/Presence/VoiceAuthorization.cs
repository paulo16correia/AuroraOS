using Aurora.Core.Contracts;

namespace Aurora.Core.Presence;

/// <summary>
/// Whether a voice session may ask for something, decided before the Kernel is asked
/// (docs/adr/0073).
/// </summary>
/// <remarks>
/// This is the session's own ceiling and it runs <b>first</b>. The Kernel still decides afterwards,
/// against policy, approval and the principal — nothing here can allow something the Kernel would
/// refuse. What it can do is refuse things the Kernel might have allowed, because a voice session
/// carries less authority than the person who started it.
/// <para>
/// It is a pure function on purpose. The interesting cases are all refusals, and a refusal path
/// that can only be reached by holding a real telephone call in the right state is a refusal path
/// nobody tests. Every branch here is reachable from a unit test on any machine.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> There is no parameter for the participant's relationship,
/// none for what Aurora remembers about them, none for the mission that produced the session and
/// none for the planner's opinion. They are absent because they are not inputs to this decision,
/// and a parameter that existed would eventually be read.
/// </para>
/// </remarks>
public static class VoiceAuthorization
{
    /// <summary>
    /// Judges one tool request against the session that made it.
    /// </summary>
    /// <param name="session">The session as Aurora currently holds it.</param>
    /// <param name="actionId">What the interaction layer asked for.</param>
    /// <param name="nowUtc">The current time, passed in so expiry is testable.</param>
    /// <param name="voiceStopped">
    /// Whether an operator has stopped voice. Checked first, because a stop that could be
    /// out-argued by anything below it would not be a stop.
    /// </param>
    public static VoiceDecision ForTool(
        VoiceSession session, string actionId, DateTimeOffset nowUtc, bool voiceStopped)
    {
        if (voiceStopped)
        {
            return VoiceDecision.Refuse(
                VoiceRefusal.VoiceStopped,
                "voice is stopped; nothing on a call can start it again");
        }

        if (!session.IsLive)
        {
            return VoiceDecision.Refuse(
                VoiceRefusal.NotLive,
                $"the session is {session.State.ToString().ToLowerInvariant()}");
        }

        if (Expired(session, nowUtc))
        {
            // Both clocks: the grant's own deadline and the session's maximum duration. A call that
            // runs long is a call whose authority has quietly outlived the decision that granted
            // it, which is the same problem as an approval that never expires.
            return VoiceDecision.Refuse(
                VoiceRefusal.Expired, "this session's authority has expired");
        }

        if (session.ToolCallsUsed >= session.Grant.MaxToolCalls)
        {
            return VoiceDecision.Refuse(
                VoiceRefusal.BudgetSpent,
                $"this session has used its {session.Grant.MaxToolCalls} requests");
        }

        if (!session.Grant.Names(actionId))
        {
            // The refusal that carries the most weight. Somebody on the call asking for something
            // outside the grant is the ordinary case — an unknown caller asking Aurora to send an
            // email, a known one asking it to call somebody else — and the answer is the same
            // whoever they are and however they ask.
            return VoiceDecision.Refuse(
                VoiceRefusal.NotInGrant,
                $"this session may not ask for '{actionId}'");
        }

        return VoiceDecision.Allow();
    }

    private static bool Expired(VoiceSession session, DateTimeOffset nowUtc)
    {
        if (!InsideWindow(session.Grant.ExpiresAtUtc, nowUtc))
        {
            return true;
        }

        return DateTimeOffset.TryParse(
                session.StartedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTimeOffset started)
            && nowUtc - started > session.Grant.MaxDuration;
    }

    private static bool InsideWindow(string expiresAtUtc, DateTimeOffset nowUtc) =>
        // An unparseable deadline is treated as passed. A grant whose expiry cannot be read is one
        // whose limits are unknown, and the safe reading of an unknown limit is that it is reached.
        DateTimeOffset.TryParse(
            expiresAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTimeOffset expires)
        && nowUtc < expires;
}

/// <summary>Allowed, or refused with a reason somebody can act on.</summary>
public sealed record VoiceDecision(bool Allowed, string? Refusal = null, string? Detail = null)
{
    private static readonly VoiceDecision Yes = new(true);

    public static VoiceDecision Allow() => Yes;

    public static VoiceDecision Refuse(string refusal, string detail) => new(false, refusal, detail);
}
