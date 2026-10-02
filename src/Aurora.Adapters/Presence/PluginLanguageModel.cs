using System.Text.Json;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;

namespace Aurora.Adapters.Presence;

/// <summary>
/// The local model, reached through the voice plugin rather than from inside Aurora.
/// </summary>
/// <remarks>
/// <para>
/// <b>This supersedes the location <c>docs/adr/0084</c> chose, and not the seam it defined.</b> That
/// record says the model "sits on Aurora's side of the seam", and justifies it by the graphics
/// card: the plugin is confined and denied the GPU, so a model held by a plugin could not use it.
/// The reasoning does not reach the conclusion. The model runs in Ollama's process either way —
/// neither Aurora nor the plugin loads it — so which of them makes the HTTP call has nothing to do
/// with who holds the card.
/// </para>
/// <para>
/// What it does decide is whether Aurora's own process opens a connection. It does not, and
/// <c>LocalOnlyTests</c> fails the build over an <c>HttpClient</c> for that reason. Implementing
/// this inside Aurora would have meant carving out a second exception beside the loopback liveness
/// probe, for an endpoint that is configurable — and configurable is exactly what that rule is
/// about. It would also have meant two implementations of "ask the model": this one and the voice
/// plugin's, which already does it, already streams, and is already tested.
/// </para>
/// <para>
/// The seam itself is untouched and says so: "deliberately unaware of any runtime. Whether the
/// answer comes from a process on a loopback port, an in-process library or a stub in a test is a
/// deployment decision." A plugin is a deployment decision.
/// </para>
/// <para>
/// <b>Nothing the model says arrives here as anything but text.</b> The capability returns a
/// sentence and a model name; there is no branch below that reads either as a path, a command or an
/// instruction, and the plugin is given no tools through this path at all.
/// </para>
/// </remarks>
public sealed class PluginLanguageModel : ILocalLanguageModel
{
    /// <summary>The plugin that holds the connection to the model.</summary>
    /// <remarks>
    /// Named rather than discovered. "Whichever plugin offers something called answer" is how a
    /// plugin gets to volunteer itself as Aurora's reasoning, and the installation is the owner's
    /// decision rather than a race between manifests.
    /// </remarks>
    public const string Plugin = "voice";

    private readonly AuroraKernel _kernel;
    private readonly Principal _principal;

    public PluginLanguageModel(AuroraKernel kernel, Principal principal)
    {
        _kernel = kernel;
        _principal = principal;
    }

    public async Task<LanguageModelIdentity?> IdentifyAsync(CancellationToken ct)
    {
        ExecuteResponse answered = await _kernel.ExecuteAsync(
            new ExecuteRequest(
                ActionId: $"{Plugin}.model",
                Input: JsonSerializer.SerializeToElement(new { })),
            _principal,
            ct)
            .ConfigureAwait(false);

        if (answered.Status != ExecuteStatus.Completed || answered.Result is not { } result
            || result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The plugin answers "not available" as a result rather than a failure, because a runtime
        // that is not running is a fact about the machine and not an error in asking.
        if (!Flag(result, "available"))
        {
            return null;
        }

        var runtime = Text(result, "runtime");
        var model = Text(result, "model");

        if (string.IsNullOrWhiteSpace(runtime) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return new LanguageModelIdentity(runtime, model, Text(result, "revision"));
    }

    public async Task<LanguageModelAnswer> AnswerAsync(
        LanguageModelRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Cancelled, "withdrawn before asking");
        }

        // Measured here rather than taken from the plugin. What the contract asks for is how long
        // the caller waited, which includes the Kernel, the pipe and the plugin — a number the
        // plugin cannot see.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var asked = request.Instruction.Length + request.Conversation.Sum(turn => turn.Said.Length);

        // The instruction and the turns go as separate fields and stay separate all the way to the
        // model. Joining them here would undo the one property the seam exists to hold.
        var input = JsonSerializer.SerializeToElement(new
        {
            instruction = request.Instruction,
            conversation = request.Conversation
                .Select(turn => new { speaker = turn.Speaker, said = turn.Said })
                .ToArray(),
            max_characters = request.MaxAnswerCharacters,
            timeout_seconds = Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds)),
        });

        ExecuteResponse answered;

        try
        {
            answered = await _kernel.ExecuteAsync(
                new ExecuteRequest(ActionId: $"{Plugin}.answer", Input: input), _principal, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Cancelled, "withdrawn while waiting", clock.ElapsedMilliseconds);
        }
        finally
        {
            clock.Stop();
        }

        if (answered.Status != ExecuteStatus.Completed)
        {
            // Never thrown, because the contract says so: an unreachable runtime, a timeout and a
            // refusal are all results a caller can act on. Which one it was comes from the plugin's
            // own refusal code rather than from guessing.
            return LanguageModelAnswer.Did(
                Refused(answered),
                Detail(answered) ?? $"the call did not complete ({answered.Status})",
                clock.ElapsedMilliseconds);
        }

        if (answered.Result is not { } result || result.ValueKind != JsonValueKind.Object)
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Malformed, "the plugin answered with no sentence",
                clock.ElapsedMilliseconds);
        }

        // What happened, in the plugin's own word for it. Read rather than guessed at: the Kernel
        // answers a failed execution with "Execution failed." and keeps the reason in the audit, so
        // anything a caller needs to tell apart has to come back as a result.
        LanguageModelOutcome outcome = Outcome(Text(result, "outcome"));
        var text = Text(result, "text");

        if (outcome != LanguageModelOutcome.Answered)
        {
            return LanguageModelAnswer.Did(
                outcome,
                Text(result, "detail") ?? "the model did not answer",
                clock.ElapsedMilliseconds,
                Identity(result));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Malformed, "the plugin answered with no sentence",
                clock.ElapsedMilliseconds, Identity(result));
        }

        // Checked here as well as in the plugin. Two bounds on the same thing, because the one that
        // matters is the one on the side that will speak it.
        if (text.Length > request.MaxAnswerCharacters)
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.TooLong,
                $"the model answered with {text.Length} characters and the limit was "
                + $"{request.MaxAnswerCharacters}",
                clock.ElapsedMilliseconds, Identity(result));
        }

        return LanguageModelAnswer.Spoke(text, Identity(result), clock.ElapsedMilliseconds, asked);
    }

    /// <summary>What the plugin said about itself, for the record of what actually answered.</summary>
    private static LanguageModelIdentity? Identity(JsonElement result)
    {
        var model = Text(result, "model");

        return string.IsNullOrWhiteSpace(model) ? null : new LanguageModelIdentity("ollama", model);
    }

    /// <summary>What the plugin said happened, in the words the contract uses.</summary>
    /// <remarks>
    /// An unrecognised word is <see cref="LanguageModelOutcome.Failed"/> rather than a guess. A
    /// plugin inventing an outcome Aurora does not know is a plugin Aurora should not act on.
    /// </remarks>
    private static LanguageModelOutcome Outcome(string? said) => said switch
    {
        "answered" => LanguageModelOutcome.Answered,
        "unavailable" => LanguageModelOutcome.Unavailable,
        "timed_out" => LanguageModelOutcome.TimedOut,
        "too_long" => LanguageModelOutcome.TooLong,
        "malformed" => LanguageModelOutcome.Malformed,
        _ => LanguageModelOutcome.Failed,
    };

    /// <summary>
    /// A capability that did not complete.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse, because this is all there is to go on. A failed execution comes back as
    /// <c>"Execution failed."</c> with the reason kept in the audit, by design — so anything worth
    /// telling apart travels as a result instead, and arrives through <see cref="Outcome"/>. What is
    /// left here is the difference between Aurora refusing the call and the call breaking.
    /// </remarks>
    private static LanguageModelOutcome Refused(ExecuteResponse answered) =>
        answered.Status == ExecuteStatus.Denied || answered.Status == ExecuteStatus.Asked
            ? LanguageModelOutcome.Unavailable
            : LanguageModelOutcome.Failed;

    private static string? Detail(ExecuteResponse answered) => answered.Error?.Message;

    private static string? Text(JsonElement result, string name) =>
        result.TryGetProperty(name, out JsonElement found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static bool Flag(JsonElement result, string name) =>
        result.TryGetProperty(name, out JsonElement found)
        && found.ValueKind is JsonValueKind.True;
}
