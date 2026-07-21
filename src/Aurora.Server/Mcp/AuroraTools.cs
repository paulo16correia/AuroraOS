using System.ComponentModel;
using System.Text.Json;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using ModelContextProtocol.Server;

namespace Aurora.Server.Mcp;

/// <summary>
/// The two fixed MCP tools. Both return a <see cref="JsonElement"/> serialized via
/// <see cref="AuroraJson.Options"/>, so the wire payload is snake_case regardless of the MCP SDK's
/// own serializer.
/// </summary>
[McpServerToolType]
public sealed class AuroraTools
{
    [McpServerTool(Name = "aurora_catalog")]
    [Description("List the Aurora capabilities available to the caller. Optional 'query' filters by substring.")]
    public static JsonElement Catalog(AuroraKernel kernel, string? query = null)
    {
        var result = kernel.Catalog(query);
        return JsonSerializer.SerializeToElement(result, AuroraJson.Options);
    }

    [McpServerTool(Name = "aurora_execute")]
    [Description("Execute an Aurora capability. Provide either a natural-language 'objective' XOR an explicit "
        + "'action_id' with 'input'. 'idempotency_key' is optional.")]
    public static async Task<JsonElement> Execute(
        AuroraKernel kernel,
        IPrincipalAccessor principals,
        string? objective = null,
        string? action_id = null,
        JsonElement? input = null,
        string? idempotency_key = null,
        CancellationToken ct = default)
    {
        // Treat an absent/undefined input as "no input" so the kernel can apply its empty-object default.
        if (input is { ValueKind: JsonValueKind.Undefined })
        {
            input = null;
        }

        var request = new ExecuteRequest(
            Objective: objective,
            ActionId: action_id,
            Input: input,
            IdempotencyKey: idempotency_key);

        var response = await kernel.ExecuteAsync(request, principals.Current, ct);
        return JsonSerializer.SerializeToElement(response, AuroraJson.Options);
    }
}
