using System.Collections.Concurrent;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Incidents;

/// <summary>
/// Counts refusals and turns a pattern of them into an incident (RFC 09 rule 5).
/// </summary>
/// <remarks>
/// In memory, and on purpose. These counts describe what is happening to a running instance; a
/// process that restarted lost the context that made them mean something, and carrying them across
/// would mean a restart weeks later inherits the tail of an attack that ended.
/// <para>
/// The thresholds do the work. One failed credential is a typo; one refused permission is a plugin
/// asking for something it was not given, which the manifest reader is supposed to have caught.
/// Neither is an incident on its own, and neither is quiet when it repeats.
/// </para>
/// </remarks>
public sealed class SecurityWatch : ISecurityWatch
{
    /// <summary>
    /// How many credentials may fail before it stops being somebody mistyping.
    /// </summary>
    /// <remarks>
    /// Aurora listens on loopback only, so whoever is trying is already on this machine. Five is
    /// low because at that point the interesting question is not whether they are authorised but
    /// what else they are doing.
    /// </remarks>
    private const int FailuresBeforeIncident = 5;

    /// <summary>
    /// How long a count and a raised incident stand for.
    /// </summary>
    /// <remarks>
    /// Both, and the same window for both: it bounds how far apart attempts can be and still be
    /// one attack, and it stops a loop that keeps failing from opening an incident a second.
    /// </remarks>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Attempts> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _raised = new(StringComparer.Ordinal);

    private readonly IIncidentService _incidents;
    private readonly IClock _clock;

    public SecurityWatch(IIncidentService incidents, IClock clock)
    {
        _incidents = incidents;
        _clock = clock;
    }

    public async Task AuthenticationFailedAsync(string source, CancellationToken ct)
    {
        DateTimeOffset now = _clock.UtcNow;

        Attempts attempts = _failures.AddOrUpdate(
            source,
            _ => new Attempts(1, now),
            (_, existing) => now - existing.Since > Window
                ? new Attempts(1, now)
                : existing with { Count = existing.Count + 1 });

        if (attempts.Count < FailuresBeforeIncident)
        {
            return;
        }

        // Reset before opening, so the incident itself cannot be the thing that keeps the count
        // above the line for the rest of the window.
        _failures.TryRemove(source, out _);

        await OpenAsync(
            $"auth:{source}",
            new SecurityEvent(
                string.Empty, SecuritySeverity.High, SecurityEventType.AuthenticationAbuse,
                Guid.NewGuid().ToString("N"), source,

                // Nothing named, so containment revokes standing consent and disables nothing:
                // whoever is guessing is not a component Aurora can switch off.
                ResourceRef: string.Empty,
                DecisionRef: null,
                EvidenceRef: $"{attempts.Count} credentials refused on {source} within {Window.TotalMinutes:F0}m",
                DetectedAtUtc: string.Empty),
            now, ct).ConfigureAwait(false);
    }

    public Task PrivilegeEscalationAsync(
        string actor, string resourceRef, string detail, CancellationToken ct) =>

        // No threshold. Asking for authority that was never granted is not something that happens
        // by accident the way a mistyped credential does — the manifest reader refuses an
        // undeclared permission at install, and the catalogue refuses an unknown action, so
        // reaching this means something got past both.
        OpenAsync(
            $"escalation:{actor}:{resourceRef}",
            new SecurityEvent(
                string.Empty, SecuritySeverity.High, SecurityEventType.PrivilegeEscalation,
                Guid.NewGuid().ToString("N"), actor, resourceRef, DecisionRef: null,
                EvidenceRef: detail, DetectedAtUtc: string.Empty),
            _clock.UtcNow, ct);

    /// <summary>
    /// Opens one, at most once per key per window.
    /// </summary>
    /// <remarks>
    /// A loop that keeps being refused would otherwise open an incident per iteration, and an
    /// incident log with four thousand entries in it says less than one with a single entry saying
    /// the same thing happened four thousand times.
    /// </remarks>
    private async Task OpenAsync(
        string key, SecurityEvent securityEvent, DateTimeOffset now, CancellationToken ct)
    {
        if (_raised.TryGetValue(key, out DateTimeOffset last) && now - last <= Window)
        {
            return;
        }

        _raised[key] = now;

        try
        {
            await _incidents.OpenAsync(securityEvent, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The caller has already refused whatever this was about, and the refusal is what
            // protects the system. Failing to record it must not turn a handled refusal into an
            // unhandled exception on the request path.
            _raised.TryRemove(key, out _);
        }
    }

    private sealed record Attempts(int Count, DateTimeOffset Since);
}
