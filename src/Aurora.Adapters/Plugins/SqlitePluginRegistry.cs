using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// Third-party capabilities on a security contract (RFC 060).
/// </summary>
/// <remarks>
/// The RFC's justification in one line: a plugin for one thing can be useful without gaining powers
/// over email, SSH or the Mind. Everything here exists to keep a declaration from becoming an
/// authority — a manifest states limits, and what is enforced is exactly those limits and nothing
/// softer. An empty list in a manifest means "none", never "unspecified".
/// </remarks>
public sealed class SqlitePluginRegistry : IPluginRegistry
{
    /// <summary>What this build of Aurora is, for a manifest's minimum-platform claim.</summary>
    public const int PlatformVersion = 1;

    /// <summary>How many failures in a row open the circuit.</summary>
    /// <remarks>
    /// RFC 060's limit case asks for a protective circuit rather than an endless retry. Three is
    /// enough to distinguish a bad moment from a broken plugin, and few enough that a broken one
    /// stops being asked quickly.
    /// </remarks>
    private const int FailureThreshold = 3;

    private readonly SqliteConnectionFactory _factory;
    private readonly IPluginHost _host;
    private readonly IEventBus _bus;
    private readonly byte[] _publisherKey;
    private readonly IClock _clock;

    public SqlitePluginRegistry(
        SqliteConnectionFactory factory, IPluginHost host, IEventBus bus,
        byte[] publisherKey, IClock clock)
    {
        _factory = factory;
        _host = host;
        _bus = bus;
        _publisherKey = publisherKey;
        _clock = clock;
    }

    public Task<PluginVerification> VerifyAsync(PluginManifest manifest, CancellationToken ct)
    {
        var refusals = new List<string>();

        // Limit case: an invalid signature is rejected before installing, and does not run even in
        // preview. There is no mode in which unattributable code executes.
        if (!SignatureValid(manifest))
        {
            refusals.Add(PluginRefusal.SignatureInvalid);
        }

        if (!string.Equals(manifest.IntegrityHash, HashOf(manifest), StringComparison.Ordinal))
        {
            refusals.Add(PluginRefusal.IntegrityMismatch);
        }

        if (manifest.MinPlatformVersion > PlatformVersion)
        {
            refusals.Add(PluginRefusal.PlatformTooOld);
        }

        if (!Sensitivity.IsKnown(manifest.MaxDataClass))
        {
            refusals.Add(PluginRefusal.UndeclaredDataClass);
        }

        // A declared endpoint used to be refused here: the sandbox denied the network outright, so
        // there was nothing to grant. Endpoints are grantable since docs/adr/0067, and what a
        // manifest must not do is ask vaguely. Every host is named, and a wildcard is not a name.
        foreach (var endpoint in manifest.NetworkEndpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint)
                || endpoint.Contains('*', StringComparison.Ordinal)
                || endpoint.Contains('/', StringComparison.Ordinal))
            {
                refusals.Add(PluginRefusal.UndeclaredEndpoint);
                break;
            }
        }

        return Task.FromResult(new PluginVerification(
            refusals.Count == 0, refusals,
            refusals.Count == 0
                ? $"{manifest.PluginId} {manifest.Version} by {manifest.Publisher} verifies"
                : $"refused: {string.Join(", ", refusals)}"));
    }

    public Task<PluginInstallation> InstallAsync(
        PluginManifest manifest, IReadOnlyList<string> grantedPermissions,
        string approvalRef, CancellationToken ct) =>
        InstallAsync(manifest, grantedPermissions, [], approvalRef, ct);

    public Task<PluginInstallation> InstallAsync(
        PluginManifest manifest, IReadOnlyList<string> grantedPermissions,
        IReadOnlyList<string> grantedEndpoints, string approvalRef, CancellationToken ct) =>
        InstallAsync(manifest, grantedPermissions, grantedEndpoints, false, approvalRef, ct);

    public async Task<PluginInstallation> InstallAsync(
        PluginManifest manifest, IReadOnlyList<string> grantedPermissions,
        IReadOnlyList<string> grantedEndpoints, bool grantGpu, string approvalRef,
        CancellationToken ct)
    {
        PluginVerification verification = await VerifyAsync(manifest, ct).ConfigureAwait(false);
        if (!verification.Ok)
        {
            throw new PluginException(verification.Detail);
        }

        // Rule 3: installation is a review somebody did, not a step a caller took.
        if (string.IsNullOrWhiteSpace(approvalRef))
        {
            throw new PluginException("Installing a plugin needs an approval.");
        }

        // Granting more than was asked for is how a review stops meaning anything. What is granted
        // is the intersection, never the union.
        var granted = grantedPermissions
            .Intersect(manifest.RequiredPermissions, StringComparer.Ordinal)
            .ToList();

        // The same rule for hosts: what is granted is the intersection with what was asked for, so
        // agreeing cannot widen the request.
        var endpoints = grantedEndpoints
            .Intersect(manifest.NetworkEndpoints, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var now = Iso(_clock.UtcNow);
        var installation = new PluginInstallation(
            Guid.NewGuid().ToString("N"), manifest.PluginId, manifest.Version, manifest.Publisher,
            InstallationStatus.Installed, granted, AuroraJson.Serialize(manifest),
            now, now, ConsecutiveFailures: 0, QuarantineReason: null, approvalRef,
            GrantedEndpoints: endpoints,

            // Granting what was not asked for is how a review stops meaning anything, here as
            // everywhere else: the intersection, never the union.
            GpuGranted: grantGpu && manifest.RequiresGpu);

        await SaveAsync(installation, ct).ConfigureAwait(false);
        return installation;
    }

    public async Task<PluginInstallation> UpdateAsync(PluginManifest manifest, CancellationToken ct)
    {
        PluginInstallation existing = await RequireAsync(manifest.PluginId, ct).ConfigureAwait(false);
        PluginVerification verification = await VerifyAsync(manifest, ct).ConfigureAwait(false);

        if (!verification.Ok)
        {
            throw new PluginException(verification.Detail);
        }

        // Rule 5. A new publisher is a different party behind the same name, and new permissions
        // are a bigger ask than the one that was reviewed. Either is a reason to stop and look
        // rather than to carry the previous decision forward onto something it was not about.
        var reasons = new List<string>();

        if (!string.Equals(manifest.Publisher, existing.Publisher, StringComparison.Ordinal))
        {
            reasons.Add(PluginRefusal.NewPublisher);
        }

        var added = manifest.RequiredPermissions
            .Except(existing.GrantedPermissions, StringComparer.Ordinal)
            .ToList();

        if (added.Count > 0)
        {
            reasons.Add($"{PluginRefusal.NewPermissions}: {string.Join(", ", added)}");
        }

        PluginInstallation updated = existing with
        {
            Version = manifest.Version,
            Publisher = manifest.Publisher,
            ManifestJson = AuroraJson.Serialize(manifest),
            UpdatedAtUtc = Iso(_clock.UtcNow),
            Status = reasons.Count > 0 ? InstallationStatus.Quarantined : existing.Status,
            QuarantineReason = reasons.Count > 0 ? string.Join("; ", reasons) : null,
            ConsecutiveFailures = 0,
        };

        await SaveAsync(updated, ct).ConfigureAwait(false);

        if (reasons.Count > 0)
        {
            await QuarantineEventAsync(updated, ct).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<PluginResult> InvokeAsync(PluginInvocation invocation, CancellationToken ct)
    {
        PluginInstallation installation =
            await RequireAsync(invocation.PluginId, ct).ConfigureAwait(false);

        if (!InstallationStatus.CanRun(installation.Status))
        {
            return Refused(
                PluginRefusal.CircuitOpen,
                $"{invocation.PluginId} is {installation.Status}: "
                + (installation.QuarantineReason ?? "not currently installed"));
        }

        PluginManifest manifest = AuroraJson.Deserialize<PluginManifest>(installation.ManifestJson);

        PluginCapability? capability = manifest.Capabilities
            .FirstOrDefault(c => string.Equals(c.Key, invocation.CapabilityKey, StringComparison.Ordinal));

        // Rule 1: an undeclared request is denied. Not "unsupported" — denied, because the
        // manifest is the whole of what this plugin was reviewed to do.
        if (capability is null)
        {
            return Refused(
                PluginRefusal.UndeclaredEffect,
                $"'{invocation.CapabilityKey}' is not declared by {invocation.PluginId}");
        }

        // Rule 4: never handed data above what it declared. Checked before the call, because
        // afterwards the plugin has already seen it.
        if (Sensitivity.Rank(invocation.DataClass) > Sensitivity.Rank(manifest.MaxDataClass))
        {
            return Refused(
                PluginRefusal.AboveDeclaredClassification,
                $"{invocation.DataClass} is above the {manifest.MaxDataClass} this plugin declared");
        }

        var missing = manifest.RequiredPermissions
            .Except(installation.GrantedPermissions, StringComparer.Ordinal)
            .ToList();

        if (missing.Count > 0)
        {
            return Refused(
                PluginRefusal.PermissionNotGranted,
                $"never granted: {string.Join(", ", missing)}");
        }

        // Rule 1: a plugin talks to the hosts it declared and the owner agreed to, or to none.
        // A manifest that asks and an installation that never granted is refused here rather than
        // handed to a sandbox that would let it out.
        IReadOnlyList<string> grantedEndpoints = installation.GrantedEndpoints ?? [];

        if (manifest.NetworkEndpoints.Count > 0 && grantedEndpoints.Count == 0)
        {
            return Refused(
                PluginRefusal.NetworkNotGranted,
                $"{invocation.PluginId} declares {manifest.NetworkEndpoints.Count} host(s) and was "
                + "granted none");
        }

        var reaching = manifest.NetworkEndpoints
            .Except(grantedEndpoints, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (reaching.Count > 0)
        {
            // The manifest grew a host since the grant. That is a new decision, not a continuation
            // of the old one.
            return Refused(
                PluginRefusal.UndeclaredEndpoint,
                $"not covered by the grant: {string.Join(", ", reaching)}");
        }

        if (manifest.RequiresGpu && !installation.GpuGranted)
        {
            return Refused(
                PluginRefusal.GpuNotGranted,
                $"{invocation.PluginId} asks for the graphics processor and was not granted it");
        }

        PluginResult result;
        try
        {
            result = await _host.InvokeAsync(
                manifest,
                invocation with
                {
                    NetworkGranted = grantedEndpoints.Count > 0,
                    GpuGranted = installation.GpuGranted,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            result = new PluginResult(false, null, "host_failed", failure.GetType().Name, 0);
        }

        if (result is { Ok: true, OutputJson: { } output } && LooksLikeSecret(output))
        {
            // Limit case, and the sharpest one: a plugin returning something that looks like a
            // credential has its output dropped, an event raised and its installation quarantined.
            // Whether it was malice or an accident, the next call is not made until somebody looks.
            await QuarantineAsync(
                installation, PluginRefusal.SecretInOutput,
                "output resembled a credential", ct).ConfigureAwait(false);

            return Refused(PluginRefusal.SecretInOutput, "the output was withheld and not returned");
        }

        if (result.Refusal == PluginRefusal.SandboxUnavailable)
        {
            // The plugin never ran. Counting this would open the circuit after three calls and
            // quarantine a plugin for a property of the machine — so that installing bubblewrap,
            // or accepting an unconfined run, would leave the owner with a plugin still marked
            // untrustworthy for a reason it was never responsible for.
            return result;
        }

        await RecordOutcomeAsync(installation, result.Ok, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<PluginInstallation> DisableAsync(
        string installationId, string actor, CancellationToken ct)
    {
        PluginInstallation installation =
            await ByInstallationAsync(installationId, ct).ConfigureAwait(false);

        if (installation.Status == InstallationStatus.Removed)
        {
            throw new PluginException($"{installation.PluginId} was removed; there is nothing to disable.");
        }

        PluginInstallation disabled = installation with
        {
            Status = InstallationStatus.Disabled,
            UpdatedAtUtc = Iso(_clock.UtcNow),
            QuarantineReason = $"disabled by {actor}",
        };

        await SaveAsync(disabled, ct).ConfigureAwait(false);
        return disabled;
    }

    public async Task<PluginInstallation> RemoveAsync(
        string installationId, string actor, CancellationToken ct)
    {
        PluginInstallation installation =
            await ByInstallationAsync(installationId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new PluginException("Removing a plugin names who removed it.");
        }

        if (installation.Status == InstallationStatus.Removed)
        {
            throw new PluginException($"{installation.PluginId} is already removed.");
        }

        // The row stays. What Aurora once ran, and what it was granted while it ran, is part of
        // how the instance got here — an installation log that forgets removals cannot answer
        // "what was this machine doing in March".
        PluginInstallation removed = installation with
        {
            Status = InstallationStatus.Removed,
            QuarantineReason = $"removed by {actor}",
            GrantedPermissions = [],
            UpdatedAtUtc = Iso(_clock.UtcNow),
        };

        await SaveAsync(removed, ct).ConfigureAwait(false);
        return removed;
    }

    public async Task<PluginInstallation> ReleaseAsync(
        string installationId, string approvalRef, string actor, CancellationToken ct)
    {
        PluginInstallation installation =
            await ByInstallationAsync(installationId, ct).ConfigureAwait(false);

        // A quarantine ends because somebody looked and decided, not because time passed.
        if (string.IsNullOrWhiteSpace(approvalRef) || string.IsNullOrWhiteSpace(actor))
        {
            throw new PluginException("Releasing a quarantine is a decision; it needs an approval and an actor.");
        }

        // Removed is terminal. Letting one back would restore permissions the owner took away,
        // through the door meant for a plugin that was held rather than one that was finished
        // with.
        if (installation.Status == InstallationStatus.Removed)
        {
            throw new PluginException(
                $"{installation.PluginId} was removed. Install it again if you want it back.");
        }

        PluginInstallation released = installation with
        {
            Status = InstallationStatus.Installed,
            QuarantineReason = null,
            ConsecutiveFailures = 0,
            ApprovalRef = approvalRef,
            UpdatedAtUtc = Iso(_clock.UtcNow),
        };

        await SaveAsync(released, ct).ConfigureAwait(false);
        return released;
    }

    /// <summary>
    /// The events this plugin may receive, filtered by what it was granted.
    /// </summary>
    /// <remarks>
    /// Rule 4's second half: a plugin cannot subscribe to events without authorisation filtering.
    /// A declared subscription is a request; this is the answer, and it is the intersection of what
    /// was asked for, what Aurora actually publishes, and what the plugin may see.
    /// </remarks>
    public async Task<IReadOnlyList<string>> PermittedSubscriptionsAsync(
        string pluginId, CancellationToken ct)
    {
        PluginInstallation installation = await RequireAsync(pluginId, ct).ConfigureAwait(false);

        if (!InstallationStatus.CanRun(installation.Status))
        {
            return [];
        }

        PluginManifest manifest = AuroraJson.Deserialize<PluginManifest>(installation.ManifestJson);

        return manifest.EventSubscriptions
            .Where(type => EventCatalogue.Declared.Any(c =>
                string.Equals(c.Type, type, StringComparison.Ordinal)
                && Sensitivity.Rank(c.SensitivityClass) <= Sensitivity.Rank(manifest.MaxDataClass)))
            .ToList();
    }

    public async Task<PluginInstallation?> GetAsync(string pluginId, CancellationToken ct)
    {
        IReadOnlyList<PluginInstallation> found = await ReadAsync(
            $"{Select} WHERE plugin_id = @id;", ct, ("@id", pluginId)).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    public Task<IReadOnlyList<PluginInstallation>> ListAsync(CancellationToken ct) =>
        ReadAsync($"{Select} ORDER BY plugin_id;", ct);

    // ---- plumbing ----

    /// <summary>
    /// Shapes that look like a credential leaving a plugin.
    /// </summary>
    /// <remarks>
    /// A heuristic, and named as one. It catches the obvious carriers — bearer tokens, private key
    /// blocks, long base64 runs under a secret-sounding key — and it will not catch a determined
    /// exfiltration. The structural control is elsewhere: the plugin is never handed a secret in
    /// the first place, so what this protects against is a plugin that found one another way.
    /// </remarks>
    private static bool LooksLikeSecret(string output) => SecretShape.Matches(output);

    private bool SignatureValid(PluginManifest manifest) =>
        string.Equals(
            manifest.Signature,
            Hashing.HmacSha256Hex(_publisherKey, $"{manifest.PluginId}\n{manifest.Version}\n{manifest.Publisher}"),
            StringComparison.Ordinal);

    /// <summary>Signs a manifest, for a publisher this deployment trusts.</summary>
    public static string Sign(byte[] publisherKey, string pluginId, string version, string publisher) =>
        Hashing.HmacSha256Hex(publisherKey, $"{pluginId}\n{version}\n{publisher}");

    /// <summary>The hash a manifest must carry, over everything it declares.</summary>
    public static string HashOf(PluginManifest manifest) =>
        Hashing.Sha256Hex(AuroraJson.Serialize(manifest with { IntegrityHash = string.Empty }));

    private static PluginResult Refused(string refusal, string detail) =>
        new(false, null, refusal, detail, 0);

    private async Task RecordOutcomeAsync(
        PluginInstallation installation, bool ok, CancellationToken ct)
    {
        var failures = ok ? 0 : installation.ConsecutiveFailures + 1;

        if (failures >= FailureThreshold)
        {
            // The circuit. A plugin that keeps failing stops being asked, and the pending calls are
            // left where reconciliation can find them rather than retried into the same wall.
            await QuarantineAsync(
                installation, PluginRefusal.CircuitOpen,
                $"{failures} consecutive failures", ct).ConfigureAwait(false);

            return;
        }

        await ExecuteAsync(
            "UPDATE plugin_installation SET consecutive_failures = @n, updated_at_utc = @at WHERE id = @id;",
            ct,
            ("@n", failures), ("@at", Iso(_clock.UtcNow)), ("@id", installation.Id))
            .ConfigureAwait(false);
    }

    private async Task QuarantineAsync(
        PluginInstallation installation, string refusal, string detail, CancellationToken ct)
    {
        PluginInstallation quarantined = installation with
        {
            Status = InstallationStatus.Quarantined,
            QuarantineReason = $"{refusal}: {detail}",
            UpdatedAtUtc = Iso(_clock.UtcNow),
        };

        await SaveAsync(quarantined, ct).ConfigureAwait(false);
        await QuarantineEventAsync(quarantined, ct).ConfigureAwait(false);
    }

    private Task QuarantineEventAsync(PluginInstallation installation, CancellationToken ct) =>
        _bus.PublishAsync(
            new OutboxWrite(
                EventCatalogue.PluginQuarantined, 1, EventCatalogue.Producers.Kernel,
                Guid.NewGuid().ToString("N"), Sensitivity.Private,
                AggregateRef: $"plugin/{installation.PluginId}",
                PayloadJson: AuroraJson.Serialize(
                    new { plugin_id = installation.PluginId, reason = installation.QuarantineReason }),
                IdempotencyKey: $"quarantine:{installation.Id}:{installation.UpdatedAtUtc}"),
            ct);

    private async Task<PluginInstallation> RequireAsync(string pluginId, CancellationToken ct) =>
        await GetAsync(pluginId, ct).ConfigureAwait(false)
        ?? throw new PluginException($"'{pluginId}' is not installed.");

    private async Task<PluginInstallation> ByInstallationAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<PluginInstallation> found = await ReadAsync(
            $"{Select} WHERE id = @id;", ct, ("@id", id)).ConfigureAwait(false);

        return found.Count == 0 ? throw new PluginException("Unknown installation.") : found[0];
    }

    private const string Select = """
        SELECT id, plugin_id, version, publisher, status, granted_permissions, manifest_json,
               installed_at_utc, updated_at_utc, consecutive_failures, quarantine_reason,
               approval_ref, granted_endpoints, gpu_granted
          FROM plugin_installation
        """;

    private Task SaveAsync(PluginInstallation installation, CancellationToken ct) =>
        ExecuteAsync("""
            INSERT INTO plugin_installation
                (id, plugin_id, version, publisher, status, granted_permissions, manifest_json,
                 installed_at_utc, updated_at_utc, consecutive_failures, quarantine_reason,
                 approval_ref, granted_endpoints, gpu_granted)
            VALUES (@id, @plugin, @version, @publisher, @status, @granted, @manifest, @installed,
                    @updated, @failures, @reason, @approval, @endpoints, @gpu)
            ON CONFLICT(plugin_id) DO UPDATE SET
                version = @version, publisher = @publisher, status = @status,
                granted_permissions = @granted, manifest_json = @manifest, updated_at_utc = @updated,
                consecutive_failures = @failures, quarantine_reason = @reason,
                approval_ref = @approval, granted_endpoints = @endpoints,
                gpu_granted = @gpu;
            """, ct,
            ("@id", installation.Id), ("@plugin", installation.PluginId),
            ("@version", installation.Version), ("@publisher", installation.Publisher),
            ("@status", installation.Status),
            ("@granted", string.Join('\n', installation.GrantedPermissions)),
            ("@manifest", installation.ManifestJson),
            ("@installed", installation.InstalledAtUtc), ("@updated", installation.UpdatedAtUtc),
            ("@failures", installation.ConsecutiveFailures),
            ("@reason", (object?)installation.QuarantineReason ?? DBNull.Value),
            ("@approval", (object?)installation.ApprovalRef ?? DBNull.Value),
            ("@endpoints", string.Join('\n', installation.GrantedEndpoints ?? [])),
            ("@gpu", installation.GpuGranted ? 1 : 0));

    private async Task<IReadOnlyList<PluginInstallation>> ReadAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var installations = new List<PluginInstallation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            installations.Add(new PluginInstallation(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), Lines(reader.GetString(5)), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? [] : Lines(reader.GetString(12)),
                !reader.IsDBNull(13) && reader.GetInt32(13) == 1));
        }

        return installations;
    }

    private async Task ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
