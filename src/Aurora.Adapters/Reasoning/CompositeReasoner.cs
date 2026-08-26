using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Reasoning;

/// <summary>
/// Tries each proposer in order and returns the first one that names an action.
/// </summary>
/// <remarks>
/// Today the list holds one entry, <see cref="KeywordReasoner"/>, and Aurora ships no other
/// (docs/adr/0051): interpreting language is the LLM client's job, and a proposer that reaches the
/// network would undo the property that Aurora runs entirely on this machine. The seam stays
/// because it is where a local proposer would be added, and because it is the shape that keeps a
/// proposer's answer a suggestion — every proposal still goes through the kernel, which resolves,
/// authorizes and commits on its own terms.
/// </remarks>
public sealed class CompositeReasoner : IReasoner
{
    private readonly IReadOnlyList<IReasoner> _proposers;

    public CompositeReasoner(IEnumerable<IReasoner> proposers)
    {
        _proposers = proposers.ToList();
    }

    public async ValueTask<ReasonerProposal?> ProposeAsync(
        string objective, IReadOnlyList<CapabilityDescriptor> catalog, CancellationToken ct)
    {
        foreach (IReasoner proposer in _proposers)
        {
            ReasonerProposal? proposal = await proposer.ProposeAsync(objective, catalog, ct).ConfigureAwait(false);
            if (proposal is not null && !string.IsNullOrWhiteSpace(proposal.ActionId))
            {
                return proposal;
            }
        }

        return null;
    }
}
