using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Aurora.Adapters.Files;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// That "owner-only" means the same thing on every platform Aurora runs on.
/// </summary>
/// <remarks>
/// Written to assert the property rather than the mechanism, so the same test is meaningful on
/// Windows and on Unix: a file Aurora created for a key is reachable by the account that owns
/// Aurora and by no other. Before this, the Windows branch of that sentence was a comment saying
/// the directory probably carried the right ACL.
/// </remarks>
public sealed class OwnerOnlyTests
{
    [Fact]
    public void AWrittenFileIsRestrictedToItsOwner()
    {
        var path = TestTemp.Path("key");

        OwnerOnly.Write(path, FileMode.CreateNew, stream => stream.Write("secret"u8));

        Assert.Equal("secret", File.ReadAllText(path));
        AssertOnlyTheOwnerReaches(path);
    }

    [Fact]
    public void RewritingAFileKeepsTheRestriction()
    {
        var path = TestTemp.Path("verifier");

        OwnerOnly.Write(path, FileMode.CreateNew, stream => stream.Write("first"u8));
        OwnerOnly.Write(path, FileMode.Create, stream => stream.Write("second"u8));

        // Truncated rather than appended to — the passphrase verifier rewrites this file on every
        // change, and a rewrite that left the old bytes past the new ones would leave a superseded
        // verifier readable on disk.
        Assert.Equal("second", File.ReadAllText(path));
        AssertOnlyTheOwnerReaches(path);
    }

    [Fact]
    public void CreateNewRefusesToOverwriteAnExistingKey()
    {
        var path = TestTemp.Path("key");
        OwnerOnly.Write(path, FileMode.CreateNew, stream => stream.Write("original"u8));

        // The audit key and the genome key are created exactly once. Silently replacing one would
        // make every record it signed unverifiable, which is the thing the key existed to prevent.
        Assert.Throws<IOException>(
            () => OwnerOnly.Write(path, FileMode.CreateNew, stream => stream.Write("second"u8)));

        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void ADirectoryIsRestrictedToItsOwner()
    {
        var path = TestTemp.Path("sandbox");
        Directory.CreateDirectory(path);

        Assert.True(OwnerOnly.Directory(path));
        AssertOnlyTheOwnerReaches(path);
    }

    [Fact]
    public void RestrictingSomethingThatIsNotThereIsReportedRatherThanThrown()
    {
        // The sandbox root is restricted at startup, before anything has read it. A missing path
        // there is a configuration problem, and Aurora keeps starting and says so — it does not
        // refuse to run over a permission it could not set.
        Assert.False(OwnerOnly.File(TestTemp.Path("absent")));
        Assert.False(OwnerOnly.Directory(TestTemp.Path("absent-directory")));
    }

    /// <summary>
    /// Asserts the property both platforms have to deliver, by the means each one has.
    /// </summary>
    private static void AssertOnlyTheOwnerReaches(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertWindowsAclNamesOnlyTheOwner(path);
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(path);

        Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite));
        Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
        Assert.NotEqual(UnixFileMode.None, mode & UnixFileMode.UserRead);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsAclNamesOnlyTheOwner(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();

        // Nothing inherited: the whole point is not to depend on where the file was put.
        Assert.True(security.AreAccessRulesProtected);

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        AuthorizationRuleCollection rules =
            security.GetAccessRules(true, false, typeof(SecurityIdentifier));

        Assert.NotEmpty(rules);

        foreach (AuthorizationRule rule in rules)
        {
            Assert.Equal(identity.User, rule.IdentityReference);
        }
    }
}
