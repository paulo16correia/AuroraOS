using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aurora.Core.Abstractions;

namespace Aurora.Adapters.Reasoning;

/// <summary>
/// The local model, asked over loopback from Aurora's own process (docs/adr/0089).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the second answer to where the model lives, and the first one was wrong.</b>
/// <c>docs/adr/0084</c> put it on Aurora's side. <c>docs/adr/0087</c> moved it behind the plugin
/// protocol, on the grounds that 0084's reasoning — the graphics card — did not support its
/// conclusion. The reasoning really did not; the conclusion did, for a reason neither record gave:
/// <b>Windows refuses loopback to an AppContainer</b>, so a confined plugin cannot reach a model on
/// 127.0.0.1 at all. Measured, not deduced: <c>voice.model</c> through the plugin answered
/// "Ollama could not be reached at http://localhost:11434 (timed out)" while Ollama was serving.
/// </para>
/// <para>
/// So it is here, where there is no container in the way. The cost is the one
/// <c>LocalOnlyTests</c> exists to prevent, and it is paid narrowly: this file is the second named
/// exception to "nothing in Aurora opens an outbound connection", and it earns that the same way
/// the first one does — <b>it cannot reach another machine</b>. The address is checked at
/// construction and a non-loopback host is refused outright, so the only thing configuration can
/// move is which port on this machine answers.
/// </para>
/// <para>
/// <b>What it is not.</b> No tools, no credentials, no retries that could turn one question into
/// three, and no streaming: the caller wants one sentence inside a timeout it stated, and a partial
/// sentence is not a shorter answer, it is a different one. Everything it is told arrives in
/// <see cref="LanguageModelRequest"/> and everything it produces leaves as text.
/// </para>
/// </remarks>
public sealed class OllamaLanguageModel : ILocalLanguageModel, IDisposable
{
    /// <summary>Hosts that are this machine, and the only ones this will talk to.</summary>
    /// <remarks>
    /// The whole justification for this file existing rests on this list. A configurable host would
    /// make "Aurora reaches nothing" a statement about today's configuration rather than about the
    /// code, which is the difference between an invariant and a hope.
    /// </remarks>
    private static readonly string[] ThisMachine = ["127.0.0.1", "localhost", "::1", "[::1]"];

    private readonly HttpClient _http;
    private readonly string _model;

    /// <param name="endpoint">Where the runtime answers. Must be a loopback address.</param>
    /// <param name="model">The model to ask for, as the runtime names it.</param>
    public OllamaLanguageModel(string endpoint, string model)
    {
        var uri = new Uri(Loopback(endpoint), UriKind.Absolute);

        // No proxy. A loopback address sent through a company proxy is a leak rather than a
        // connection, and on some machines the system proxy applies to 127.0.0.1 unless refused.
        _http = new HttpClient(new HttpClientHandler { UseProxy = false, Proxy = null })
        {
            BaseAddress = uri,

            // Long, because the caller's own timeout is the one that matters and it is shorter.
            // This is the ceiling that stops a hung runtime holding a thread for ever.
            Timeout = TimeSpan.FromMinutes(5),
        };

        _model = model;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// The address, if it is on this machine. Refused outright if it is not.
    /// </summary>
    /// <remarks>
    /// Thrown rather than defaulted. An installation that configured a model on another host has
    /// said something Aurora will not do, and quietly using a different address instead would be
    /// answering a question nobody asked.
    /// </remarks>
    public static string Loopback(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.TrimEnd('/'), UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException(
                $"'{endpoint}' is not an address Aurora can read.", nameof(endpoint));
        }

        if (!ThisMachine.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Aurora will only ask a model on this machine, and '{uri.Host}' is not. "
                + "Aurora opens no connection that could reach another machine; a model elsewhere "
                + "would have to be reached by something that is allowed to.",
                nameof(endpoint));
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public async Task<LanguageModelIdentity?> IdentifyAsync(CancellationToken ct)
    {
        // What the runtime is holding, asked of the runtime. A name from a settings file says what
        // somebody intended to run, and a runtime that has unloaded its model is not ready — which
        // is worth a question rather than a conversation's worth of silence (docs/adr/0084).
        try
        {
            Tags? tags = await _http.GetFromJsonAsync<Tags>("/api/tags", ct).ConfigureAwait(false);

            TagEntry? found = tags?.Models?.FirstOrDefault(
                m => string.Equals(m.Name, _model, StringComparison.Ordinal));

            return found is null
                ? null
                : new LanguageModelIdentity("ollama", _model, found.Details?.Quantization);
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException
                                                or TaskCanceledException or JsonException)
        {
            // Null rather than an exception: "no model is available" is a fact about the machine
            // and not an error in asking.
            return null;
        }
    }

    /// <summary>
    /// Asks the runtime to load the model and hold it, so the first question is not the one that
    /// pays for it. Returns whether it is now ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on this machine: a cold llama3.1:8b takes <b>54 seconds</b> to answer and 0.3 to 0.5
    /// seconds once loaded. The conversation boundary waits ten seconds. So without this, the first
    /// thing anybody says after a restart is answered with silence — the request times out, the load
    /// it started completes anyway, and the <i>second</i> question works. A system that only talks
    /// to people who ask twice is not one that holds a conversation.
    /// </para>
    /// <para>
    /// <c>docs/adr/0084</c> saw this coming and put the job on <see cref="IdentifyAsync"/>, which
    /// cannot do it: listing what is installed says nothing about what is loaded. Separate and
    /// explicit, because loading four gigabytes is not a side effect to hide inside a question about
    /// names.
    /// </para>
    /// </remarks>
    public async Task<bool> PrepareAsync(CancellationToken ct)
    {
        try
        {
            // An empty prompt loads the model and generates nothing. keep_alive holds it, so a quiet
            // half-hour does not undo the wait.
            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                "/api/generate",
                new Load(_model, string.Empty, KeepAlive: "30m"),
                ct)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException
                                               or TaskCanceledException or OperationCanceledException)
        {
            // Not a failure worth propagating: the runtime being absent is a fact about the machine,
            // and the first real question will say so in words the caller can use.
            return false;
        }
    }

    public async Task<LanguageModelAnswer> AnswerAsync(
        LanguageModelRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Cancelled, "withdrawn before asking");
        }

        var asked = request.Instruction.Length + request.Conversation.Sum(t => t.Said.Length);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // The instruction is Aurora's and the only instruction here. What people said travels as
        // separate messages, each with its speaker beside it, and is never concatenated into the
        // system prompt — a model can tell a quoted turn from an order only if the structure
        // survives as far as it (docs/adr/0084).
        var messages = new List<Message> { new("system", request.Instruction, null) };

        messages.AddRange(request.Conversation.Select(
            turn => new Message("user", turn.Said, Named(turn.Speaker))));

        // No tools, and no field in which the model could ask for one. A sentence is the only thing
        // it is able to produce.
        var body = new ChatRequest(
            _model,
            messages,
            Stream: false,
            new Options(
                Temperature: 0.6,

                // Characters are the caller's unit and tokens are the runtime's. Three to one is
                // crude and errs small, and the answer is checked against the real limit below
                // either way.
                NumPredict: Math.Max(32, request.MaxAnswerCharacters / 3)));

        using var withTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        withTimeout.CancelAfter(request.Timeout);

        ChatResponse? answered;

        try
        {
            using HttpResponseMessage response = await _http
                .PostAsJsonAsync("/api/chat", body, withTimeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return LanguageModelAnswer.Did(
                    LanguageModelOutcome.Failed,
                    $"the model refused the request ({(int)response.StatusCode})",
                    clock.ElapsedMilliseconds);
            }

            answered = await response.Content
                .ReadFromJsonAsync<ChatResponse>(withTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Cancelled, "withdrawn while waiting", clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // The caller's own ceiling. Named as a timeout rather than a failure, because a model
            // that is merely slow and a model that is broken need different things done about them.
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.TimedOut,
                $"the model did not answer within {request.Timeout.TotalSeconds:0}s",
                clock.ElapsedMilliseconds);
        }
        catch (HttpRequestException unreachable)
        {
            // The commonest failure by far, and the one worth a clear sentence: the runtime is not
            // running, or was never installed.
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Unavailable,
                $"the model runtime could not be reached at {_http.BaseAddress} "
                + $"({unreachable.Message})",
                clock.ElapsedMilliseconds);
        }
        catch (JsonException unreadable)
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Malformed,
                $"the runtime answered with something unreadable ({unreadable.Message})",
                clock.ElapsedMilliseconds);
        }

        var said = answered?.Message?.Content?.Trim();
        var identity = new LanguageModelIdentity("ollama", _model);

        if (string.IsNullOrEmpty(said))
        {
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.Malformed, "the model answered with nothing",
                clock.ElapsedMilliseconds, identity);
        }

        if (said.Length > request.MaxAnswerCharacters)
        {
            // Refused rather than truncated. Half a sentence spoken aloud is worse than silence,
            // and the caller said what it could use.
            return LanguageModelAnswer.Did(
                LanguageModelOutcome.TooLong,
                $"the model answered with {said.Length} characters and the limit was "
                + $"{request.MaxAnswerCharacters}",
                clock.ElapsedMilliseconds, identity);
        }

        return LanguageModelAnswer.Spoke(said, identity, clock.ElapsedMilliseconds, asked);
    }

    /// <summary>
    /// A speaker's name in the shape the chat API accepts, or none.
    /// </summary>
    /// <remarks>
    /// Sent as the message's own field rather than folded into what they said. Trimmed because the
    /// runtime rejects an over-long name and losing the turn over a label would be losing the
    /// sentence to keep the signature.
    /// </remarks>
    private static string? Named(string speaker) =>
        string.IsNullOrWhiteSpace(speaker) ? null : speaker.Trim()[..Math.Min(speaker.Trim().Length, 64)];

    // ---- the runtime's own shapes, named here so nothing else has to know them ----

    private sealed record Message(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record Options(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<Message> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] Options Options);

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")] Message? Message);

    private sealed record Load(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("keep_alive")] string KeepAlive);

    private sealed record TagDetails(
        [property: JsonPropertyName("quantization_level")] string? Quantization);

    private sealed record TagEntry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("details")] TagDetails? Details);

    private sealed record Tags(
        [property: JsonPropertyName("models")] IReadOnlyList<TagEntry>? Models);
}
