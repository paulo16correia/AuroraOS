using Aurora.Server;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Defaults an operator inherits without choosing them (docs/adr/0037).
/// </summary>
public sealed class ServerOptionsTests
{
    private static AuroraServerOptions From(params (string Key, string Value)[] settings)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings
                    .Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value))
                    .Append(new KeyValuePair<string, string?>(
                        "Aurora:BearerToken", "a-token-long-enough-to-pass-validation"))
                    .ToList())
            .Build();

        return AuroraServerOptions.FromConfiguration(config);
    }

    [Fact]
    public void TheSandboxCapabilitiesAreOfferedByDefault()
    {
        // Unfrozen by the owner's decision. Being in the catalog is not permission to use them:
        // both are MEDIUM and approval-gated, and that gate applies on every call.
        Assert.True(From().SandboxFilesEnabled);
    }

    [Fact]
    public void AnInstanceThatShouldNotTouchFilesCanStillSaySo()
    {
        // Turning them off stays a legitimate thing to want. The switch is not what makes them
        // safe — it is what makes them absent.
        Assert.False(From(("Aurora:SandboxFilesEnabled", "false")).SandboxFilesEnabled);
    }
}
