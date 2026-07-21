using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Capabilities;

/// <summary>Read-only LOW capability that returns the current UTC time.</summary>
public sealed class ClockNowCapability : ICapability
{
    private const string InputSchemaJson =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"properties":{}}""";

    private static readonly JsonElement SchemaElement =
        JsonDocument.Parse(InputSchemaJson).RootElement.Clone();

    private readonly IClock _clock;

    public ClockNowCapability(IClock clock) => _clock = clock;

    public CapabilityDescriptor Descriptor { get; } = new(
        ActionId: "clock.now",
        Title: "Current time",
        Description: "Returns the current UTC time.",
        InputSchema: SchemaElement,
        Effects: Array.Empty<string>(),
        Risk: RiskLevel.Low,
        ApprovalRequired: false);

    public ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        DateTimeOffset now = _clock.UtcNow;
        var obj = new JsonObject
        {
            ["utc"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["unix_ms"] = now.ToUnixTimeMilliseconds(),
        };
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(obj));
    }
}
