using System.Text.Json;
using Aurora.Adapters.Events;
using Aurora.Adapters.Persistence;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Adapters.Time;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Server;

/// <summary>
/// Writing, checking and installing a plugin, from Aurora's own console.
/// </summary>
/// <remarks>
/// Not an HTTP endpoint, and deliberately. Installing a plugin grants third-party code a place in
/// Aurora's catalogue; an endpoint that did it would let whoever holds the agent's bearer token
/// extend Aurora, which is the one thing the token is not for. The console is reachable by whoever
/// already has the machine — which is the same person who would have to approve it anyway.
/// </remarks>
public static class PluginConsole
{
    public static bool TryHandle(string[] args, AuroraServerOptions options)
    {
        if (args.FirstOrDefault() != "plugin")
        {
            return false;
        }

        var verb = args.Length > 1 ? args[1] : "help";
        var argument = args.Length > 2 ? args[2] : null;

        return verb switch
        {
            "new" => Scaffold(argument),
            "validate" => Validate(argument, options),
            "install" => Install(argument, options),
            "list" => List(options),
            "disable" => Disable(argument, options),
            "release" => Release(argument, options),
            "remove" => Remove(argument, options),
            _ => Help(),
        };
    }

    private static bool Help()
    {
        Console.WriteLine("""
            [Aurora] plugin <verb>

              new <directory>       write a working plugin to copy from
              validate <directory>  check a plugin.json and say what is wrong with it
              install <directory>   verify, seal and install it; takes effect on restart
              list                  what is installed, and its state
              disable <plugin_id>   stop it running, without forgetting it
              release <plugin_id>   let a held plugin run again, after you have looked
              remove <plugin_id>    end it for good and take back what it was granted

            A plugin is a folder holding a plugin.json and a program. Aurora runs the program,
            writes the call to its standard input as JSON, and reads the result from its standard
            output. See docs/guides/writing-a-plugin.md.
            """);

        return true;
    }

    private static bool Scaffold(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            Console.WriteLine("[Aurora] Usage: plugin new <directory>");
            return true;
        }

        var full = Path.GetFullPath(directory);

        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            // Scaffolding over somebody's work is the kind of help nobody asks for twice.
            Console.WriteLine($"[Aurora] {full} already has something in it. Pick an empty folder.");
            return true;
        }

        Directory.CreateDirectory(full);
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));

        File.WriteAllText(Path.Combine(full, "plugin.json"), Template(name));
        var script = Path.Combine(full, "run.py");
        File.WriteAllText(script, RunTemplate);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        Console.WriteLine($"""
            [Aurora] Wrote a working plugin to {full}

              plugin.json   what it offers, and what it needs
              run.py        the program Aurora runs

            Try it:  plugin validate {directory}
            """);

        return true;
    }

    private static bool Validate(string? directory, AuroraServerOptions options)
    {
        if (!TryRead(directory, options, out PluginManifest? manifest, out var full))
        {
            return true;
        }

        Console.WriteLine($"[Aurora] {manifest!.PluginId} {manifest.Version} by {manifest.Publisher}");

        foreach (PluginCapability capability in manifest.Capabilities)
        {
            Console.WriteLine(
                $"  {capability.Key} — {capability.Risk.ToString().ToUpperInvariant()}"
                + $"{(capability.ApprovalRequired ? ", approval required" : "")}"
                + $"{(capability.Reversible ? ", reversible" : "")}"
                + $"{(capability.Effects.Count > 0 ? $" [{string.Join(", ", capability.Effects)}]" : "")}");
        }

        var executable = Path.Combine(full!, manifest.Executable!);

        if (!File.Exists(executable))
        {
            Console.WriteLine($"  ! executable not found: {manifest.Executable}");
            return true;
        }

        Console.WriteLine($"[Aurora] This manifest is valid. Install it with: plugin install {directory}");
        return true;
    }

    private static bool Install(string? directory, AuroraServerOptions options)
    {
        if (!TryRead(directory, options, out PluginManifest? manifest, out var full))
        {
            return true;
        }

        var executable = Path.Combine(full!, manifest!.Executable!);

        if (!File.Exists(executable))
        {
            Console.WriteLine($"[Aurora] executable not found: {executable}");
            return true;
        }

        // The absolute path is what Aurora will run, resolved once at install rather than every
        // call: a manifest whose relative path resolves differently later is a different plugin.
        PluginManifest sealed_ = Seal(manifest with { Executable = executable }, options);

        Console.WriteLine($"""
            [Aurora] About to install {sealed_.PluginId} {sealed_.Version} by {sealed_.Publisher}

              runs        {executable}
              permissions {(sealed_.RequiredPermissions.Count == 0 ? "none" : string.Join(", ", sealed_.RequiredPermissions))}
              data class  up to {sealed_.MaxDataClass}
              network     {(sealed_.NetworkEndpoints.Count == 0 ? "none declared" : string.Join(", ", sealed_.NetworkEndpoints))}
            """);

        Console.Write("[Aurora] Grant exactly these permissions and install? [y/N] ");

        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[Aurora] Nothing was installed.");
            return true;
        }

        // Asked separately, because it is a separate decision. Somebody can reasonably want a
        // plugin's capabilities and not want it talking to the internet, and rolling the two into
        // one question means the narrower answer cannot be given (docs/adr/0067).
        var endpoints = new List<string>();

        if (sealed_.NetworkEndpoints.Count > 0)
        {
            Console.WriteLine($"""

                [Aurora] It also asks to reach {string.Join(", ", sealed_.NetworkEndpoints)}.

                Granting this lets the plugin send whatever it holds to those hosts. Aurora cannot
                narrow it further: no sandbox on any platform filters outbound traffic by hostname,
                so what you are agreeing to is the plugin having a network, with these names as the
                record of what it said it would use.
                """);

            Console.Write("[Aurora] Let it reach the network? [y/N] ");

            if (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                endpoints.AddRange(sealed_.NetworkEndpoints);
            }
            else
            {
                Console.WriteLine(
                    "[Aurora] Installing without the network. Anything needing it will be refused.");
            }
        }

        var gpu = false;

        if (sealed_.RequiresGpu)
        {
            Console.WriteLine("""

                [Aurora] It also asks for the graphics processor.

                That is compute rather than your files, which is why it can be granted at all. It
                is also a large driver surface reached from third-party code, which is why it is
                not granted by default. Local speech recognition is roughly twenty times slower
                without it — slow enough to be unusable — so for that the answer is probably yes,
                and for a plugin with no such need it is probably no.
                """);

            Console.Write("[Aurora] Let it use the GPU? [y/N] ");
            gpu = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

            if (!gpu)
            {
                Console.WriteLine(
                    "[Aurora] Installing without it. Anything needing it will be refused.");
            }
        }

        if ((sealed_.RequiredSecrets ?? []).Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("[Aurora] It needs secrets before it will start:");

            foreach (PluginSecretRequirement secret in sealed_.RequiredSecrets!)
            {
                Console.WriteLine($"           {secret.Name} — {secret.Purpose}");
                Console.WriteLine(
                    $"           aurora secret set {sealed_.PluginId} {secret.Name}");
            }
        }

        Registry registry = Open(options);

        PluginInstallation installed = registry.Plugins.InstallAsync(
            sealed_, sealed_.RequiredPermissions, endpoints, gpu,
            $"console/{Environment.UserName}", CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine($"""
            [Aurora] Installed. Status {installed.Status}.

            Its capabilities join the catalogue when Aurora next starts — the catalogue is read
            once at startup so a running instance cannot change shape underneath a call in flight.
            """);

        return true;
    }

    private static bool List(AuroraServerOptions options)
    {
        Registry registry = Open(options);

        IReadOnlyList<PluginInstallation> installed =
            registry.Plugins.ListAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (installed.Count == 0)
        {
            Console.WriteLine("[Aurora] Nothing installed. Write one with: plugin new <directory>");
            return true;
        }

        foreach (PluginInstallation plugin in installed)
        {
            Console.WriteLine(
                $"  {plugin.PluginId} {plugin.Version} by {plugin.Publisher} — {plugin.Status}"
                + (plugin.QuarantineReason is { } why ? $" ({why})" : string.Empty));
        }

        return true;
    }

    private static bool Disable(string? pluginId, AuroraServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            Console.WriteLine("[Aurora] Usage: plugin disable <plugin_id>");
            return true;
        }

        Registry registry = Open(options);

        PluginInstallation? installed =
            registry.Plugins.GetAsync(pluginId, CancellationToken.None).GetAwaiter().GetResult();

        if (installed is null)
        {
            Console.WriteLine($"[Aurora] {pluginId} is not installed.");
            return true;
        }

        registry.Plugins.DisableAsync(
            installed.Id, $"console/{Environment.UserName}", CancellationToken.None)
            .GetAwaiter().GetResult();

        Console.WriteLine($"[Aurora] {pluginId} is disabled. It is not forgotten, and can be released.");
        return true;
    }

    /// <summary>
    /// Ends a quarantine, which is a decision somebody makes after looking.
    /// </summary>
    /// <remarks>
    /// A plugin that fails enough times in a row is held by the circuit breaker, and until this
    /// existed the only way back was the control panel — so an owner whose plugin was held had to
    /// start a second server to release it. Install, disable and remove all lived here; this was
    /// the one step in the lifecycle that did not.
    /// </remarks>
    private static bool Release(string? pluginId, AuroraServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            Console.WriteLine("[Aurora] Usage: plugin release <plugin_id>");
            return true;
        }

        Registry registry = Open(options);

        PluginInstallation? held =
            registry.Plugins.GetAsync(pluginId, CancellationToken.None).GetAwaiter().GetResult();

        if (held is null)
        {
            Console.WriteLine($"[Aurora] {pluginId} is not installed.");
            return true;
        }

        if (held.Status != InstallationStatus.Quarantined)
        {
            Console.WriteLine($"[Aurora] {pluginId} is {held.Status}; there is no quarantine to end.");
            return true;
        }

        Console.WriteLine($"""
            [Aurora] {pluginId} is held: {held.QuarantineReason ?? "no reason recorded"}

            A quarantine ends because somebody looked and decided, not because time passed. If you
            have not found out why it was failing, releasing it will hold it again.
            """);

        Console.Write("[Aurora] Let it run again? [y/N] ");

        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[Aurora] Left held.");
            return true;
        }

        PluginInstallation released = registry.Plugins.ReleaseAsync(
            held.Id, $"console/{Environment.UserName}", $"console/{Environment.UserName}",
            CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine(
            $"[Aurora] {pluginId} is {released.Status}. Its failure count is back to zero, and its "
            + "capabilities rejoin the catalogue when Aurora next starts.");

        return true;
    }

    private static bool Remove(string? pluginId, AuroraServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            Console.WriteLine("[Aurora] Usage: plugin remove <plugin_id>");
            return true;
        }

        Registry registry = Open(options);

        PluginInstallation? installed =
            registry.Plugins.GetAsync(pluginId, CancellationToken.None).GetAwaiter().GetResult();

        if (installed is null)
        {
            Console.WriteLine($"[Aurora] {pluginId} is not installed.");
            return true;
        }

        Console.Write($"[Aurora] Remove {pluginId} and take back its permissions? [y/N] ");

        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[Aurora] Nothing was removed.");
            return true;
        }

        registry.Plugins.RemoveAsync(
            installed.Id, $"console/{Environment.UserName}", CancellationToken.None)
            .GetAwaiter().GetResult();

        Console.WriteLine(
            $"[Aurora] {pluginId} is removed. The record stays, because what Aurora once ran is "
            + "part of how this instance got here, and it cannot be released back. Install it "
            + "again if you want it.");

        return true;
    }

    private static bool TryRead(
        string? directory, AuroraServerOptions options,
        out PluginManifest? manifest, out string? full)
    {
        manifest = null;
        full = null;

        if (string.IsNullOrWhiteSpace(directory))
        {
            Console.WriteLine("[Aurora] Usage: plugin validate|install <directory>");
            return false;
        }

        full = Path.GetFullPath(directory);
        var path = Path.Combine(full, "plugin.json");

        if (!File.Exists(path))
        {
            Console.WriteLine($"[Aurora] No plugin.json in {full}");
            return false;
        }

        PluginManifestRead read = PluginManifestReader.Read(
            File.ReadAllText(path), BuiltInActionIds(options));

        if (!read.Ok)
        {
            // Every problem at once. Somebody fixing five mistakes should need one round trip.
            Console.WriteLine($"[Aurora] {path} cannot be used yet:");

            foreach (var problem in read.Problems)
            {
                Console.WriteLine($"  - {problem}");
            }

            return false;
        }

        manifest = read.Manifest;
        return true;
    }

    /// <summary>
    /// Aurora's own action ids, so a plugin cannot claim one.
    /// </summary>
    /// <remarks>
    /// Built from the same registration the server uses, rather than a list kept beside it: a
    /// hand-maintained copy would go stale on the day somebody adds a capability, which is exactly
    /// the day a plugin could start shadowing it.
    /// </remarks>
    private static IReadOnlyList<string> BuiltInActionIds(AuroraServerOptions options) =>
        [.. ServiceRegistration.BuiltInCapabilityIds(options)];

    /// <summary>
    /// Signs the manifest with Aurora's own key and hashes what was signed.
    /// </summary>
    /// <remarks>
    /// The owner is the trust anchor here, not a marketplace: nothing about a publisher's identity
    /// is verifiable on a machine with no network, so a signature from one would be a ceremony that
    /// proves nothing. What this does prove is that the manifest has not changed since the moment
    /// somebody read it and said yes — which is the property that actually matters, and the one
    /// re-verification checks on every call.
    /// </remarks>
    private static PluginManifest Seal(PluginManifest manifest, AuroraServerOptions options)
    {
        var key = LocalKeyFile.LoadOrCreate(options.PluginKeyPath, "Plugin");

        PluginManifest signed = manifest with
        {
            Signature = SqlitePluginRegistry.Sign(
                key, manifest.PluginId, manifest.Version, manifest.Publisher),
        };

        return signed with { IntegrityHash = SqlitePluginRegistry.HashOf(signed) };
    }

    /// <summary>A registry over the same database the server uses, for one console command.</summary>
    private sealed record Registry(SqlitePluginRegistry Plugins);

    private static Registry Open(AuroraServerOptions options)
    {
        var factory = new SqliteConnectionFactory(options.DbPath);
        new SqliteDatabase(factory).Initialize();

        var clock = new SystemClock();
        var bus = new SqliteEventBus(
            factory, new SqliteOutbox(new DeclaredEventCatalogue(), clock), clock);

        var host = new SubprocessPluginHost(
            options.PluginRoot, PluginSandbox.ForThisMachine(), options.AllowUnconfinedPlugins);

        return new Registry(new SqlitePluginRegistry(
            factory, host, bus,
            LocalKeyFile.LoadOrCreate(options.PluginKeyPath, "Plugin"), clock));
    }

    private static string Template(string name) =>
        $$"""
        {
          "plugin_id": "you/{{name}}",
          "version": "1.0.0",
          "publisher": "you",
          "executable": "run.py",
          "max_data_class": "PRIVATE",
          "documentation_ref": "README.md",
          "required_permissions": [],
          "capabilities": [
            {
              "key": "{{name}}.greet",
              "title": "Greet somebody",
              "description": "Returns a greeting. Replace this with something useful.",
              "input_schema": {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "type": "object",
                "additionalProperties": false,
                "required": ["name"],
                "properties": { "name": { "type": "string", "minLength": 1, "maxLength": 64 } }
              },
              "output_schema": { "type": "object" },
              "effects": [],
              "risk": "LOW",
              "approval_required": false,
              "reversible": true,
              "idempotent": true
            }
          ]
        }
        """;

    private const string RunTemplate =
        """""
        #!/usr/bin/env python3
        """An Aurora plugin.
        
        Aurora runs this program once per call. The call arrives on standard input as JSON and the result
        goes to standard output as JSON. Anything else the program writes is ignored, and a non-zero exit
        means the call failed.
        
        Almost nothing is inherited: none of Aurora's environment, and on macOS and Linux no network and
        no access to the owner's files. Three variables are passed deliberately — AURORA_PLUGIN_ID,
        AURORA_CAPABILITY, and a PATH holding only the system directories, so that this line can find an
        interpreter at all.
        """
        import json
        import os
        import sys
        
        call = json.loads(sys.stdin.read() or "{}")
        capability = os.environ.get("AURORA_CAPABILITY", "")
        
        if capability.endswith(".greet"):
            print(json.dumps({"greeting": f"Hello, {call['name']}."}))
        else:
            # Exit non-zero for anything you cannot do. Aurora records the exit code and deliberately does
            # not read your standard error, so put nothing there that somebody needs to see.
            sys.exit(1)
        """"";
}
