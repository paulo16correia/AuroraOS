using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Who has a tie to whom, and how someone likes things done (RFC 029).
/// </summary>
/// <remarks>
/// Two objects that look adjacent and are not. A relationship is a fact about the world; a
/// preference is a habit of the person. Neither is a permission, and nothing on this interface can
/// turn either into one — rule 1 keeps relationship, permission and identity separate, and the way
/// to keep them separate is to have no method that crosses between them.
/// </remarks>
public interface IRelationshipModel
{
    /// <summary>
    /// States a tie. Refused against an entity that has not been resolved, and refused for a
    /// third party without an authorisation and a retention.
    /// </summary>
    Task<RelationshipAssertion> AssertAsync(
        RelationshipCandidate candidate, IReadOnlyList<string> evidenceRefs, CancellationToken ct);

    /// <summary>
    /// Closes a relationship's interval. The record stays and the past is not rewritten (rule 4).
    /// </summary>
    Task<RelationshipAssertion> EndAsync(
        string relationshipId, string evidenceRef, CancellationToken ct);

    /// <summary>Marks a tie as contested. It stops being usable and stays on record.</summary>
    Task<RelationshipAssertion> DisputeAsync(
        string relationshipId, string evidenceRef, string reason, CancellationToken ct);

    /// <summary>The ties in force for a subject at a moment.</summary>
    Task<IReadOnlyList<RelationshipAssertion>> InForceAsync(
        string subjectRef, DateTimeOffset at, CancellationToken ct);

    /// <summary>Everything ever stated about a subject, including what has ended.</summary>
    Task<IReadOnlyList<RelationshipAssertion>> HistoryAsync(string subjectRef, CancellationToken ct);

    /// <summary>
    /// Records what the person actually said. Displaces any inference that contradicts it.
    /// </summary>
    Task<Preference> SetExplicitAsync(
        string ownerRef, string subjectRef, string dimension, string valueJson,
        IReadOnlyList<string> evidenceRefs, CancellationToken ct);

    /// <summary>
    /// Records a preference Aurora worked out. Never displaces an explicit one.
    /// </summary>
    Task<Preference> InferAsync(
        Preference candidate, IReadOnlyList<string> evidenceRefs, CancellationToken ct);

    /// <summary>
    /// The preferences that apply, and whether they license acting without asking (rule 2).
    /// </summary>
    Task<PreferenceResolution> ResolveAsync(
        string ownerRef, string dimension, string effect, CancellationToken ct);

    Task<RelationshipAssertion?> GetAsync(string relationshipId, CancellationToken ct);

    /// <summary>Expires preferences past review, and ends relationships past their retention.</summary>
    Task<int> ReviewDueAsync(CancellationToken ct);
}
