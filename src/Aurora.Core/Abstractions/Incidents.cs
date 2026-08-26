using Aurora.Core.Contracts;

namespace Aurora.Core.Abstractions;

/// <summary>
/// Opens, contains and closes security incidents (RFC 09 rule 5).
/// </summary>
/// <remarks>
/// The rule is three things at once: "high risk incidents MUST revoke affected capacity, preserve
/// evidence, and notify owner". Aurora had all three as separate half-measures — the development
/// model restricted a stage after an incident, life history had an episode kind for one,
/// maintenance notified about one, plugins quarantined themselves — and nothing that raised the
/// event or tied them together.
/// <para>
/// The order matters and is not negotiable: revoke first, then record, then notify. A notification
/// that goes out before containment tells the owner about something that is still happening, and
/// the seconds spent drawing a dialog are seconds the thing is still running.
/// </para>
/// </remarks>
public interface IIncidentService
{
    /// <summary>
    /// Opens an incident. At <see cref="SecuritySeverity.High"/> or above this revokes, records and
    /// notifies before it returns; below that it records and returns.
    /// </summary>
    Task<Incident> OpenAsync(SecurityEvent securityEvent, CancellationToken ct);

    /// <summary>Closes one, with what was concluded and who concluded it.</summary>
    Task<Incident> ResolveAsync(
        string incidentId, string resolution, string actor, CancellationToken ct);

    Task<Incident?> GetAsync(string incidentId, CancellationToken ct);

    /// <summary>Everything not yet resolved, newest first.</summary>
    Task<IReadOnlyList<Incident>> OpenIncidentsAsync(CancellationToken ct);
}
