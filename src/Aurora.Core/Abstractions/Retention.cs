namespace Aurora.Core.Abstractions;

/// <summary>
/// How long Aurora keeps the records of its own working.
/// </summary>
/// <remarks>
/// Deliberately does <b>not</b> cover the audit chain, memories, goals or missions. Those are the
/// record of what Aurora did and what it was told, and nothing here may touch them: a system that
/// tidies away its own history on a schedule is one whose history cannot be relied on.
/// <para>
/// What it does cover is the by-products — closed cycles and their stage records, settled schedule
/// runs, resolved signals, expired questions. Those grow without bound and stop being useful long
/// before they stop being stored.
/// </para>
/// </remarks>
public sealed record RetentionPolicy(
    TimeSpan Cycles,
    TimeSpan ScheduleRuns,
    TimeSpan Signals,
    TimeSpan CuriosityProposals)
{
    /// <summary>
    /// Ninety days for cycles, thirty for the rest.
    /// </summary>
    /// <remarks>
    /// Cycles are kept longest because they are how a person reconstructs why Aurora did something,
    /// and that question tends to arrive late. Everything else is operational noise within a month.
    /// </remarks>
    public static RetentionPolicy Default { get; } = new(
        Cycles: TimeSpan.FromDays(90),
        ScheduleRuns: TimeSpan.FromDays(30),
        Signals: TimeSpan.FromDays(30),
        CuriosityProposals: TimeSpan.FromDays(30));

    /// <summary>Keeps everything. For a deployment that would rather grow than forget.</summary>
    public static RetentionPolicy KeepEverything { get; } = new(
        TimeSpan.MaxValue, TimeSpan.MaxValue, TimeSpan.MaxValue, TimeSpan.MaxValue);
}

/// <summary>What one retention pass removed, by kind.</summary>
public sealed record RetentionReport(
    int Cycles, int CycleStages, int ScheduleRuns, int Signals, int CuriosityProposals)
{
    public int Total => Cycles + CycleStages + ScheduleRuns + Signals + CuriosityProposals;
}

/// <summary>
/// Removes finished working records that are past their retention.
/// </summary>
/// <remarks>
/// Only ever removes records that are <i>closed</i>. A cycle still running, a run still due, a
/// signal still pending — none of those age out, however old they are, because their age is the
/// interesting thing about them.
/// </remarks>
public interface IRetentionService
{
    Task<RetentionReport> ApplyAsync(RetentionPolicy policy, CancellationToken ct);
}
