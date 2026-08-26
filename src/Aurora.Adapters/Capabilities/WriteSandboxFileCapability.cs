using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>
/// MEDIUM, approval-gated capability that writes a UTF-8 text file inside the sandbox root
/// (docs/adr/0003). The first capability that touches the filesystem.
/// </summary>
public sealed class WriteSandboxFileCapability : ICapability
{
    private static readonly JsonElement SchemaElement =
        CapabilityInput.Object()
            .String("path", maxLength: 512, required: true, minLength: 1)
            .String("content", maxLength: 65536, required: true)
            .Build();

    private readonly ISandboxFileWriter _writer;

    public WriteSandboxFileCapability(ISandboxFileWriter writer)
    {
        _writer = writer;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        ActionId: "files.write_sandbox",
        Title: "Write a file in the sandbox",
        Description: "Writes a UTF-8 text file inside the Aurora sandbox directory. Requires approval.",
        InputSchema: SchemaElement,
        Effects: ["files.write"],
        Risk: RiskLevel.Medium,
        ApprovalRequired: true);

    public async ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var content = input.GetProperty("content").GetString() ?? string.Empty;

        SandboxWriteResult written = await _writer.WriteAsync(path, content, ct).ConfigureAwait(false);

        var obj = new JsonObject
        {
            ["path"] = written.Path,
            ["bytes"] = written.Bytes,
            ["overwritten"] = written.Overwritten,
        };
        return JsonSerializer.SerializeToElement(obj);
    }
}
