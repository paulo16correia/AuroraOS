using System.Security.Cryptography;

using Aurora.Adapters.Reasoning;

using Aurora.Adapters.Files;
using Aurora.Core.Abstractions;

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
    /// Interpreter paths the owner named, by runtime — from <c>Aurora:Plugins:Interpreters</c>.
    /// </summary>
    /// <remarks>
    /// Empty by default: Aurora resolves a script plugin's interpreter from the program's shebang
    /// and from <c>PATH</c>. This is the escape hatch the interpreter resolver and <c>doctor</c>
    /// already point at — a per-user Python that an AppContainer can be granted, named explicitly
    /// so resolution does not depend on <c>PATH</c> order. A name here is authoritative: if the
    /// file is not there the plugin is refused rather than a different interpreter being found.
    /// </remarks>
    public IReadOnlyDictionary<string, string> PluginInterpreters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether plugins may run on a platform that cannot confine them (docs/adr/0052).
    /// </summary>
    /// <remarks>
    /// Default <see langword="false"/>. Turning it on means third-party code runs with the
    /// owner's own reach: the network, every file the owner can read — Aurora's database and key
    /// files among them — and every file the owner can write. It is the right answer for somebody
    /// who wrote the plugin themselves, and the wrong one for anything installed.
    /// </remarks>
    public bool AllowUnconfinedPlugins { get; init; }

    /// <summary>
    /// How often Aurora's own upkeep runs, or <see cref="TimeSpan.Zero"/> to not run at all.
    /// </summary>
    /// <remarks>
    /// Zero is for tests, which want a deterministic instance rather than one doing things
    /// underneath them. On a real installation, off means signals never expire, needs never decay
    /// and events are never delivered (docs/adr/0063).
    /// </remarks>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(5);

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

    /// <summary>
    /// What voice may do here (docs/adr/0073).
    /// </summary>
    /// <remarks>
    /// Answering and calling are separate switches and both are off unless somebody turned them
    /// on, because having a telephone number is not a decision to ring people with it. The
    /// destination allowlist is empty unless somebody writes one, and empty allows nothing.
    /// </remarks>
    public required VoiceSettings Voice { get; init; }

    /// <summary>
    /// The environment variable that forbids falling back to the owner's deployment directory.
    /// </summary>
    /// <remarks>
    /// Set by the test assembly and by nothing else. Unconfigured paths defaulting to
    /// <c>%LOCALAPPDATA%\Aurora</c> is the right behaviour for a server somebody started; it is
    /// the wrong behaviour for a test, where the same default silently aims a test at the running
    /// deployment's database, keys and sandbox. A test that forgets to say where its data goes
    /// should fail saying so, not quietly borrow the operator's (docs/adr/0083).
    /// </remarks>
    public const string RequireExplicitPathsVariable = "AURORA_REQUIRE_EXPLICIT_PATHS";

    private static bool RequiresExplicitPaths =>
        Environment.GetEnvironmentVariable(RequireExplicitPathsVariable) == "1";

    /// <summary>Refuses a path that was never configured, where falling back would be wrong.</summary>
    private static void RefuseImplicitPath(string setting)
    {
        if (RequiresExplicitPaths)
        {
            throw new InvalidOperationException(
                $"'{setting}' is not configured and {RequireExplicitPathsVariable}=1, so Aurora "
                + "will not fall back to the deployment directory. Configure it — a temporary "
                + "directory in a test — rather than writing to the owner's live installation.");
        }
    }

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
            RefuseImplicitPath("Aurora:DbPath");

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aurora");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "aurora.db");
        }

        var sandboxFilesEnabled = config.GetValue<bool?>("Aurora:SandboxFilesEnabled") ?? true;

        var sandboxRoot = config["Aurora:SandboxRoot"];
        if (string.IsNullOrWhiteSpace(sandboxRoot))
        {
            RefuseImplicitPath("Aurora:SandboxRoot");

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

        var heartbeatSeconds =
            config.GetValue<int?>("Aurora:HeartbeatSeconds") ?? 300;

        var allowUnconfinedPlugins =
            config.GetValue<bool?>("Aurora:Plugins:AllowUnconfined") ?? false;

        // Interpreter paths the owner named, e.g. Aurora:Plugins:Interpreters:python3. Absent by
        // default; when set, they let a script plugin be confined against a per-user interpreter
        // whose directory an AppContainer can be granted (see PluginInterpreters, doctor).
        var interpreters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection entry in config.GetSection("Aurora:Plugins:Interpreters").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(entry.Value))
            {
                interpreters[entry.Key] = entry.Value;
            }
        }

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

        var voice = VoiceSettings.Default with
        {
            Enabled = config.GetValue<bool?>("Aurora:Voice:Enabled") ?? false,
            OutboundEnabled = config.GetValue<bool?>("Aurora:Voice:OutboundEnabled") ?? false,
            AllowedDestinations =
                config.GetSection("Aurora:Voice:AllowedDestinations").Get<string[]>() ?? [],
            MaxConcurrentSessions =
                config.GetValue<int?>("Aurora:Voice:MaxConcurrentSessions") ?? 2,
            MaxCallDuration =
                config.GetValue<TimeSpan?>("Aurora:Voice:MaxCallDuration") ?? TimeSpan.FromMinutes(15),
        };

        var options = new AuroraServerOptions
        {
            Voice = voice,
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
            AllowUnconfinedPlugins = allowUnconfinedPlugins,
            PluginInterpreters = interpreters,
            HeartbeatInterval = TimeSpan.FromSeconds(Math.Max(0, heartbeatSeconds)),
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
