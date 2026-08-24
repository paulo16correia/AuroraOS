using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Memories;

/// <summary>
/// Ranks by term overlap across summary, predicate and subject.
/// </summary>
/// <remarks>
/// Deliberately not semantic. RFC 03 describes hybrid search with embeddings, and this is the
/// structured half of it: an honest lexical baseline rather than a stub that pretends to understand
/// meaning. The embedding half arrives with its own increment, and the ordering the rule cares
/// about — access before ranking — already holds.
/// </remarks>
public sealed class LexicalMemoryRanker : IMemoryRanker
{
    public IReadOnlyList<RankedMemory> Rank(string query, IReadOnlyList<MemoryRecord> permitted)
    {
        var terms = Tokenise(query);
        if (terms.Count == 0)
        {
            return permitted.Select(m => new RankedMemory(m, 0)).ToList();
        }

        var ranked = new List<RankedMemory>();
        foreach (MemoryRecord memory in permitted)
        {
            var haystack = Tokenise($"{memory.Summary} {memory.Predicate} {memory.SubjectRef}");
            var hits = terms.Count(t => haystack.Contains(t));
            if (hits == 0)
            {
                continue;
            }

            // Confidence breaks ties, so a confirmed fact outranks an equally-matching guess.
            ranked.Add(new RankedMemory(memory, ((double)hits / terms.Count) + (memory.Confidence * 0.01)));
        }

        return ranked.OrderByDescending(r => r.Score).ToList();
    }

    private static HashSet<string> Tokenise(string text) =>
        text.Split([' ', '\t', '\n', '.', ',', ';', ':', '/', '_', '-'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
}
