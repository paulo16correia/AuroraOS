using System.Security.Cryptography;

using Aurora.Adapters.Reasoning;

using Aurora.Adapters.Files;

namespace Aurora.Server;

/// <summary>
/// Runtime options resolved from configuration/environment. The bearer token is required; if none
/// is supplied one is generated for the run (and printed once) so the server is never unprotected.
/// </summary>
public sealed class AuroraServerOptions
{
    public required string BearerToken { get; init; }

    public int Port { get; init; } = 5099;

    public required string DbPath { get; init; }

    /// <summary>Root of the writable sandbox for <c>files.write_sandbox</c> (docs/adr/0003).</summary>
    public required string SandboxRoot { get; init; }

    /// <summary>
    /// Whether the sandbox file capabilities are offered in the catalog.
    /// </summary>
    /// <remarks>
    /// Frozen off by the re-baseline (docs/adr/0012) because they were built at step 8 before
    /// steps 3–7 existed; unfrozen by the owner's decision in docs/adr/0037, now that those steps
    /// do exist and the review's conditions are closed. Default true.
    /// <para>
    /// Still a switch, because turning them off is a legitimate thing to want: an instance that
    /// has no business touching files should not offer to. Nothing about the switch is what makes
    /// them safe — the approval gate is, and that gate applies on every single call.
    /// </para>
    /// </remarks>
    public bool SandboxFilesEnabled { get; init; } = true;

    /// <summary>
    /// How long a reservation may sit in EXECUTING before startup reconciliation calls it
    /// indeterminate. Long enough that a slow-but-live execution is never stolen from itself.
    /// </summary>
    public TimeSpan ExecutingStaleAfter { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>File holding the key that encrypts Mind State snapshots (docs/adr/0018).</summary>
    public required string SnapshotKeyPath { get; init; }

    /// <summary>File holding the ECDSA key that signs genome manifests (docs/adr/0017).</summary>
    public required string GenomeKeyPath { get; init; }

    /// <summary>Where plugin working directories live, one per plugin.</summary>
    public required string PluginRoot { get; init; }

    /// <summary>File holding the key that verifies plugin manifest signatures (docs/adr/0048).</summary>
    public required string PluginKeyPath { get; init; }

    /// <summary>
    /// File holding the key that encrypts deliberation traces (docs/adr/0040).
    /// </summary>
    /// <remarks>
    /// Its own key, not the vault's. They protect different things for different reasons and last
    /// for different lengths of time; sharing one would mean a trace kept for a week and a secret
    /// kept indefinitely stand or fall together.
    /// </remarks>
    public required string DeliberationKeyPath { get; init; }

    /// <summary>File holding the key that encrypts vault secrets at rest (docs/adr/0014).</summary>
    public required string VaultKeyPath { get; init; }

    /// <summary>File holding the operator passphrase verifier (docs/adr/0011).</summary>
    public required string PassphrasePath { get; init; }

    /// <summary>File holding the HMAC key that signs the audit chain (docs/adr/0005).</summary>
    public required string AuditKeyPath { get; init; }

    /// <summary>File mirroring the audit head, so a truncated tail is detectable.</summary>
    public required string AuditAnchorPath { get; init; }

    public static AuroraServerOptions FromConfiguration(IConfiguration config)
    {
        var token = config["Aurora:BearerToken"]
            ?? Environment.GetEnvironmentVariable("AURORA_BEARER_TOKEN");
        var generated = false;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            generated = true;
        }

        var port = config.GetValue<int?>("Aurora:Port") ?? 5099;

        var dbPath = config["Aurora:DbPath"];
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aurora");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "aurora.db");
        }

        var sandboxFilesEnabled = config.GetValue<bool?>("Aurora:SandboxFilesEnabled") ?? true;

        var sandboxRoot = config["Aurora:SandboxRoot"];
        if (string.IsNullOrWhiteSpace(sandboxRoot))
        {
            sandboxRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aurora", "sandbox");
        }

        Directory.CreateDirectory(sandboxRoot);

        // At creation, not at first use. The writer restricts the root when it is constructed, but
        // it is constructed lazily — so on an instance that never touches a file, the sandbox would
        // sit world-readable indefinitely, which is exactly the precondition the path hardening
        // rests on (docs/adr/0036).
        SandboxGuard.RestrictToOwner(sandboxRoot);

        // Default the audit key and anchor beside the database, but keep them configurable so an
        // operator can put the key somewhere the database's own backups do not reach.
        var snapshotKeyPath = config["Aurora:SnapshotKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.snapshot.key");

        var genomeKeyPath = config["Aurora:GenomeKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.genome.key");

        var deliberationKeyPath = config["Aurora:DeliberationKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.deliberation.key");

        var pluginRoot = config["Aurora:PluginRoot"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "plugins");

        var pluginKeyPath = config["Aurora:PluginKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.plugin.key");

        var vaultKeyPath = config["Aurora:VaultKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.vault.key");

        var passphrasePath = config["Aurora:PassphrasePath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.passphrase.json");

        var auditKeyPath = config["Aurora:AuditKeyPath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "aurora.audit.key");
        // Derived from the database file, not the directory: two databases side by side must not
        // share one anchor, or each would read the other's head as evidence of truncation.
        var auditAnchorPath = config["Aurora:AuditAnchorPath"]
            ?? Path.GetFullPath(dbPath) + ".anchor";

        var options = new AuroraServerOptions
        {
            BearerToken = token,
            Port = port,
            DbPath = dbPath,
            SandboxRoot = sandboxRoot,
            SandboxFilesEnabled = sandboxFilesEnabled,
            SnapshotKeyPath = snapshotKeyPath,
            GenomeKeyPath = genomeKeyPath,
            DeliberationKeyPath = deliberationKeyPath,
            PluginRoot = pluginRoot,
            PluginKeyPath = pluginKeyPath,
            VaultKeyPath = vaultKeyPath,
            PassphrasePath = passphrasePath,
            AuditKeyPath = auditKeyPath,
            AuditAnchorPath = auditAnchorPath,
        };
        if (generated)
        {
            Console.WriteLine($"[Aurora] No bearer token configured; generated one for this run: {token}");
        }

        return options;
    }
}
