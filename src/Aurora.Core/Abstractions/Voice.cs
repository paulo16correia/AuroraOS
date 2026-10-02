using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Aurora's voice sessions, wherever they are happening (docs/adr/0073).
/// </summary>
/// <remarks>
/// One store for every channel, because the alternative is a phone session table and a Discord
/// session table that agree about nothing. The kill switch has to reach all of them, the
/// concurrency limit has to count all of them, and the audit has to correlate across them.
/// </remarks>
public interface IVoiceSessionStore
{
    /// <summary>Records a session that has been authorised and not yet connected.</summary>
    Task<VoiceSession> OpenAsync(VoiceSession session, CancellationToken ct);

    Task<VoiceSession?> FindAsync(string sessionId, CancellationToken ct);

    Task<VoiceSession> AdvanceAsync(
        string sessionId, VoiceSessionState state, string? reason, CancellationToken ct);

    /// <summary>
    /// Spends one of a session's tool calls, or reports that there are none left.
    /// </summary>
    /// <remarks>
    /// Check-and-spend in one step, for the same reason consent sessions do it that way: two tool
    /// requests arriving together must not both consume the last unit of a budget.
    /// </remarks>
    Task<VoiceBudgetUse> SpendToolCallAsync(string sessionId, CancellationToken ct);

    Task<IReadOnlyList<VoiceSession>> LiveAsync(CancellationToken ct);

    /// <summary>Ends every live session. The operator's stop.</summary>
    Task<int> EndAllAsync(string reason, CancellationToken ct);
}

/// <summary>Whether a tool call was charged to a session.</summary>
public sealed record VoiceBudgetUse(bool Spent, int Used, int Limit);

/// <summary>
/// Whether voice is switched on, and the limits that apply to all of it.
/// </summary>
/// <remarks>
/// Read on every decision rather than cached, so that turning voice off takes effect on the next
/// request rather than on the next restart. An operator reaching for a stop is not in a mood to
/// wait for a process to notice.
/// </remarks>
public interface IVoicePolicy
{
    Task<VoiceSettings> CurrentAsync(CancellationToken ct);

    /// <summary>Stops voice: no new sessions, no tool calls, and every live session ended.</summary>
    Task StopAsync(string actor, string reason, CancellationToken ct);

    Task ResumeAsync(string actor, CancellationToken ct);
}

/// <summary>What voice is allowed to do on this installation.</summary>
/// <param name="Stopped">The operator's switch. When true nothing runs and nothing starts.</param>
/// <param name="Enabled">
/// Whether voice runs on this installation at all. Off until somebody turns it on, which is not
/// the same switch as <paramref name="Stopped"/>: that one is an operator stopping something that
/// was running, this one is an installation nobody has decided about yet.
/// </param>
/// <param name="MaxConcurrentSessions">Across every channel, not per channel.</param>
/// <param name="MaxSessionDuration">
/// The ceiling any one session's grant may ask for. A grant asking for longer is refused rather
/// than quietly shortened, because a session that ends earlier than its grant says is a session
/// whose authority nobody can read off the record afterwards.
/// </param>
public sealed record VoiceSettings(
    bool Stopped,
    bool Enabled,
    int MaxConcurrentSessions,
    TimeSpan MaxSessionDuration)
{
    /// <summary>What an installation does before anybody configures it: nothing.</summary>
    /// <remarks>
    /// Off. An install that listened and spoke before its owner had decided it should is an
    /// install that made a decision on their behalf — and voice is the channel where that decision
    /// is least recoverable, because it is made in somebody's room with a microphone.
    /// </remarks>
    public static VoiceSettings Default { get; } = new(
        Stopped: false,
        Enabled: false,
        MaxConcurrentSessions: 2,
        MaxSessionDuration: TimeSpan.FromMinutes(15));
}

/// <summary>
/// Runs one voice tool request through the Kernel and hands back what actually happened.
/// </summary>
/// <remarks>
/// <b>This is the seam the whole design turns on, and the direction matters.</b>
/// <para>
/// A plugin cannot call into Aurora. There is no frame for it and there is not going to be one —
/// the plugin protocol is deliberately one-way, so that a process holding a connection to somewhere
/// else can report what happened and can never ask Aurora to do something. Adding a request frame
/// would hand every plugin a way to reach the Kernel, in order to give one of them a way.
/// </para>
/// <para>
/// So the loop runs the other way. The voice plugin <i>reports</i> that the interaction layer asked
/// for something — an observation, like any other. This runs inside Aurora, receives that
/// observation, checks the session's grant, submits the request to the Kernel under the session's
/// principal, and then <i>calls</i> the plugin back with the outcome, through the ordinary
/// capability path. The plugin never asks; Aurora decides and acts.
/// </para>
/// <para>
/// That is LAW-003's action-observation loop and LAW-007's event-mediated communication doing
/// exactly what they were written for, and it is why voice needed no change to the plugin protocol.
/// </para>
/// </remarks>
public interface IVoiceToolBridge
{
    Task<VoiceToolOutcome> RunAsync(VoiceToolContext request, CancellationToken ct);
}
