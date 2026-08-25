using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Makes standing operational conditions visible and orderable (RFC 031).
/// </summary>
/// <remarks>
/// Needs give limited autonomy a direction without inventing human desires. The strongest thing one
/// can do is draft a goal; running it goes through the cycle, the policies and the approvals like
/// any other work (rule 2).
/// </remarks>
public interface INeedsService
{
    /// <summary>
    /// Derives needs from what was observed. Idempotent by subject: an unmet need that is still
    /// unmet is updated rather than duplicated, so the list is a state and not a log.
    /// </summary>
    Task<IReadOnlyList<Need>> DetectAsync(
        NeedsSnapshot snapshot, IReadOnlyList<Signal> signals, CancellationToken ct);

    /// <summary>
    /// Orders open needs. Safety and recovery first; then what the person asked for; then
    /// maintenance (rule 3).
    /// </summary>
    Task<IReadOnlyList<Need>> RankAsync(CancellationToken ct);

    /// <summary>
    /// Turns a need into a DRAFT goal and nothing more (rule 2).
    /// </summary>
    Task<Need> PlanAsync(string needId, CancellationToken ct);

    /// <summary>Records that a need was met, against the evidence that met it (rule 1).</summary>
    Task<Need> SatisfyAsync(string needId, string evidenceRef, CancellationToken ct);

    /// <summary>Puts a need aside until a stated time, with its reason.</summary>
    Task<Need> DeferAsync(string needId, DateTimeOffset until, string reason, CancellationToken ct);

    /// <summary>
    /// Decays intensity and expires what is past its time. Rule 4: there are no eternal urgencies,
    /// so a need that nobody acts on gets quieter rather than louder.
    /// </summary>
    Task<int> DecayAsync(CancellationToken ct);

    Task<Need?> GetAsync(string needId, CancellationToken ct);
}
