using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Operational situational awareness (RFC 034).
/// </summary>
/// <remarks>
/// Answers "is this a good moment", which is a different question from "is this allowed". An
/// assessment never grants anything: it can only make Aurora quieter or more careful.
/// </remarks>
public interface ISituationService
{
    /// <summary>
    /// Reads the moment. The result expires, because an assessment describes conditions and
    /// conditions move (rule 1).
    /// </summary>
    Task<SituationAssessment> AssessAsync(SituationContext context, CancellationToken ct);

    /// <summary>
    /// Whether an action fits the moment described.
    /// </summary>
    /// <remarks>
    /// A stale assessment is refused rather than trusted: reusing yesterday's reading of the room
    /// is worse than admitting there is no current one.
    /// </remarks>
    AppropriatenessResult IsAppropriate(string workClass, bool imposesOnUser, SituationAssessment assessment);
}
