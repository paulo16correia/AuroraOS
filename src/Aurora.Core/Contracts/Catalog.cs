using System.Text.Json;

namespace Aurora.Core.Contracts;

/// <summary>Risk tier of a capability. Governs consent: LOW auto, MEDIUM+ requires approval.</summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>A single capability as advertised by <c>aurora_catalog</c>.</summary>
public sealed record CapabilityDescriptor(
    string ActionId,
    string Title,
    string Description,
    JsonElement InputSchema,
    IReadOnlyList<string> Effects,
    RiskLevel Risk,
    bool ApprovalRequired,
    /// <summary>
    /// Whether a completed call can be undone by whoever made it.
    /// </summary>
    /// <remarks>
    /// Not a claim that Aurora will undo it — a claim that the caller is given what they need to.
    /// Default <see langword="false"/>, because the honest answer for a capability whose author
    /// did not think about it is that they did not.
    /// <para>
    /// Policy reads this: a HIGH capability is permitted only when it is both approval-gated and
    /// reversible. Approval is a person saying yes once, and at HIGH that is not enough on its own
    /// — the Constitution's Article 3 asks the same question of the decision.
    /// </para>
    /// </remarks>
    bool Reversible = false,
    /// <summary>
    /// A window this capability asks to open when a person approves it (docs/adr/0070).
    /// </summary>
    /// <remarks>
    /// Null for almost everything, and that is the state to prefer: a capability that declares
    /// nothing here costs one decision per call, which is the model Aurora starts from.
    /// <para>
    /// Declaring one does not widen what a session may cover — it makes the coverage nameable.
    /// The window pays for the actions it lists and for nothing else, so the person approving is
    /// told, in the words of the capability itself, which repeated action they are consenting to
    /// and for how long. What stays refused is the write nobody named.
    /// </para>
    /// </remarks>
    SessionWindow? OpensWindow = null);

/// <summary>
/// The repeated authority a capability asks for on the strength of one approval.
/// </summary>
/// <param name="Actions">
/// The action ids the window pays for — exhaustively. An action absent from this list is not
/// covered, whatever its risk or its author.
/// </param>
/// <param name="MaxActions">How many calls the window pays for before it is spent.</param>
/// <param name="Lifetime">How long it lives, whether or not the budget is used.</param>
/// <remarks>
/// Both bounds are required, and both are ceilings rather than promises: revoking sessions,
/// restarting the server or changing policy ends the window earlier, because liveness is a
/// predicate rather than a timer.
/// </remarks>
public sealed record SessionWindow(
    IReadOnlyList<string> Actions,
    int MaxActions,
    TimeSpan Lifetime);

/// <summary>Result of <c>aurora_catalog</c>.</summary>
public sealed record CatalogResult(IReadOnlyList<CapabilityDescriptor> Actions);
