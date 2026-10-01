namespace Aurora.Core.Abstractions;

/// <summary>
/// Turns "somebody spoke to Aurora" into "Aurora said something", or into a reason it did not
/// (docs/adr/0084).
/// </summary>
/// <remarks>
/// <b>The direction is the same one voice already runs in, and for the same reason.</b> The plugin
/// cannot call into Aurora; it <i>reports</i> that somebody addressed Aurora and that it would
/// answer. This runs inside Aurora, receives that report, decides whether an answer is allowed at
/// all, prepares the only context a model will see, asks it for a sentence, checks what comes
/// back, and then calls the plugin through the ordinary capability path. The plugin never asks
/// (docs/adr/0073).
/// <para>
/// Everything that makes this safe is on this side of the seam. The conversation window is the
/// authority to speak and is checked before a model is asked anything — an expired window means no
/// call, not a call whose answer is discarded. What people said is data and is never joined to
/// Aurora's instruction. The answer is validated before it can be spoken. And the only thing the
/// model's output can become is words in a voice channel: there is no branch here that reads it as
/// a command, a path, a tool call or an instruction, which is why it cannot be one.
/// </para>
/// </remarks>
public interface IVoiceConversationBoundary
{
    Task<VoiceAnswerOutcome> AnswerAsync(VoiceAnswerRequest request, CancellationToken ct);
}

/// <summary>
/// What the plugin reported: somebody addressed Aurora, and here is the conversation.
/// </summary>
/// <remarks>
/// Nothing in here is trusted. Not the transcript, not the speaker's name, and not the claim that
/// a conversation window is open — that is looked up rather than believed, the way a voice tool
/// request's session is.
/// </remarks>
public sealed record VoiceAnswerRequest(
    string PluginId,
    string GuildId,
    string ChannelId,
    /// <summary>The turns Aurora heard, oldest first. Untrusted content.</summary>
    IReadOnlyList<ConversationTurn> Conversation,
    /// <summary>Ties every event of one exchange together in the audit.</summary>
    string RequestId);

/// <summary>Why Aurora did not answer, when it did not.</summary>
public enum VoiceAnswerRefusal
{
    None,

    /// <summary>No conversation window is open, so nothing may be said (docs/adr/0074).</summary>
    NotConversing,

    /// <summary>There is nothing to answer.</summary>
    NothingSaid,

    /// <summary>What arrived was larger than Aurora will put to a model.</summary>
    ContextTooLarge,

    /// <summary>No local model is installed or reachable.</summary>
    ModelUnavailable,

    /// <summary>The model did not finish in time.</summary>
    ModelTimedOut,

    /// <summary>The attempt was withdrawn.</summary>
    Cancelled,

    /// <summary>The model produced nothing usable.</summary>
    AnswerUnusable,

    /// <summary>Aurora had a sentence and the voice path refused to say it.</summary>
    NotSpoken,
}

/// <summary>
/// What happened, in enough detail to tell a model that would not answer from a channel that would
/// not let Aurora speak.
/// </summary>
public sealed record VoiceAnswerOutcome(
    bool Answered,
    VoiceAnswerRefusal Refusal,
    /// <summary>What was said, when something was.</summary>
    string? Said,
    string Detail,
    LanguageModelIdentity? Model,
    long ModelDurationMs)
{
    public static VoiceAnswerOutcome Silent(VoiceAnswerRefusal refusal, string detail,
        LanguageModelIdentity? model = null, long modelDurationMs = 0) =>
        new(false, refusal, null, detail, model, modelDurationMs);

    public static VoiceAnswerOutcome Spoke(
        string text, LanguageModelIdentity? model, long modelDurationMs) =>
        new(true, VoiceAnswerRefusal.None, text, "spoke", model, modelDurationMs);
}

/// <summary>
/// The limits an answer is prepared and judged against.
/// </summary>
/// <remarks>
/// One place, because they are one decision: how much of a conversation is worth putting to a
/// model, how long somebody will wait to be answered, and how long Aurora may talk for. Scattered
/// through the code they drift apart, and the one that drifts is always the one nobody checks.
/// </remarks>
public sealed record VoiceConversationLimits(
    /// <summary>How many turns of history a model sees. Older ones are dropped, not summarised.</summary>
    int MaxTurns = 8,
    /// <summary>The whole conversation's size. Past it Aurora refuses rather than trimming blindly.</summary>
    int MaxContextCharacters = 4000,
    /// <summary>The longest thing Aurora will say in one turn.</summary>
    int MaxAnswerCharacters = 400,
    /// <summary>How long a sentence may take before the moment for it has passed.</summary>
    TimeSpan ModelTimeout = default)
{
    /// <summary>Ten seconds: past that, a spoken answer has missed the conversation it was for.</summary>
    public TimeSpan Timeout => ModelTimeout == default ? TimeSpan.FromSeconds(10) : ModelTimeout;

    public static VoiceConversationLimits Default { get; } = new();
}
