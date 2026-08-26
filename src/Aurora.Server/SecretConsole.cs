using Aurora.Adapters.Persistence;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Time;
using Aurora.Adapters.Vault;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;
using Aurora.Server.Security;

namespace Aurora.Server;

/// <summary>
/// Putting a secret where a plugin can be given it, without it passing through anything that keeps
/// a copy.
/// </summary>
/// <remarks>
/// A plugin declares the secrets it needs by name and Aurora refuses to start one whose secret is
/// missing — so until this existed, a plugin needing a credential could be installed and could
/// never run. The gap was found by trying to write the instructions for using one.
/// <para>
/// The value is read from the terminal with echo off and never taken as an argument. A command line
/// lands in shell history, in the process table, and in whatever the terminal is logging: three
/// places a bot token would sit in plain text for as long as the machine lives.
/// </para>
/// </remarks>
public static class SecretConsole
{
    public static bool TryHandle(string[] args, AuroraServerOptions options)
    {
        if (args.FirstOrDefault() != "secret")
        {
            return false;
        }

        var verb = args.Length > 1 ? args[1] : "help";

        return verb switch
        {
            "set" => Set(Arg(args, 2), Arg(args, 3), options),
            "list" => List(options),
            "revoke" => Revoke(Arg(args, 2), Arg(args, 3), options),
            _ => Help(),
        };
    }

    private static string? Arg(string[] args, int at) => args.Length > at ? args[at] : null;

    private static bool Help()
    {
        Console.WriteLine("""
            [Aurora] Secrets a plugin needs to run.

              secret set <plugin_id> <name>      supply one, typed rather than pasted into a command
              secret list                        which are on file, never their values
              secret revoke <plugin_id> <name>   withdraw one; the plugin stops being able to start

            The value is asked for on the next line with the terminal's echo off. It is never taken
            as an argument, because a command line is kept in shell history and is visible in the
            process table to anything else running as you.
            """);

        return true;
    }

    private static bool Set(string? pluginId, string? name, AuroraServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("[Aurora] Usage: secret set <plugin_id> <name>");
            return true;
        }

        Console.WriteLine($"[Aurora] Supplying '{name}' for {pluginId}.");
        Console.WriteLine("         Aurora hands this to the plugin's process and keeps it encrypted at rest.");
        Console.Write("         Value (not shown): ");

        var value = ReadHidden();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine("[Aurora] Nothing was supplied.");
            return true;
        }

        IVault vault = Vault(options);
        var purpose = VaultPluginSecretSource.PurposeOf(pluginId, name);

        SecretReference? existing = vault
            .FindByPurposeAsync(purpose, CancellationToken.None).GetAwaiter().GetResult();

        SecretReference stored = existing is null
            ? vault.PutAsync(purpose, [pluginId], value, null, CancellationToken.None)
                .GetAwaiter().GetResult()

            // Rotated rather than added beside it, so there is one answer to "what is the token"
            // and replacing it does not leave the old one leasable.
            : vault.RotateAsync(existing.Id, value, null, CancellationToken.None)
                .GetAwaiter().GetResult();

        Console.WriteLine(
            existing is null
                ? $"[Aurora] Stored. {pluginId} can be started now."
                : $"[Aurora] Replaced. Restart the plugin for it to take effect: aurora plugin restart {pluginId}");

        Console.WriteLine($"         Filed under {stored.Purpose}, leasable only by {pluginId}.");

        return true;
    }

    /// <summary>
    /// Which plugin secrets are on file, read straight from the table.
    /// </summary>
    /// <remarks>
    /// The vault deliberately offers no "list everything" on its interface — a method returning
    /// every reference is one call away from a method returning every value, and RFC 040 keeps the
    /// value out of the domain entirely. This reads the reference columns and no others; there is
    /// no path from here to a plaintext.
    /// </remarks>
    private static bool List(AuroraServerOptions options)
    {
        var factory = new SqliteConnectionFactory(options.DbPath);
        new SqliteDatabase(factory).Initialize();

        using Microsoft.Data.Sqlite.SqliteConnection connection =
            factory.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT purpose, status FROM vault_item
             WHERE purpose LIKE 'plugin/%' ORDER BY purpose;
            """;

        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        var any = false;

        while (reader.Read())
        {
            if (!any)
            {
                Console.WriteLine(
                    "[Aurora] Plugin secrets on file. Values are not shown and cannot be.");
                any = true;
            }

            Console.WriteLine($"           {reader.GetString(0),-56} {reader.GetString(1)}");
        }

        if (!any)
        {
            Console.WriteLine("[Aurora] No plugin secrets on file.");
        }

        return true;
    }

    private static bool Revoke(string? pluginId, string? name, AuroraServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("[Aurora] Usage: secret revoke <plugin_id> <name>");
            return true;
        }

        IVault vault = Vault(options);

        SecretReference? found = vault
            .FindByPurposeAsync(VaultPluginSecretSource.PurposeOf(pluginId, name), CancellationToken.None)
            .GetAwaiter().GetResult();

        if (found is null)
        {
            Console.WriteLine($"[Aurora] No '{name}' on file for {pluginId}.");
            return true;
        }

        vault.RevokeAsync(found.Id, CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine($"""
            [Aurora] Revoked. {pluginId} will refuse to start until a value is supplied again.

            The record stays, revoked, rather than being deleted — what this instance was once
            given is part of how it got here.
            """);

        return true;
    }

    /// <summary>
    /// Reads a line without echoing it.
    /// </summary>
    /// <remarks>
    /// Backspace is handled because somebody typing a fifty-character token will mistype it, and a
    /// prompt that cannot be corrected is a prompt people paste into instead.
    /// </remarks>
    private static string ReadHidden()
    {
        if (Console.IsInputRedirected)
        {
            // Piped in, which is how a script supplies one. There is no terminal to hide it from.
            return Console.ReadLine() ?? string.Empty;
        }

        var typed = new System.Text.StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }

    private static IVault Vault(AuroraServerOptions options)
    {
        var factory = new SqliteConnectionFactory(options.DbPath);
        new SqliteDatabase(factory).Initialize();

        var clock = new SystemClock();

        return new SqliteVault(
            factory,
            new AesGcmSecretProtector(LocalKeyFile.LoadOrCreate(options.VaultKeyPath, "Vault")),
            clock,
            new SqliteAuditStore(
                factory, clock,
                LocalKeyFile.LoadOrCreate(options.AuditKeyPath, "Audit"),
                new AuditAnchorFile(options.AuditAnchorPath)),
            new LocalPrincipalAccessor(),
            VaultOptions.Default);
    }
}
