using Aurora.Core.Abstractions;

namespace Aurora.Tests.Support;

/// <summary>
/// A local language model that answers exactly what a test says it answers.
/// </summary>
/// <remarks>
/// Deterministic on purpose. Everything worth pinning about the conversation boundary — whether an
/// expired window stops a call, whether a transcript reaches the model as data, whether an
/// oversized answer is refused — is a property of Aurora's side, and testing it against a real
/// model would make each of those assertions depend on what a particular set of weights felt like
/// saying that afternoon.
/// <para>
/// It also records what it was asked, which is how the injection tests check the shape of the
/// prompt rather than the model's willingness to resist one.
/// </para>
/// </remarks>
public sealed class ScriptedLanguageModel : ILocalLanguageModel
{
    public LanguageModelIdentity? Identity { get; set; } =
        new("test-runtime", "scripted", "deterministic");

    /// <summary>What it will answer. Set one of these, not both.</summary>
    public string? Answer { get; set; } = "Estou aqui.";

    public LanguageModelOutcome Outcome { get; set; } = LanguageModelOutcome.Answered;

    public string Detail { get; set; } = "answered";

    /// <summary>How long it pretends to take, so a timeout can be a timeout rather than a wait.</summary>
    public TimeSpan Takes { get; set; } = TimeSpan.Zero;

    /// <summary>Every request it received, in order.</summary>
    public List<LanguageModelRequest> Asked { get; } = [];

    public LanguageModelRequest? Last => Asked.Count == 0 ? null : Asked[^1];

    public Task<LanguageModelIdentity?> IdentifyAsync(CancellationToken ct) =>
        Task.FromResult(Identity);

    public async Task<LanguageModelAnswer> AnswerAsync(
        LanguageModelRequest request, CancellationToken ct)
    {
        Asked.Add(request);

        if (Takes > TimeSpan.Zero)
        {
            await Task.Delay(Takes, ct).ConfigureAwait(false);
        }

        var prompt = request.Instruction.Length
            + request.Conversation.Sum(turn => turn.Speaker.Length + turn.Said.Length);

        return Outcome == LanguageModelOutcome.Answered
            ? LanguageModelAnswer.Spoke(Answer ?? string.Empty, Identity, 1, prompt)
            : LanguageModelAnswer.Did(Outcome, Detail, 1, Identity);
    }
}
