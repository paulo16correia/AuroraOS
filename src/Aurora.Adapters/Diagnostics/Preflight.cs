using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Aurora.Adapters.Files;
using Aurora.Adapters.Plugins;
using Aurora.Adapters.Plugins.Sandboxes;
using Aurora.Core;
using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Diagnostics;

/// <summary>
/// The checks behind <c>aurora doctor</c> (F-2, docs/adr/0079): turn a deployment that will fail at
/// first plugin call into an explicit report an operator can act on beforehand.
/// </summary>
/// <remarks>
/// Pure of the console: each method takes what it needs and returns results, so the whole report
/// can be exercised in tests without a process, a server, or a real Windows kernel. It reads state
/// and never changes it — a diagnostic that repaired what it found would hide the problem it exists
/// to surface — and it never emits a secret value, only names, paths and outcomes.
/// </remarks>
public static class Preflight
{
    /// <summary>One security-critical key file to check, by the name an operator would recognise.</summary>
    public sealed record KeyFile(string Name, string Path);

    /// <summary>The database file and the directory that must hold it.</summary>
    public static IReadOnlyList<PreflightCheck> Filesystem(string dbPath, string sandboxRoot)
    {
        var results = new List<PreflightCheck>();

        var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (string.IsNullOrEmpty(dbDirectory))
        {
            results.Add(new PreflightCheck("database", PreflightStatus.Fail,
                $"the database path '{dbPath}' has no directory"));
        }
        else if (Writable(dbDirectory, out var why))
        {
            results.Add(new PreflightCheck("database", PreflightStatus.Pass,
                File.Exists(dbPath)
                    ? $"present and its directory is writable ({dbPath})"
                    : $"will be created in a writable directory ({dbPath})"));
        }
        else
        {
            results.Add(new PreflightCheck("database", PreflightStatus.Fail,
                $"the database directory '{dbDirectory}' is not writable: {why}"));
        }

        results.Add(DirectoryOwnerOnly("sandbox root", sandboxRoot));
        return results;
    }

    /// <summary>Every security-critical key file: present and owner-only, or creatable.</summary>
    public static IReadOnlyList<PreflightCheck> KeyMaterial(IEnumerable<KeyFile> keys)
    {
        var results = new List<PreflightCheck>();

        foreach (KeyFile key in keys)
        {
            if (!File.Exists(key.Path))
            {
                // Not a failure: keys are created on first use, and creation is fail-closed (F-3),
                // so an absent key becomes an owner-only one or refuses to start — never an
                // unprotected one.
                var directory = Path.GetDirectoryName(Path.GetFullPath(key.Path));
                var creatable = !string.IsNullOrEmpty(directory) && Writable(directory, out _);

                results.Add(new PreflightCheck($"{key.Name} key", creatable
                        ? PreflightStatus.Info
                        : PreflightStatus.Fail,
                    creatable
                        ? $"absent; will be created owner-only on first start ({key.Path})"
                        : $"absent and its directory is not writable ({key.Path})"));

                continue;
            }

            bool? restricted = OwnerOnly.IsRestricted(key.Path);

            results.Add(new PreflightCheck($"{key.Name} key", restricted switch
                {
                    true => PreflightStatus.Pass,
                    false => PreflightStatus.Fail,
                    null => PreflightStatus.Warn,
                },
                restricted switch
                {
                    true => $"present and owner-only ({key.Path})",
                    false => $"present but NOT owner-only — anyone who can read '{key.Path}' can "
                        + "read this key; move Aurora's data to a per-user location",
                    null => $"present but its protection could not be determined ({key.Path})",
                }));
        }

        return results;
    }

    /// <summary>The confinement this machine will apply to plugins.</summary>
    public static IReadOnlyList<PreflightCheck> Sandbox(IPluginSandbox sandbox, string pluginRoot)
    {
        var results = new List<PreflightCheck>();

        SandboxPlan plan = sandbox.Plan(
            new SandboxRequest("aurora", Path.Combine(pluginRoot, "plugin"), pluginRoot));

        results.Add(plan.Level == SandboxLevel.Confined
            ? new PreflightCheck("plugin confinement", PreflightStatus.Pass,
                $"plugins run confined by {plan.Mechanism}")
            : new PreflightCheck("plugin confinement", PreflightStatus.Warn,
                $"plugins are NOT confined: {plan.Mechanism}. They run only if "
                + "Aurora:Plugins:AllowUnconfined is set, which grants them the owner's own reach"));

        return results;
    }

    /// <summary>
    /// Each installed plugin: manifest validity, interpreter resolution, and — on Windows — whether
    /// the interpreter's directory can be granted to an AppContainer.
    /// </summary>
    public static IReadOnlyList<PreflightCheck> Plugins(
        IReadOnlyList<PluginInstallation> installed,
        PluginInterpreters interpreters,
        Func<string, string, bool> secretPresent)
    {
        var results = new List<PreflightCheck>();

        if (installed.Count == 0)
        {
            results.Add(new PreflightCheck("plugins", PreflightStatus.Info, "none installed"));
            return results;
        }

        foreach (PluginInstallation installation in installed)
        {
            var id = installation.PluginId;

            // The stored manifest is a sealed PluginManifest (absolute executable), read back the
            // way the registry reads it — not a plugin.json parsed by PluginManifestReader.
            PluginManifest? manifest;
            try
            {
                manifest = AuroraJson.Deserialize<PluginManifest>(installation.ManifestJson);
            }
            catch (System.Text.Json.JsonException malformed)
            {
                results.Add(new PreflightCheck($"{id} manifest", PreflightStatus.Fail,
                    $"the stored manifest could not be read: {malformed.Message}"));
                continue;
            }

            if (manifest is null)
            {
                results.Add(new PreflightCheck($"{id} manifest", PreflightStatus.Fail,
                    "the stored manifest is empty"));
                continue;
            }

            results.Add(new PreflightCheck($"{id} manifest", PreflightStatus.Pass,
                $"readable; status {installation.Status}"));

            var executable = manifest.Executable;
            if (string.IsNullOrWhiteSpace(executable))
            {
                results.Add(new PreflightCheck($"{id} program", PreflightStatus.Fail,
                    "the manifest names nothing to run"));
            }
            else if (!File.Exists(executable))
            {
                results.Add(new PreflightCheck($"{id} program", PreflightStatus.Fail,
                    $"the program is not where the manifest says: {executable}"));
            }
            else
            {
                results.Add(InterpreterCheck(id, manifest, executable, interpreters));
            }

            foreach (PreflightCheck secret in Secrets(id, manifest, secretPresent))
            {
                results.Add(secret);
            }
        }

        return results;
    }

    private static PreflightCheck InterpreterCheck(
        string id, PluginManifest manifest, string executable, PluginInterpreters interpreters)
    {
        if (!interpreters.Needed(executable))
        {
            return new PreflightCheck($"{id} interpreter", PreflightStatus.Pass,
                "the platform runs the program directly; no interpreter needed");
        }

        InterpreterResolution resolution = interpreters.Resolve(manifest, executable);

        if (!resolution.Ok || resolution.Interpreter is null)
        {
            return new PreflightCheck($"{id} interpreter", PreflightStatus.Fail, resolution.Refused!);
        }

        var interpreterPath = resolution.Interpreter.Path;
        var interpreterDirectory = Path.GetDirectoryName(interpreterPath);

        if (string.IsNullOrEmpty(interpreterDirectory) || !Directory.Exists(interpreterDirectory))
        {
            return new PreflightCheck($"{id} interpreter", PreflightStatus.Fail,
                $"resolved to '{interpreterPath}', whose directory does not exist");
        }

        // The Windows blocker (F-1): an AppContainer reaches the interpreter only if Aurora can put
        // the container's SID on the directory's ACL. A system-wide install a non-administrator
        // cannot re-permission is refused fail-closed at launch, which reads as a broken plugin
        // unless it is said here first.
        if (OperatingSystem.IsWindows())
        {
            bool? grantable = CanGrantAcl(interpreterDirectory);

            return grantable switch
            {
                true => new PreflightCheck($"{id} interpreter", PreflightStatus.Pass,
                    $"{resolution.Interpreter.Runtime} at '{interpreterPath}', "
                    + "and its directory can be granted to an AppContainer"),
                // Three remedies, and the cheapest one named first. This said only "use a
                // per-user interpreter, or have an administrator grant it" — so an owner who had a
                // per-user interpreter already, beside a system-wide one that PATH found first,
                // read it as needing an administrator or a reinstall. Neither is true: the setting
                // below is authoritative and takes one line.
                false => new PreflightCheck($"{id} interpreter", PreflightStatus.Fail,
                    $"{resolution.Interpreter.Runtime} at '{interpreterPath}', but Aurora cannot "
                    + $"grant its directory '{interpreterDirectory}' to an AppContainer — a "
                    + "system-wide install often cannot be re-permissioned without administrator "
                    + $"rights. Set Aurora:Plugins:Interpreters:{resolution.Interpreter.Runtime} to "
                    + "an interpreter whose directory can be granted (a per-user install lands "
                    + "under %LocalAppData%), or have an administrator grant this one"),
                null => new PreflightCheck($"{id} interpreter", PreflightStatus.Warn,
                    $"{resolution.Interpreter.Runtime} at '{interpreterPath}', but whether its "
                    + "directory can be granted to an AppContainer could not be determined"),
            };
        }

        return new PreflightCheck($"{id} interpreter", PreflightStatus.Pass,
            $"{resolution.Interpreter.Runtime} at '{interpreterPath}'");
    }

    private static IEnumerable<PreflightCheck> Secrets(
        string id, PluginManifest manifest, Func<string, string, bool> secretPresent)
    {
        foreach (PluginSecretRequirement required in manifest.RequiredSecrets ?? [])
        {
            var present = secretPresent(id, required.Name);

            // Presence only, never the value. A required secret that is absent stops the plugin
            // starting, so it is a failure the operator must resolve before a demo.
            yield return new PreflightCheck($"{id} secret '{required.Name}'",
                present ? PreflightStatus.Pass : PreflightStatus.Fail,
                present
                    ? "provisioned in the vault"
                    : $"missing — provision it with:  secret set {id} {required.Name}  "
                        + "(the value is typed on the next line, not passed as an argument)");
        }
    }

    /// <summary>Configuration that is legal but worth an operator's eye.</summary>
    public static IReadOnlyList<PreflightCheck> Configuration(bool allowUnconfinedPlugins)
    {
        var results = new List<PreflightCheck>();

        if (allowUnconfinedPlugins)
        {
            results.Add(new PreflightCheck("configuration", PreflightStatus.Warn,
                "Aurora:Plugins:AllowUnconfined is set: plugins run with the owner's own reach, "
                + "not confined. Appropriate only for plugins you wrote yourself"));
        }
        else
        {
            results.Add(new PreflightCheck("configuration", PreflightStatus.Pass,
                "unconfined plugin execution is off (the default)"));
        }

        return results;
    }

    private static PreflightCheck DirectoryOwnerOnly(string component, string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception cannot)
            when (cannot is IOException or UnauthorizedAccessException)
        {
            return new PreflightCheck(component, PreflightStatus.Fail,
                $"'{path}' does not exist and could not be created: {cannot.GetType().Name}");
        }

        bool? restricted = OwnerOnly.IsRestricted(path);

        return restricted switch
        {
            true => new PreflightCheck(component, PreflightStatus.Pass, $"present and owner-only ({path})"),
            false => new PreflightCheck(component, PreflightStatus.Warn,
                $"present but not owner-only ({path}); Aurora restricts it at startup, and the "
                + "path defences still apply, but a per-user location is preferable"),
            null => new PreflightCheck(component, PreflightStatus.Warn,
                $"present but its protection could not be determined ({path})"),
        };
    }

    private static bool Writable(string directory, out string reason)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".aurora-preflight-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            reason = string.Empty;
            return true;
        }
        catch (Exception cannot)
            when (cannot is IOException or UnauthorizedAccessException)
        {
            reason = cannot.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Whether the current account can change a directory's ACL — the right an AppContainer grant
    /// needs. Read-only: it inspects ownership and rules rather than writing anything.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool? CanGrantAcl(string directory)
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);

            // An administrator can always re-permission.
            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                return true;
            }

            DirectorySecurity security = new DirectoryInfo(directory).GetAccessControl();
            IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));

            // The owner of an object can always change its DACL.
            if (owner is not null && identity.User is not null && owner.Equals(identity.User))
            {
                return true;
            }

            // Otherwise, an explicit ACE granting the right to change permissions.
            var mine = new List<IdentityReference> { identity.User! };
            if (identity.Groups is not null)
            {
                mine.AddRange(identity.Groups);
            }

            foreach (AuthorizationRule rule in
                security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule is FileSystemAccessRule { AccessControlType: AccessControlType.Allow } allow
                    && mine.Contains(allow.IdentityReference)
                    && (allow.FileSystemRights & FileSystemRights.ChangePermissions) != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception unreadable)
            when (unreadable is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
