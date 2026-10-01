using Aurora.Adapters.Diagnostics;
using Aurora.Adapters.Events;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Time;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Aurora.Server;

/// <summary>
/// <c>aurora doctor</c> — the deployment preflight (F-2, docs/adr/0079).
/// </summary>
/// <remarks>
/// A separate command from <c>health</c>, which asks a <i>running</i> instance whether it is live.
/// Doctor runs before that, against the files and configuration on disk, and turns the failures an
/// operator would otherwise hit at first plugin call — a key that is not owner-only, an interpreter
/// an AppContainer cannot reach, a missing secret — into an explicit report they can act on.
/// <para>
/// It renders; the checks themselves live in <see cref="Preflight"/>, which is where they are
/// tested. It reads state and never changes it, and it never prints a secret value — only names,
/// paths and outcomes. The exit code is 1 if any check FAILed, so it can gate a scripted start.
/// </para>
/// </remarks>
public static class DoctorConsole
{
    public static bool TryHandle(string[] args, AuroraServerOptions options)
    {
        if (args.FirstOrDefault() != "doctor")
        {
            return false;
        }

        var checks = new List<PreflightCheck>();

        checks.AddRange(Preflight.Filesystem(options.DbPath, options.SandboxRoot));
        checks.AddRange(Preflight.KeyMaterial(
        [
            new Preflight.KeyFile("audit", options.AuditKeyPath),
            new Preflight.KeyFile("vault", options.VaultKeyPath),
            new Preflight.KeyFile("genome", options.GenomeKeyPath),
            new Preflight.KeyFile("snapshot", options.SnapshotKeyPath),
            new Preflight.KeyFile("deliberation", options.DeliberationKeyPath),
            new Preflight.KeyFile("plugin", options.PluginKeyPath),
        ]));
        checks.AddRange(Preflight.Sandbox(PluginSandbox.ForThisMachine(), options.PluginRoot));
        checks.AddRange(Preflight.Configuration(options.AllowUnconfinedPlugins));

        try
        {
            checks.AddRange(PluginChecks(options));
        }
        catch (Exception unavailable) when (unavailable is SqliteException or IOException)
        {
            checks.Add(new PreflightCheck("plugins", PreflightStatus.Warn,
                $"could not read installed plugins: {unavailable.GetType().Name}"));
        }

        checks.Add(new PreflightCheck("native dependencies", PreflightStatus.Info,
            "not started here; a plugin's own status capability (for example voice.status or "
            + "discord.voice.status) reports libopus and the local speech engines once Aurora is running"));

        return Render(checks);
    }

    private static IReadOnlyList<PreflightCheck> PluginChecks(AuroraServerOptions options)
    {
        var factory = new SqliteConnectionFactory(options.DbPath);
        new SqliteDatabase(factory).Initialize();

        var clock = new SystemClock();
        var bus = new SqliteEventBus(
            factory, new SqliteOutbox(new DeclaredEventCatalogue(), clock), clock);
        var host = new SubprocessPluginHost(
            options.PluginRoot, PluginSandbox.ForThisMachine(), options.AllowUnconfinedPlugins);

        var registry = new SqlitePluginRegistry(
            factory, host, bus, LocalKeyFile.LoadOrCreate(options.PluginKeyPath, "Plugin"), clock);

        IReadOnlyList<PluginInstallation> installed =
            registry.ListAsync(CancellationToken.None).GetAwaiter().GetResult();

        return Preflight.Plugins(
            installed,
            new PluginInterpreters(options.PluginInterpreters),
            (pluginId, name) => SecretPresent(factory, pluginId, name));
    }

    /// <summary>
    /// Whether a plugin's declared secret is provisioned — presence only, by purpose, never the value.
    /// </summary>
    private static bool SecretPresent(SqliteConnectionFactory factory, string pluginId, string name)
    {
        using SqliteConnection connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM vault_item WHERE purpose = @purpose AND status = @active;";
        command.Parameters.AddWithValue("@purpose", VaultPluginSecretSource.PurposeOf(pluginId, name));
        command.Parameters.AddWithValue("@active", VaultItemStatus.Active);

        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>Prints the report and returns true; sets a non-zero exit code on any failure.</summary>
    private static bool Render(IReadOnlyList<PreflightCheck> checks)
    {
        Console.WriteLine("[Aurora] doctor — deployment preflight");
        Console.WriteLine();

        foreach (PreflightCheck check in checks)
        {
            Console.WriteLine($"  {Label(check.Status)}  {check.Component}: {check.Detail}");
        }

        var fails = checks.Count(c => c.Status == PreflightStatus.Fail);
        var warns = checks.Count(c => c.Status == PreflightStatus.Warn);

        Console.WriteLine();
        Console.WriteLine(
            $"[Aurora] {checks.Count} checks — {fails} FAIL, {warns} WARN, "
            + $"{checks.Count(c => c.Status == PreflightStatus.Pass)} PASS.");

        if (fails > 0)
        {
            Console.WriteLine(
                "[Aurora] Resolve the FAIL items before running plugins. WARN items are worth a look "
                + "but do not block a controlled demo.");
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static string Label(PreflightStatus status) => status switch
    {
        PreflightStatus.Pass => "PASS",
        PreflightStatus.Warn => "WARN",
        PreflightStatus.Fail => "FAIL",
        _ => "INFO",
    };
}
