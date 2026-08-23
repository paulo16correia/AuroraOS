using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Reasoning;

/// <summary>
/// Tries each proposer in order and returns the first proposal. The model-backed proposer goes
/// first when configured; the keyword fallback answers when it is absent, offline or unsure, so a
/// missing Azure deployment degrades objective mode to LOW read-only actions instead of killing it.
/// </summary>
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
