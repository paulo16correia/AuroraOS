using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// The mandatory connector interface (RFC 06).
/// </summary>
/// <remarks>
/// <c>ExecuteAsync</c> receives a <see cref="EphemeralSecretHandle"/>, never a secret value. The
/// executor resolves it in a minimal scope and it is never serialised to a log.
/// </remarks>
public interface IToolConnector
{
    ToolManifest Describe();

    Task<ToolResult> ExecuteAsync(ToolCall authorizedCall, EphemeralSecretHandle? secret, CancellationToken ct);

    Task<ToolResult> CancelAsync(string callId, CancellationToken ct);

    /// <summary>Asks the remote side what became of a call whose outcome we never learned.</summary>
    Task<ToolResult> ReconcileAsync(string callId, string? externalReference, CancellationToken ct);
}

/// <summary>Registers connectors and runs their calls under contract (RFC 06).</summary>
public interface IToolManager
{
    Task RegisterAsync(IToolConnector connector, CancellationToken ct);

    /// <summary>
    /// Proposes a call. Refuses a capability the manifest does not declare, an input that fails the
    /// input schema, and a writing tool without an idempotency key.
    /// </summary>
    Task<ToolCall> ProposeAsync(
        string workItemId, string? taskId, string toolId, string capability,
        string inputJson, string? idempotencyKey, CancellationToken ct);

    Task<ToolCall> AuthorizeAsync(
        string callId, IReadOnlyList<string> policyDecisionIds, string? approvalId, CancellationToken ct);

    /// <summary>Runs an authorized call, enforcing timeout, rate limit and output validation.</summary>
    Task<ToolCall> ExecuteAsync(string callId, CancellationToken ct);

    /// <summary>
    /// Resolves an <c>UNKNOWN</c> call by asking the remote side. Never resends: "we did not hear
    /// back" is not permission to do it again.
    /// </summary>
    Task<ToolCall> ReconcileAsync(string callId, CancellationToken ct);

    /// <summary>Disables a tool whose remote schema changed, so no further call is attempted.</summary>
    Task<int> DisableAsync(string toolId, string reason, CancellationToken ct);

    Task<ToolCall?> GetCallAsync(string callId, CancellationToken ct);

    Task<IReadOnlyList<ToolCall>> UnknownCallsAsync(CancellationToken ct);
}
