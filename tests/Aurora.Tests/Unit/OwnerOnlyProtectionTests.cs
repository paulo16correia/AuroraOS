using Aurora.Adapters.Files;
using Aurora.Adapters.Persistence;
using Aurora.Tests.Support;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Owner-only key protection is fail-closed (F-3, docs/adr/0079).
/// </summary>
/// <remarks>
/// The invariant: Aurora never keeps security-critical key material behind a permission it could
/// not narrow to the owner. Where the protection holds, key handling proceeds; where it cannot be
/// established, the operation refuses rather than continuing while claiming a protection it does
/// not have.
/// </remarks>
public sealed class OwnerOnlyProtectionTests
{
    // ---- 1. the success path proceeds normally ----

    [Fact]
    public void AKeyIsCreatedOwnerOnlyAndIsStableAcrossReloads()
    {
        var path = Path.Combine(TestTemp.Folder("keys"), "aurora.audit.key");

        var first = LocalKeyFile.LoadOrCreate(path, "Audit");
        var second = LocalKeyFile.LoadOrCreate(path, "Audit");

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);

        // The file the key lives in really is owner-only, not merely written by code that intends it.
        Assert.True(OwnerOnly.IsRestricted(path));
    }

    // ---- 2. protection failure fails closed ----

    [Fact]
    public void RequireThrowsWhenProtectionCannotBeEstablished()
    {
        // A path that does not exist cannot be restricted, on any platform. This is the same gate
        // the key-load path runs through, so proving it refuses here proves the reload path refuses
        // rather than handing back a key from a file it cannot protect. There is no permissive
        // fallback: the answer is an exception, not a shrug.
        var missing = Path.Combine(TestTemp.Folder("keys"), "not-there.key");

        OwnerOnlyProtectionException thrown =
            Assert.Throws<OwnerOnlyProtectionException>(() => OwnerOnly.Require(missing));

        Assert.Equal(missing, thrown.Path);
    }

    // ---- 3. no key is treated as safe after protection is broadened ----

    [Fact]
    public void ABroadenedKeyIsReRestrictedOnReloadNotReadSilently()
    {
        var path = Path.Combine(TestTemp.Folder("keys"), "aurora.vault.key");
        LocalKeyFile.LoadOrCreate(path, "Vault");

        // Broaden the protection behind Aurora's back.
        Broaden(path);
        Assert.False(OwnerOnly.IsRestricted(path), "the file should now be readable by others");

        // Reload. The load path runs the owner-only gate, which re-establishes the restriction —
        // so the key is not handed back from a file that was left broadened. If it could not be
        // re-established, this would throw rather than return, which is the fail-closed half.
        var reloaded = LocalKeyFile.LoadOrCreate(path, "Vault");

        Assert.Equal(32, reloaded.Length);
        Assert.True(
            OwnerOnly.IsRestricted(path),
            "reloading a key must leave it owner-only, never read it back from a broadened file");
    }

    [Fact]
    public void IsRestrictedDetectsABroadenedFileRatherThanRubberStamping()
    {
        var path = Path.Combine(TestTemp.Folder("keys"), "probe.key");
        File.WriteAllBytes(path, new byte[32]);
        OwnerOnly.File(path);
        Assert.True(OwnerOnly.IsRestricted(path));

        Broaden(path);

        Assert.False(OwnerOnly.IsRestricted(path));
    }

    /// <summary>Opens a file's protection so a non-owner could read it, however the platform does that.</summary>
    private static void Broaden(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            System.Security.AccessControl.FileSecurity acl = info.GetAccessControl();

            // Stop protecting (re-enable inheritance) and add a broad allow rule, so the file is no
            // longer the owner's alone.
            acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}
