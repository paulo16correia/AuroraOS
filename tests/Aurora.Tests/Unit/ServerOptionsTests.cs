using Aurora.Server;
using Microsoft.Extensions.Configuration;
using Xunit;
using Aurora.Tests.Support;

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

    [Fact]
    public void TheSandboxRootIsOwnerOnlyFromTheMomentItIsCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TestTemp.Path("opt");

        try
        {
            From(("Aurora:SandboxRoot", root));

            // Not at first use. The writer restricts the root when constructed, but it is
            // constructed lazily — so an instance that never touches a file would leave the
            // sandbox world-readable, which is the precondition the path hardening rests on.
            UnixFileMode mode = File.GetUnixFileMode(root);

            Assert.Equal(UnixFileMode.None, mode & UnixFileMode.OtherRead);
            Assert.Equal(UnixFileMode.None, mode & UnixFileMode.GroupWrite);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

}
