using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Reasoning;

/// <summary>
/// Offline fallback proposer. Matches the objective against capability names and, per design 0001,
/// only ever considers LOW-risk, effect-free capabilities.
/// </summary>
/// <remarks>
/// Deliberately timid. Keyword matching has no idea what a request means, so it proposes only when
/// it can build an input the schema actually describes: an empty object when nothing is required,
/// or the leftover text when exactly one required string field is expected. Anything richer would
/// be inventing argument values, so it declines and the caller gets "objective mode unavailable".
/// The LOW restriction here is a first line, not the guarantee — the kernel enforces it again.
/// </remarks>
public sealed class KeywordReasoner : IReasoner
{
    private const double KeywordConfidence = 0.4;

    public ValueTask<ReasonerProposal?> ProposeAsync(
        string objective, IReadOnlyList<CapabilityDescriptor> catalog, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            return ValueTask.FromResult<ReasonerProposal?>(null);
        }

        foreach (CapabilityDescriptor descriptor in catalog)
        {
            if (descriptor.Risk != RiskLevel.Low || descriptor.Effects.Count > 0)
            {
                continue;
            }

            var keyword = MatchedKeyword(objective, descriptor);
            if (keyword is null)
            {
                continue;
            }

            JsonElement? input = BuildInput(objective, keyword, descriptor.InputSchema);
            if (input is null)
            {
                continue;
            }

            return ValueTask.FromResult<ReasonerProposal?>(
                new ReasonerProposal(descriptor.ActionId, input, KeywordConfidence, ResolutionVia.Keyword));
        }

        return ValueTask.FromResult<ReasonerProposal?>(null);
    }

    /// <summary>Returns the catalog term found in the objective, or null when nothing matches.</summary>
    private static string? MatchedKeyword(string objective, CapabilityDescriptor descriptor)
    {
        // "memory.recall" is also reachable as "recall"; the verb is what people actually type.
        var terms = new List<string> { descriptor.ActionId };
        var lastSegment = descriptor.ActionId.Split('.').LastOrDefault();
        if (!string.IsNullOrEmpty(lastSegment))
        {
            terms.Add(lastSegment);
        }

        foreach (var term in terms)
        {
            if (objective.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return term;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds an input the schema will accept, or null when the schema wants more than keyword
    /// matching can honestly supply.
    /// </summary>
    private static JsonElement? BuildInput(string objective, string keyword, JsonElement schema)
    {
        var required = new List<string>();
        if (schema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in requiredElement.EnumerateArray())
            {
                if (item.GetString() is { } name)
                {
                    required.Add(name);
                }
            }
        }

        if (required.Count == 0)
        {
            return JsonSerializer.SerializeToElement(new JsonObject());
        }

        if (required.Count > 1)
        {
            return null;
        }

        var field = required[0];
        if (!schema.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty(field, out var fieldSchema)
            || fieldSchema.TryGetProperty("type", out var type) is false
            || type.GetString() != "string")
        {
            return null;
        }

        var remainder = TextAfter(objective, keyword);
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new JsonObject { [field] = remainder });
    }

    private static string TextAfter(string objective, string keyword)
    {
        var index = objective.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? string.Empty : objective[(index + keyword.Length)..].Trim();
    }
}
