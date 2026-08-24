using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Chooses a mode of action, separately from writing a response (RFC 022).</summary>
public interface IDecisionEngine
{
    /// <summary>
    /// Weighs the options and proposes one. Never escalates privilege: an option that is not
    /// permitted, or a confident claim with no evidence, is reduced rather than granted.
    /// </summary>
    Task<Decision> EvaluateAsync(DecisionThought thought, DecisionContext context, CancellationToken ct);

    /// <summary>
    /// Commits a proposal against the Policy stage's results. A TOOL_CALL without an allow and,
    /// where required, a satisfied approval cannot be committed (rule 2).
    /// </summary>
    Task<Decision> CommitAsync(
        string decisionId, IReadOnlyList<PolicyResult> policyResults, CancellationToken ct);

    /// <summary>Supersedes a decision that new information invalidated, before it takes effect.</summary>
    Task<Decision> InvalidateAsync(string decisionId, string reason, CancellationToken ct);

    /// <summary>Expires decisions past their deadline (rule 4).</summary>
    Task<int> ExpireDueAsync(CancellationToken ct);

    Task<Decision?> GetAsync(string decisionId, CancellationToken ct);
}
