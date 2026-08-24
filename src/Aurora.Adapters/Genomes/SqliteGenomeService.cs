using System.Globalization;
using Aurora.Adapters.Persistence;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Core.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aurora.Adapters.Genomes;

/// <summary>Resolution and bootstrap of genomes (RFC 036).</summary>
public sealed class SqliteGenomeService : IGenomeService
{
    /// <summary>Fields a variant may never touch: relaxing any of them relaxes a Law.</summary>
    private static readonly string[] Untouchable =
        ["constitution_version", "law_set_version", "mind_schema_version"];

    private readonly SqliteConnectionFactory _factory;
    private readonly IGenomeSigner _signer;
    private readonly ICapabilityRegistry _registry;
    private readonly IClock _clock;

    public SqliteGenomeService(
        SqliteConnectionFactory factory, IGenomeSigner signer, ICapabilityRegistry registry, IClock clock)
    {
        _factory = factory;
        _signer = signer;
        _registry = registry;
        _clock = clock;
    }

    public async Task<Genome> RegisterAsync(Genome genome, CancellationToken ct)
    {
        if (!_signer.Verify(genome))
        {
            throw new GenomeException("Genome signature does not verify.");
        }

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO genome
                (id, family, version, parent_genome_ref, status, constitution_version, law_set_version,
                 base_identity_template_ref, personality_baseline_ref, development_profile_ref,
                 mind_schema_version, allowed_capability_ids, policy_bundle_refs, default_locales,
                 bootstrap_configuration_ref, integrity_hash, signature)
            VALUES (@id, @fam, @ver, @parent, @status, @cv, @lv, @identity, @personality, @development,
                    @msv, @caps, @policies, @locales, @bootstrap, @hash, @sig);
            """;
        command.Parameters.AddWithValue("@id", genome.Id);
        command.Parameters.AddWithValue("@fam", genome.Family);
        command.Parameters.AddWithValue("@ver", genome.Version);
        command.Parameters.AddWithValue("@parent", (object?)genome.ParentGenomeRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", genome.Status);
        command.Parameters.AddWithValue("@cv", genome.ConstitutionVersion);
        command.Parameters.AddWithValue("@lv", genome.LawSetVersion);
        command.Parameters.AddWithValue("@identity", genome.BaseIdentityTemplateRef);
        command.Parameters.AddWithValue("@personality", genome.PersonalityBaselineRef);
        command.Parameters.AddWithValue("@development", genome.DevelopmentProfileRef);
        command.Parameters.AddWithValue("@msv", genome.MindSchemaVersion);
        command.Parameters.AddWithValue("@caps", string.Join(',', genome.AllowedCapabilityIds));
        command.Parameters.AddWithValue("@policies", string.Join(',', genome.PolicyBundleRefs));
        command.Parameters.AddWithValue("@locales", string.Join(',', genome.DefaultLocales));
        command.Parameters.AddWithValue("@bootstrap", genome.BootstrapConfigurationRef);
        command.Parameters.AddWithValue("@hash", genome.IntegrityHash);
        command.Parameters.AddWithValue("@sig", genome.Signature);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return genome;
    }

    public async Task<Genome?> GetAsync(string genomeId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM genome WHERE id = @id;";
        command.Parameters.AddWithValue("@id", genomeId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadGenome(reader) : null;
    }

    public OverrideDecision ValidateOverride(Genome genome, GenomeOverride change)
    {
        if (Untouchable.Contains(change.Field, StringComparer.Ordinal))
        {
            // A variant may restrict capabilities or policies and may never relax the Constitution,
            // the Laws, or the schema they are written against.
            return new OverrideDecision(
                OverrideVerdict.Deny, $"'{change.Field}' is fixed by the genome and cannot be overridden.");
        }

        if (change.Field == "allowed_capability_ids")
        {
            var added = change.Values.Except(genome.AllowedCapabilityIds, StringComparer.Ordinal).ToList();
            return added.Count == 0
                ? new OverrideDecision(OverrideVerdict.Allow, "Restricts the capability set.")
                : new OverrideDecision(
                    OverrideVerdict.Deny,
                    $"Would grant capabilities the genome does not carry: {string.Join(", ", added)}.");
        }

        if (change.Field == "policy_bundle_refs")
        {
            var removed = genome.PolicyBundleRefs.Except(change.Values, StringComparer.Ordinal).ToList();
            return removed.Count == 0
                ? new OverrideDecision(OverrideVerdict.Allow, "Adds policy; does not remove any.")
                : new OverrideDecision(
                    OverrideVerdict.Deny, $"Would remove policy bundles: {string.Join(", ", removed)}.");
        }

        // Not obviously a restriction. The resolver refuses to guess; a person decides.
        return new OverrideDecision(OverrideVerdict.Review, $"'{change.Field}' needs review.");
    }

    public async Task<GenomeResolution> ResolveAsync(
        string genomeId, InstallationContext context, CancellationToken ct)
    {
        Genome genome = await GetAsync(genomeId, ct).ConfigureAwait(false)
            ?? throw new GenomeException("Unknown genome.");

        // RFC 036: an invalid signature means no instance is created.
        if (!_signer.Verify(genome))
        {
            throw new GenomeException("Genome signature does not verify; refusing to create an instance.");
        }

        if (genome.Status != GenomeStatus.Released)
        {
            throw new GenomeException($"Genome is {genome.Status}; only a RELEASED genome births an instance.");
        }

        var effective = genome.AllowedCapabilityIds.ToList();
        var denied = new List<string>();

        foreach (GenomeOverride change in context.Overrides)
        {
            OverrideDecision decision = ValidateOverride(genome, change);
            if (decision.Verdict != OverrideVerdict.Allow)
            {
                denied.Add($"{change.Field}: {decision.Verdict} — {decision.Reason}");
                continue;
            }

            if (change.Field == "allowed_capability_ids")
            {
                effective = effective.Intersect(change.Values, StringComparer.Ordinal).ToList();
            }
        }

        var resolution = new GenomeResolution(
            Guid.NewGuid().ToString("N"),
            genome.Id,
            context.InstallationId,
            context.SelectedVariants,
            effective,
            denied,
            EffectiveHash: Hashing.Sha256Hex(
                $"{genome.IntegrityHash}\n{context.InstallationId}\n"
                + $"{string.Join(',', context.SelectedVariants)}\n{string.Join(',', effective)}"),
            ResolvedAtUtc: _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Resolver: "kernel");

        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO genome_resolution
                (id, genome_id, installation_id, selected_variants, effective_capability_ids,
                 denied_overrides, effective_hash, resolved_at_utc, resolver)
            VALUES (@id, @gid, @iid, @variants, @caps, @denied, @hash, @at, @resolver);
            """;
        command.Parameters.AddWithValue("@id", resolution.Id);
        command.Parameters.AddWithValue("@gid", resolution.GenomeId);
        command.Parameters.AddWithValue("@iid", resolution.InstallationId);
        command.Parameters.AddWithValue("@variants", string.Join(',', resolution.SelectedVariants));
        command.Parameters.AddWithValue("@caps", string.Join(',', resolution.EffectiveCapabilityIds));
        command.Parameters.AddWithValue("@denied", string.Join('\n', resolution.DeniedOverrides));
        command.Parameters.AddWithValue("@hash", resolution.EffectiveHash);
        command.Parameters.AddWithValue("@at", resolution.ResolvedAtUtc);
        command.Parameters.AddWithValue("@resolver", resolution.Resolver);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return resolution;
    }

    public async Task<GenomeResolution?> GetResolutionAsync(string resolutionId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM genome_resolution WHERE id = @id;";
        command.Parameters.AddWithValue("@id", resolutionId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new GenomeResolution(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Split(reader.GetString(3)), Split(reader.GetString(4)),
            reader.GetString(5).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(6), reader.GetString(7), reader.GetString(8));
    }

    public async Task<BootstrapPlan> BootstrapAsync(string resolutionId, CancellationToken ct)
    {
        GenomeResolution resolution = await GetResolutionAsync(resolutionId, ct).ConfigureAwait(false)
            ?? throw new GenomeException("Unknown resolution.");

        var available = new List<string>();
        var missing = new List<string>();

        foreach (var capabilityId in resolution.EffectiveCapabilityIds)
        {
            if (_registry.TryGet(capabilityId, out _))
            {
                available.Add(capabilityId);
            }
            else
            {
                missing.Add(capabilityId);
            }
        }

        // RFC 036: a missing capability degrades or blocks the bootstrap. It never invents a
        // replacement, because a substitute nobody asked for is worse than a smaller instance.
        var status = missing.Count == 0
            ? BootstrapStatus.Ready
            : available.Count == 0 ? BootstrapStatus.Blocked : BootstrapStatus.Degraded;

        var steps = new List<string>
        {
            $"apply genome {resolution.GenomeId}",
            $"register {available.Count} capability(ies)",
            "create initial Mind from the identity template",
            "record the effective genome in Mind State and Life History",
        };

        if (missing.Count > 0)
        {
            steps.Add($"report {missing.Count} unavailable capability(ies) without substituting them");
        }

        return new BootstrapPlan(resolutionId, status, steps, available, missing);
    }

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static Genome ReadGenome(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.GetString(7), r.GetString(8), r.GetString(9), r.GetInt32(10),
        Split(r.GetString(11)), Split(r.GetString(12)), Split(r.GetString(13)),
        r.GetString(14), r.GetString(15), r.GetString(16));
}
