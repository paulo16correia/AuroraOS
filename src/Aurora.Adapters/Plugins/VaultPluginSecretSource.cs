using Aurora.Core.Abstractions;
using Aurora.Core.Contracts;

namespace Aurora.Adapters.Plugins;

/// <summary>
/// Supplies a plugin's declared secrets from Aurora's vault.
/// </summary>
/// <remarks>
/// A plugin declares secrets by name; the vault keys them by an id it generated. The bridge between
/// the two is the purpose, which is <c>plugin/{plugin_id}/{name}</c> and unique — so the owner
/// stores a value once, under a name the plugin's documentation told them, and Aurora finds it.
/// <para>
/// This is the one place in Aurora where a secret's value is copied out of a lease. It has to be:
/// the value is about to cross a process boundary, and no lease can follow it there. Everything
/// else about the handling stays — it is read inside the callback, written straight to the child's
/// stdin, and never logged, audited, or returned to anything that could keep it.
/// </para>
/// </remarks>
public sealed class VaultPluginSecretSource : IPluginSecretSource
{
    private readonly IVault _vault;

    public VaultPluginSecretSource(IVault vault) => _vault = vault;

    /// <summary>The purpose a plugin's secret is filed under.</summary>
    public static string PurposeOf(string pluginId, string name) => $"plugin/{pluginId}/{name}";

    public async Task<string?> FindAsync(string pluginId, string name, CancellationToken ct)
    {
        SecretReference? reference = await _vault
            .FindByPurposeAsync(PurposeOf(pluginId, name), ct)
            .ConfigureAwait(false);

        if (reference is null)
        {
            return null;
        }

        try
        {
            // The plugin id is the tool id, so a secret filed for one plugin cannot be leased by
            // another even if it somehow asked for the right purpose.
            using EphemeralSecretHandle handle = await _vault
                .LeaseAsync(reference.Id, new ToolCallRef(Guid.NewGuid().ToString("N"), pluginId), ct)
                .ConfigureAwait(false);

            return handle.Use(secret => new string(secret));
        }
        catch (VaultException)
        {
            // Revoked, expired, or not allowed for this plugin. Indistinguishable from absent on
            // purpose: the caller's only decision is whether the service can start, and the reason
            // belongs in the vault's own audit rather than in a plugin's error message.
            return null;
        }
    }
}
