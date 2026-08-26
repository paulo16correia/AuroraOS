using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aurora.Core.Contracts;

/// <summary>
/// A plugin's <c>plugin.json</c>, as its author writes it.
/// </summary>
/// <remarks>
/// Separate from <see cref="PluginManifest"/> on purpose. The manifest carries a signature and an
/// integrity hash; neither is something an author writes, because in Aurora the trust anchor is
/// the owner rather than a marketplace. The author declares; the owner approves; Aurora seals what
/// was approved and refuses it later if it changed.
/// <para>
/// Every field is snake_case to match the rest of Aurora's wire format, and unknown fields are
/// refused rather than ignored — a typo in a permission name should be an error at install time,
/// not a permission silently not requested.
/// </para>
/// </remarks>
public sealed record PluginManifestFile
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    /// <summary>
    /// The program Aurora runs, relative to the folder holding this file.
    /// </summary>
    /// <remarks>
    /// Relative, so a plugin folder can be moved or copied without editing it. Absolute paths are
    /// refused: a manifest that names <c>/usr/bin/something</c> is describing the machine rather
    /// than the plugin.
    /// </remarks>
    [JsonPropertyName("executable")]
    public string Executable { get; init; } = string.Empty;

    [JsonPropertyName("min_platform_version")]
    public int MinPlatformVersion { get; init; } = 1;

    [JsonPropertyName("max_data_class")]
    public string MaxDataClass { get; init; } = Sensitivity.Private;

    [JsonPropertyName("documentation_ref")]
    public string DocumentationRef { get; init; } = string.Empty;

    [JsonPropertyName("required_permissions")]
    public IReadOnlyList<string> RequiredPermissions { get; init; } = [];

    [JsonPropertyName("event_subscriptions")]
    public IReadOnlyList<string> EventSubscriptions { get; init; } = [];

    [JsonPropertyName("network_endpoints")]
    public IReadOnlyList<string> NetworkEndpoints { get; init; } = [];

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<PluginCapabilityFile> Capabilities { get; init; } = [];

    /// <summary>Set when the plugin holds a connection rather than answering one call.</summary>
    [JsonPropertyName("service")]
    public PluginServiceFile? Service { get; init; }

    /// <summary>Secrets the plugin cannot run without, by name. Values never appear here.</summary>
    [JsonPropertyName("required_secrets")]
    public IReadOnlyList<PluginSecretFile> RequiredSecrets { get; init; } = [];
}

/// <summary>A long-lived plugin process, as its author declares it (docs/adr/0067).</summary>
public sealed record PluginServiceFile
{
    /// <summary>How long Aurora waits for the ready frame before giving up on the start.</summary>
    [JsonPropertyName("start_timeout_seconds")]
    public int StartTimeoutSeconds { get; init; } = 30;

    /// <summary>Failed starts in a row before the plugin is held rather than started again.</summary>
    [JsonPropertyName("max_consecutive_failures")]
    public int MaxConsecutiveFailures { get; init; } = 5;
}

/// <summary>
/// One secret the plugin needs, declared by name.
/// </summary>
/// <remarks>
/// The name only. A manifest is read by a person deciding whether to install, and is stored beside
/// the installation — putting a value here would write a credential into Aurora's database in
/// plain text and into whatever the author committed it to first.
/// </remarks>
public sealed record PluginSecretFile
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>What a person reads before deciding to supply it.</summary>
    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = string.Empty;
}

/// <summary>One capability, as its author writes it.</summary>
public sealed record PluginCapabilityFile
{
    /// <summary>
    /// The action id this becomes in Aurora's catalogue.
    /// </summary>
    /// <remarks>
    /// Dotted, like every other action id, and refused if it collides with one Aurora already has:
    /// a plugin claiming <c>files.write_sandbox</c> would otherwise shadow the real one.
    /// </remarks>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>JSON Schema for the input. Aurora validates against it before the plugin runs.</summary>
    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; init; }

    [JsonPropertyName("output_schema")]
    public JsonElement OutputSchema { get; init; }

    [JsonPropertyName("effects")]
    public IReadOnlyList<string> Effects { get; init; } = [];

    /// <summary>One of LOW, MEDIUM, HIGH, CRITICAL. Defaults to HIGH when unstated.</summary>
    [JsonPropertyName("risk")]
    public string Risk { get; init; } = nameof(RiskLevel.High);

    [JsonPropertyName("approval_required")]
    public bool ApprovalRequired { get; init; } = true;

    [JsonPropertyName("reversible")]
    public bool Reversible { get; init; }

    [JsonPropertyName("rate_limit_per_minute")]
    public int RateLimitPerMinute { get; init; } = 30;

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; init; } = 30;

    [JsonPropertyName("idempotent")]
    public bool Idempotent { get; init; }

    [JsonPropertyName("audit_level")]
    public string AuditLevel { get; init; } = "FULL";
}
