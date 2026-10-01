namespace Aurora.Core.Abstractions;

/// <summary>
/// A language model running on this machine, asked one bounded question at a time
/// (docs/adr/0084).
/// </summary>
/// <remarks>
/// <b>This is a generator of sentences and nothing else.</b> It has no tools, no credentials, no
/// filesystem, no network of its own and no way to reach the Kernel. It cannot write memory,
/// goals, tasks, missions or beliefs. Everything it is told arrives in
/// <see cref="LanguageModelRequest"/>, and everything it produces leaves as text that Aurora then
/// decides what to do with. Those are not warnings about how to use it — they are the shape of the
/// interface: there is no parameter through which authority could arrive and no return value
/// through which it could be claimed.
/// <para>
/// Deliberately unaware of any runtime. Whether the answer comes from a process on a loopback
/// port, an in-process library or a stub in a test is a deployment decision, and coupling the
/// contract to one of them is how a "local model" quietly becomes one vendor's client. What the
/// contract does insist on is that the caller says how long it may take and how much it may say,
/// because a conversation cannot wait indefinitely for a sentence and a spoken answer that runs
/// for pages is not an answer.
/// </para>
/// </remarks>
public interface ILocalLanguageModel
{
    /// <summary>
    /// Which model and runtime would answer, or null when none is available.
    /// </summary>
    /// <remarks>
    /// Asked rather than configured, and asked of the thing that will actually answer. A recorded
    /// model name that came from a settings file says what somebody intended to run, which is a
    /// different fact from what ran — and when a transcript is worth keeping, the second one is
    /// the one worth keeping.
    /// </remarks>
    Task<LanguageModelIdentity?> IdentifyAsync(CancellationToken ct);

    /// <summary>Answers one request, or says why it did not.</summary>
    /// <remarks>
    /// Never throws for an outcome the caller can act on: an unreachable runtime, a timeout, a
    /// cancellation and an oversized answer are all results, not exceptions. A caller that has to
    /// catch four exception types to hold a conversation will catch three.
    /// </remarks>
    Task<LanguageModelAnswer> AnswerAsync(LanguageModelRequest request, CancellationToken ct);
}

/// <summary>What answered, for the record.</summary>
public sealed record LanguageModelIdentity(
    /// <summary>The runtime that hosts the model, as it names itself.</summary>
    string Runtime,
    /// <summary>The model, as the runtime names it.</summary>
    string Model,
    /// <summary>A build, quantisation or digest, where the runtime offers one.</summary>
    string? Revision = null);

/// <summary>
/// One thing somebody said, as data.
/// </summary>
/// <remarks>
/// A speaker and what they said, kept apart. Flattening a conversation into one string is how
/// "ignore your instructions" stops being a sentence somebody spoke and starts being a line in the
/// prompt: a model reading a transcript can tell a quoted turn from an instruction only if the
/// structure survives as far as it (docs/adr/0084).
/// </remarks>
public sealed record ConversationTurn(string Speaker, string Said);

/// <summary>
/// One bounded question, with its limits stated rather than assumed.
/// </summary>
/// <remarks>
/// <see cref="Instruction"/> is Aurora's own words and the only thing in here that is an
/// instruction. <see cref="Conversation"/> is what people said: untrusted content, carried as data
/// and never concatenated into the instruction. An implementation that joins the two into one
/// string has undone the distinction, and the tests say so.
/// </remarks>
public sealed record LanguageModelRequest(
    string Instruction,
    IReadOnlyList<ConversationTurn> Conversation,
    /// <summary>The longest answer worth having. Beyond it the answer is refused, not truncated.</summary>
    int MaxAnswerCharacters,
    /// <summary>How long the caller will wait. Past it the answer is a timeout, not a hang.</summary>
    TimeSpan Timeout);

/// <summary>How an attempt to get a sentence ended.</summary>
public enum LanguageModelOutcome
{
    /// <summary>There is a sentence, and it is within its limits.</summary>
    Answered,

    /// <summary>No model is installed, or the runtime is not running.</summary>
    Unavailable,

    /// <summary>The model did not finish inside the caller's timeout.</summary>
    TimedOut,

    /// <summary>The caller withdrew the question.</summary>
    Cancelled,

    /// <summary>The runtime answered with something that is not a usable sentence.</summary>
    Malformed,

    /// <summary>There was an answer and it was longer than the caller said it could be.</summary>
    TooLong,

    /// <summary>The runtime failed in a way it could describe.</summary>
    Failed,
}

/// <summary>
/// What came back, with enough about the attempt to tell a slow model from a broken one.
/// </summary>
/// <remarks>
/// The counts are characters rather than tokens on purpose: tokens are the runtime's unit and
/// differ between models, and what a limit protects here — how long somebody waits to be answered,
/// and how long Aurora talks for — is measured in neither. Characters are comparable across every
/// runtime this will ever be pointed at.
/// </remarks>
public sealed record LanguageModelAnswer(
    LanguageModelOutcome Outcome,
    /// <summary>The sentence, present only when the outcome is <see cref="LanguageModelOutcome.Answered"/>.</summary>
    string? Text,
    /// <summary>What happened, in words, for a person reading a log.</summary>
    string Detail,
    LanguageModelIdentity? Identity,
    long DurationMs,
    int PromptCharacters,
    int AnswerCharacters)
{
    public static LanguageModelAnswer Spoke(
        string text, LanguageModelIdentity? identity, long durationMs, int promptCharacters) =>
        new(LanguageModelOutcome.Answered, text, "answered", identity, durationMs,
            promptCharacters, text.Length);

    public static LanguageModelAnswer Did(
        LanguageModelOutcome outcome, string detail, long durationMs = 0,
        LanguageModelIdentity? identity = null) =>
        new(outcome, null, detail, identity, durationMs, 0, 0);
}
