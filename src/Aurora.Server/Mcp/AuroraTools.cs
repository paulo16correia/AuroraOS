using System.ComponentModel;
using System.Text.Json;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Kernel;
using ModelContextProtocol.Server;

namespace Aurora.Server.Mcp;

/// <summary>
/// The fixed MCP tools. All return a <see cref="JsonElement"/> serialized via
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
        + "'action_id' with 'input'. 'idempotency_key' is optional. The call is reasoned through a "
        + "cognitive cycle; the returned 'cycle_ref' reads back what was attended to, decided and "
        + "observed. A status of 'asked' means Aurora wants your input before acting.")]
    public static async Task<JsonElement> Execute(
        KernelDispatcher dispatcher,
        IPrincipalAccessor principals,
        McpServer server,
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

        // Through the dispatcher, not straight to the kernel: RFC 045 rule 3 requires MCP ingress
        // to have Mind semantics applied, not only policy and audit.
        var response = await dispatcher.DispatchAsync(
            request, principals.Current, server.SessionId, ct);

        return JsonSerializer.SerializeToElement(response, AuroraJson.Options);
    }

    [McpServerTool(Name = "aurora_converse")]
    [Description("Bring a conversational turn to Aurora. Returns what Aurora recalled, decided and "
        + "recorded for that turn, by reference — not a written reply. You write the reply; Aurora "
        + "is authoritative for what is true and what happened, and never for how it is phrased.")]
    public static async Task<JsonElement> Converse(
        IPilotApplication pilot,
        IPrincipalAccessor principals,
        string conversation_ref,
        string utterance,
        CancellationToken ct = default)
    {
        var outcome = await pilot.RespondAsync(
            new PilotRequest(conversation_ref, utterance, principals.Current), ct);

        return JsonSerializer.SerializeToElement(outcome, AuroraJson.Options);
    }

    [McpServerTool(Name = "aurora_cycle")]
    [Description("Read back a cognitive cycle by its id, as returned in 'cycle_ref' by aurora_execute "
        + "or as 'cycle_id' by aurora_converse: which stages ran, which were deliberately omitted, "
        + "and what was decided and observed.")]
    public static async Task<JsonElement> Cycle(
        IPilotApplication pilot,
        string cycle_id,
        CancellationToken ct = default)
    {
        var outcome = await pilot.RecallAsync(cycle_id, ct);
        return JsonSerializer.SerializeToElement(outcome, AuroraJson.Options);
    }

    [McpServerTool(Name = "aurora_approve")]
    [Description("Decide a pending Aurora approval. 'approval_id' comes from a prior aurora_execute "
        + "response whose status was 'denied' with error code 'approval_required'. 'decision' is "
        + "'approved' or 'rejected'. When this deployment has an operator passphrase enrolled, "
        + "'passphrase' is required and must be supplied by the human operator, not guessed.")]
    public static async Task<JsonElement> Approve(
        AuroraKernel kernel,
        IPrincipalAccessor principals,
        string approval_id,
        string decision,
        string? passphrase = null,
        CancellationToken ct = default)
    {
        var request = new ApproveRequest(approval_id, decision, passphrase);
        var response = await kernel.ApproveAsync(request, principals.Current, ct);
        return JsonSerializer.SerializeToElement(response, AuroraJson.Options);
    }
}
