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

    [Fact]
    public void TheSandboxRootIsOwnerOnlyFromTheMomentItIsCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"aurora-opt-{Guid.NewGuid():N}");

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

    [Fact]
    public void BindingBeyondLoopbackWithoutNamingTheHostIsRefused()
    {
        // The binding and the Host guard are one control. Reachable from the network while still
        // judging every request against a guard that only knows loopback is the combination that
        // looks like it works and does not.
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => From(("Aurora:BindAddress", "0.0.0.0")));

        Assert.Contains("Aurora:AllowedHosts", refused.Message, StringComparison.Ordinal);

        AuroraServerOptions options = From(
            ("Aurora:BindAddress", "0.0.0.0"), ("Aurora:AllowedHosts", "aurora.example.com"));

        Assert.Equal(["aurora.example.com"], options.AllowedHosts);
    }
}
