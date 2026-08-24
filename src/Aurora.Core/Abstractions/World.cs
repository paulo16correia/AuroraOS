using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>Something a tool saw. It is never a validated claim by itself (RFC 041 rule 5).</summary>
public sealed record WorldObservation(
    string SubjectRef,
    string Predicate,
    string Category,
    string? ObjectRef,
    string? Literal,
    IReadOnlyList<string> EvidenceRefs,
    double Confidence,
    string ObservedAtUtc,
    string ValidFromUtc,
    string? ValidToUtc = null);

/// <summary>A name a tool saw, to be resolved against known entities.</summary>
public sealed record EntityCandidate(
    string ObservedName,
    string Type,
    IReadOnlyList<string> EvidenceRefs,
    string? SuggestedEntityRef = null,
    double MatchScore = 0);

/// <summary>Temporal, evidenced representation of operational reality (RFC 041).</summary>
public interface IWorldModel
{
    Task<WorldModelVersion> BeginVersionAsync(string mindId, string? parentVersionId, CancellationToken ct);

    /// <summary>Promotes a draft version once its import is complete and validated.</summary>
    Task<WorldModelVersion> ActivateVersionAsync(string versionId, string actor, CancellationToken ct);

    /// <summary>Records an observation as a PROPOSED assertion. Never CURRENT.</summary>
    Task<WorldAssertion> ObserveAsync(
        WorldObservation observation, string versionId, CancellationToken ct);

    /// <summary>
    /// Validates a proposal into a CURRENT assertion. Refused for a tool actor: RFC 041 rule 5
    /// allows a tool to observe and never to conclude.
    /// </summary>
    Task<WorldAssertion> ValidateAsync(
        string assertionId, string actor, IReadOnlyList<string> evidenceRefs, CancellationToken ct);

    /// <summary>Decides identity, deferring rather than guessing when evidence is thin.</summary>
    Task<EntityResolution> ResolveAsync(EntityCandidate candidate, string decidedBy, CancellationToken ct);

    /// <summary>
    /// Answers "what was true as of this instant", using half-open windows so a boundary belongs
    /// to exactly one interval.
    /// </summary>
    Task<WorldAnswer> AskAsync(
        string subjectRef, string predicate, DateTimeOffset asOf, CancellationToken ct);

    /// <summary>
    /// Whether Aurora has evidenced access to a resource. Consults ACCESS assertions only:
    /// ownership and social participation are different claims (rule 3).
    /// </summary>
    Task<WorldAnswer> HasAccessAsync(string subjectRef, string resourceRef, DateTimeOffset asOf, CancellationToken ct);

    /// <summary>Marks the external thing unreachable while keeping the evidence about it.</summary>
    Task<int> MarkInaccessibleAsync(string subjectRef, string reason, CancellationToken ct);

    Task<WorldAssertion?> GetAsync(string assertionId, CancellationToken ct);
}
