using System.Text.Json;

namespace Aurora.Core.Contracts;

/// <summary>What reading a <c>plugin.json</c> produced, or why it did not.</summary>
public sealed record PluginManifestRead(
    PluginManifest? Manifest,
    IReadOnlyList<string> Problems)
{
    public bool Ok => Manifest is not null;
}

/// <summary>
/// Turns a <c>plugin.json</c> into a <see cref="PluginManifest"/>, or into a list of problems
/// somebody can fix.
/// </summary>
/// <remarks>
/// The error messages are the feature. A plugin author's first encounter with Aurora is this file
/// being wrong, and "invalid manifest" teaches them nothing — so every check names the field, says
/// what was expected, and where more than one thing is wrong it reports all of them rather than
/// stopping at the first. Somebody fixing five mistakes should need one round trip, not five.
/// </remarks>
public static class PluginManifestReader
{
    /// <summary>Ids that are Aurora's own and cannot be claimed by a plugin.</summary>
    private static readonly string[] ReservedPrefixes = ["aurora.", "kernel.", "mind."];

    public static PluginManifestRead Read(string json, IReadOnlyList<string> existingActionIds)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException malformed)
        {
            // The parser's message names the line and column, which is more use than anything
            // this method could say instead.
            return new PluginManifestRead(null, [$"not valid JSON: {malformed.Message}"]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return new PluginManifestRead(null, ["plugin.json must be a JSON object"]);
            }

            var problems = new List<string>();

            // Unknown fields are collected rather than thrown on, so a typo does not hide the
            // five other things that are also wrong. Somebody fixing this file should need one
            // round trip, not six.
            Unknown(document.RootElement, KnownTopLevel, "", problems);

            if (document.RootElement.TryGetProperty("capabilities", out JsonElement declared)
                && declared.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement capability in declared.EnumerateArray())
                {
                    var where = capability.TryGetProperty("key", out JsonElement key)
                        && key.ValueKind == JsonValueKind.String
                            ? $"capability '{key.GetString()}': "
                            : "a capability: ";

                    Unknown(capability, KnownCapability, where, problems);
                }
            }

            PluginManifestFile? file;

            try
            {
                file = JsonSerializer.Deserialize<PluginManifestFile>(json, Options);
            }
            catch (JsonException mistyped)
            {
                // A field of the wrong type: a string where a list belongs, and so on. The
                // parser names it, and there is no useful way to carry on past it.
                return new PluginManifestRead(null, [.. problems, mistyped.Message]);
            }

            if (file is null)
            {
                return new PluginManifestRead(null, ["plugin.json is empty"]);
            }

            return Check(file, existingActionIds, problems);
        }
    }

    /// <summary>Names in an object that Aurora does not know, reported one by one.</summary>
    private static void Unknown(
        JsonElement element, IReadOnlyCollection<string> known, string where, List<string> problems)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                var nearest = known
                    .Where(k => k.StartsWith(property.Name[..1], StringComparison.Ordinal))
                    .Take(3)
                    .ToList();

                problems.Add(
                    $"{where}'{property.Name}' is not a field Aurora knows"
                    + (nearest.Count > 0 ? $" — did you mean {string.Join(", ", nearest)}?" : string.Empty));
            }
        }
    }

    private static readonly HashSet<string> KnownTopLevel = new(StringComparer.Ordinal)
    {
        "plugin_id", "version", "publisher", "executable", "min_platform_version",
        "max_data_class", "documentation_ref", "required_permissions", "event_subscriptions",
        "network_endpoints", "capabilities", "service", "required_secrets",
    };

    private static readonly HashSet<string> KnownCapability = new(StringComparer.Ordinal)
    {
        "key", "title", "description", "input_schema", "output_schema", "effects", "risk",
        "approval_required", "reversible", "rate_limit_per_minute", "timeout_seconds",
        "idempotent", "audit_level",
    };

    private static PluginManifestRead Check(
        PluginManifestFile file, IReadOnlyList<string> existingActionIds, List<string> problems)
    {
        Require(problems, file.PluginId, "plugin_id", "an identifier such as \"acme/notes\"");
        Require(problems, file.Version, "version", "a version such as \"1.0.0\"");
        Require(problems, file.Publisher, "publisher", "who wrote this");
        Require(problems, file.Executable, "executable", "the program to run, relative to this file");

        if (Path.IsPathRooted(file.Executable) || file.Executable.Contains("..", StringComparison.Ordinal))
        {
            problems.Add(
                "executable must be relative to the plugin folder and must not contain '..'; "
                + "a manifest naming an absolute path is describing the machine, not the plugin");
        }

        if (!Sensitivity.IsKnown(file.MaxDataClass))
        {
            problems.Add(
                $"max_data_class '{file.MaxDataClass}' is not one of "
                + $"{Sensitivity.Public}, {Sensitivity.Private}, {Sensitivity.Confidential} "
                + $"or {Sensitivity.Secret}");
        }

        foreach (PluginSecretFile secret in file.RequiredSecrets)
        {
            if (string.IsNullOrWhiteSpace(secret.Name))
            {
                problems.Add("every entry in required_secrets needs a name.");
            }

            if (string.IsNullOrWhiteSpace(secret.Purpose))
            {
                problems.Add(
                    $"required_secrets['{secret.Name}'] needs a purpose: it is what a person reads "
                    + "before deciding to hand you a credential.");
            }
        }

        foreach (var endpoint in file.NetworkEndpoints)
        {
            // Named hosts only. The owner is being asked to agree to something, and "*.acme.com"
            // is not something anybody can weigh (docs/adr/0067).
            if (string.IsNullOrWhiteSpace(endpoint)
                || endpoint.Contains('*', StringComparison.Ordinal)
                || endpoint.Contains('/', StringComparison.Ordinal)
                || endpoint.Contains(':', StringComparison.Ordinal))
            {
                problems.Add(
                    $"network_endpoints['{endpoint}'] must be a plain host name like "
                    + "'discord.com'. No wildcards, schemes, ports or paths: the owner is agreeing "
                    + "to each host by name.");
            }
        }

        if (file.Capabilities.Count == 0)
        {
            problems.Add("capabilities is empty; a plugin that offers nothing cannot be installed");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var capabilities = new List<PluginCapability>();

        foreach (PluginCapabilityFile capability in file.Capabilities)
        {
            capabilities.Add(ReadCapability(capability, existingActionIds, seen, problems));
        }

        if (problems.Count > 0)
        {
            return new PluginManifestRead(null, problems);
        }

        return new PluginManifestRead(
            new PluginManifest(
                file.PluginId, file.Version, file.Publisher,
                Signature: string.Empty, file.MinPlatformVersion,
                capabilities, file.EventSubscriptions, file.RequiredPermissions,
                file.MaxDataClass, file.NetworkEndpoints, file.DocumentationRef,
                IntegrityHash: string.Empty, file.Executable,
                Service: file.Service is null
                    ? null
                    : new PluginService(
                        file.Executable,
                        TimeSpan.FromSeconds(Math.Clamp(file.Service.StartTimeoutSeconds, 1, 300)),
                        Math.Clamp(file.Service.MaxConsecutiveFailures, 1, 100)),
                RequiredSecrets:
                [
                    .. file.RequiredSecrets.Select(
                        secret => new PluginSecretRequirement(secret.Name, secret.Purpose)),
                ]),
            []);
    }

    private static PluginCapability ReadCapability(
        PluginCapabilityFile capability, IReadOnlyList<string> existingActionIds,
        HashSet<string> seen, List<string> problems)
    {
        var where = string.IsNullOrWhiteSpace(capability.Key)
            ? "a capability"
            : $"capability '{capability.Key}'";

        if (string.IsNullOrWhiteSpace(capability.Key))
        {
            problems.Add("key is missing; expected a dotted action id such as \"notes.append\"");
        }

        if (string.IsNullOrWhiteSpace(capability.Title))
        {
            problems.Add($"{where}: title is missing; expected a short name a person will read");
        }

        if (!string.IsNullOrWhiteSpace(capability.Key))
        {
            if (!capability.Key.Contains('.', StringComparison.Ordinal))
            {
                problems.Add(
                    $"{where}: key must be dotted, like \"notes.append\", so it reads as an action");
            }

            if (existingActionIds.Contains(capability.Key, StringComparer.Ordinal))
            {
                // A plugin claiming files.write_sandbox would otherwise shadow the real one.
                problems.Add($"{where}: Aurora already has an action with this id");
            }

            foreach (var reserved in ReservedPrefixes.Where(
                p => capability.Key.StartsWith(p, StringComparison.Ordinal)))
            {
                problems.Add($"{where}: '{reserved}' is reserved for Aurora itself");
            }

            if (!seen.Add(capability.Key))
            {
                problems.Add($"{where}: declared twice in this manifest");
            }
        }

        if (!Enum.TryParse(capability.Risk, ignoreCase: true, out RiskLevel risk))
        {
            problems.Add(
                $"{where}: risk '{capability.Risk}' is not one of LOW, MEDIUM, HIGH, CRITICAL");
        }

        if (capability.InputSchema.ValueKind is not JsonValueKind.Object)
        {
            problems.Add(
                $"{where}: input_schema must be a JSON Schema object; Aurora validates every call "
                + "against it before your program runs");
        }

        if (capability.TimeoutSeconds is < 1 or > 300)
        {
            problems.Add($"{where}: timeout_seconds must be between 1 and 300");
        }

        if (capability.RateLimitPerMinute is < 1 or > 600)
        {
            problems.Add($"{where}: rate_limit_per_minute must be between 1 and 600");
        }

        if (risk != RiskLevel.Low && !capability.ApprovalRequired)
        {
            // Not a matter of taste: policy denies anything above LOW that did not ask for
            // approval, so this manifest would install and then never be allowed to run.
            problems.Add(
                $"{where}: anything above LOW must set approval_required, or policy will refuse "
                + "every call to it");
        }

        if (risk == RiskLevel.High && !capability.Reversible)
        {
            problems.Add(
                $"{where}: HIGH is permitted only when it is also reversible — say how a call can "
                + "be undone, or declare it MEDIUM");
        }

        return new PluginCapability(
            capability.Key,
            Text(capability.InputSchema),
            Text(capability.OutputSchema),
            capability.Effects,
            capability.ApprovalRequired,
            capability.RateLimitPerMinute,
            TimeSpan.FromSeconds(Math.Clamp(capability.TimeoutSeconds, 1, 300)),
            capability.Idempotent,
            capability.AuditLevel,
            capability.Title,
            capability.Description,
            risk,
            capability.Reversible);
    }

    private static void Require(List<string> problems, string value, string field, string expected)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{field} is missing; expected {expected}");
        }
    }

    private static string Text(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : element.GetRawText();

    private static readonly JsonSerializerOptions Options = new()
    {
        // A typo in a permission name should be an error at install time, not a permission
        // silently not requested.
        // Lenient here: unknown fields are found by the pass above, which reports all of them
        // rather than stopping at the first.
        PropertyNameCaseInsensitive = false,
    };
}
