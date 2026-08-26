using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>Read-only LOW capability that echoes a supplied message back to the caller.</summary>
public sealed class EchoSayCapability : ICapability
{
    private static readonly JsonElement SchemaElement =
        CapabilityInput.Object()
            .String("message", maxLength: 2000, required: true)
            .Build();

    public CapabilityDescriptor Descriptor { get; } = new(
        ActionId: "echo.say",
        Title: "Echo",
        Description: "Echoes a message back.",
        InputSchema: SchemaElement,
        Effects: Array.Empty<string>(),
        Risk: RiskLevel.Low,
        ApprovalRequired: false);

    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        string message = input.GetProperty("message").GetString() ?? string.Empty;
        var obj = new JsonObject { ["said"] = message };
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(obj));
    }
}
