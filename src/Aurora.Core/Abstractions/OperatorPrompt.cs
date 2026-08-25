namespace Aurora.Core.Abstractions;

/// <summary>What the person said, or why they did not.</summary>
public sealed record OperatorAnswer(bool Answered, string? Value, string Detail);

/// <summary>
/// Asks the person directly, outside anything the agent can reach (docs/adr/0010, 0011).
/// </summary>
/// <remarks>
/// The open item from the consent-session work. An approval prompt that the agent composes is one
/// the agent can word to its own advantage; this asks in a window the operating system draws, from
/// arguments Aurora passed, in a process the agent has no handle on.
/// <para>
/// It does not prove a human is there — nothing available locally does. It proves the question was
/// not written by the thing being approved, and that the answer did not pass through it.
/// </para>
/// </remarks>
public interface IOperatorPrompt
{
    /// <summary>Whether this machine can show a prompt at all.</summary>
    bool IsAvailable { get; }

    Task<OperatorAnswer> AskAsync(
        string title, string question, bool secret, TimeSpan timeout, CancellationToken ct);

    /// <summary>Tells the person something without asking anything.</summary>
    Task NotifyAsync(string title, string message, CancellationToken ct);
}
