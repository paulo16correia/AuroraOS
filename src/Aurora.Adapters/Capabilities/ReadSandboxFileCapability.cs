using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>
/// MEDIUM, approval-gated, read-only capability that reads a UTF-8 text file from the sandbox
/// (docs/adr/0010).
/// </summary>
/// <remarks>
/// Declares no effects, which is what makes it eligible for consent-session reuse: one approval
/// then covers further reads within the session, while a write still costs a fresh approval each
/// time. It is MEDIUM rather than LOW because reading arbitrary sandbox content is worth a human
/// decision at least once, even though repeating it changes nothing.
/// </remarks>
public sealed class ReadSandboxFileCapability : ICapability
{
    private const string InputSchemaJson =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string","minLength":1,"maxLength":512}}}""";

    private static readonly JsonElement SchemaElement =
        JsonDocument.Parse(InputSchemaJson).RootElement.Clone();

    private readonly ISandboxFileReader _reader;

    public ReadSandboxFileCapability(ISandboxFileReader reader) => _reader = reader;

    public CapabilityDescriptor Descriptor { get; } = new(
        ActionId: "files.read_sandbox",
        Title: "Read a file from the sandbox",
        Description: "Reads a UTF-8 text file from the Aurora sandbox directory. Requires approval.",
        InputSchema: SchemaElement,
        Effects: Array.Empty<string>(),
        Risk: RiskLevel.Medium,
        ApprovalRequired: true);

    public async ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        SandboxReadResult read = await _reader.ReadAsync(path, ct).ConfigureAwait(false);

        var obj = new JsonObject
        {
            ["path"] = read.Path,
            ["content"] = read.Content,
            ["bytes"] = read.Bytes,
        };
        return JsonSerializer.SerializeToElement(obj);
    }
}
