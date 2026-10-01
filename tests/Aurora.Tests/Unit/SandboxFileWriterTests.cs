using Aurora.Adapters.Files;
using Aurora.Core.Abstractions;
using Xunit;
using Aurora.Tests.Support;

namespace Aurora.Tests.Unit;

public sealed class SandboxFileWriterTests
{
    /// <summary>A throwaway sandbox root, plus an outside directory to attempt escapes into.</summary>
    private sealed class TempSandbox : IDisposable
    {
        public string Root { get; } = TestTemp.Path("sbx");

        public string Outside { get; } = TestTemp.Path("out");

        public TempSandbox()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Outside);
        }

        public void Dispose()
        {
            foreach (var dir in new[] { Root, Outside })
            {
                try
                {
                    // Through the helper, because these tests deliberately put a link inside the
                    // sandbox and a plain recursive delete refuses a tree containing one.
                    TestLinks.DeleteTree(dir);
                }
                catch (Exception leftBehind)
                    when (leftBehind is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task Write_CreatesFileWithContent()
    {
        using var sandbox = new TempSandbox();
        var writer = new SandboxFileWriter(sandbox.Root);

        var result = await writer.WriteAsync("notes.txt", "hello", CancellationToken.None);

        Assert.False(result.Overwritten);
        Assert.Equal(5, result.Bytes);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(sandbox.Root, "notes.txt")));
    }

    [Fact]
    public async Task Write_CreatesNestedDirectories()
    {
        using var sandbox = new TempSandbox();
        var writer = new SandboxFileWriter(sandbox.Root);

        await writer.WriteAsync("a/b/c.txt", "deep", CancellationToken.None);

        Assert.Equal("deep", await File.ReadAllTextAsync(Path.Combine(sandbox.Root, "a", "b", "c.txt")));
    }

    [Fact]
    public async Task Write_OverwritesExistingFileAndReportsIt()
    {
        using var sandbox = new TempSandbox();
        var writer = new SandboxFileWriter(sandbox.Root);

        await writer.WriteAsync("notes.txt", "first", CancellationToken.None);
        var second = await writer.WriteAsync("notes.txt", "second", CancellationToken.None);

        Assert.True(second.Overwritten);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(sandbox.Root, "notes.txt")));
    }

    [Fact]
    public async Task Write_LeavesNoTempFilesBehind()
    {
        using var sandbox = new TempSandbox();
        var writer = new SandboxFileWriter(sandbox.Root);

        await writer.WriteAsync("notes.txt", "hello", CancellationToken.None);

        Assert.Empty(Directory.GetFiles(sandbox.Root, "*.tmp"));
    }

    [Fact]
    public async Task Write_RejectsTraversal()
    {
        using var sandbox = new TempSandbox();
        var writer = new SandboxFileWriter(sandbox.Root);

        await Assert.ThrowsAsync<SandboxViolationException>(
            () => writer.WriteAsync("../escaped.txt", "nope", CancellationToken.None));

        Assert.Empty(Directory.GetFiles(sandbox.Outside));
    }

    [Fact]
    public async Task Write_RefusesToFollowSymlinkedDirectoryOutOfSandbox()
    {
        using var sandbox = new TempSandbox();
        var link = Path.Combine(sandbox.Root, "escape");

        // A symlink where the platform allows one, and a junction on a Windows that does not —
        // which is the link a plugin could actually make there, since a junction needs no
        // privilege and a symlink needs one the attacker would not have either.
        TestLinks.Directory(link, sandbox.Outside);

        var writer = new SandboxFileWriter(sandbox.Root);

        await Assert.ThrowsAsync<SandboxViolationException>(
            () => writer.WriteAsync("escape/pwned.txt", "nope", CancellationToken.None));

        Assert.Empty(Directory.GetFiles(sandbox.Outside));
    }

    [Fact]
    public async Task Write_RefusesToWriteThroughSymlinkedFile()
    {
        using var sandbox = new TempSandbox();
        var target = Path.Combine(sandbox.Outside, "target.txt");
        await File.WriteAllTextAsync(target, "original");

        var writer = new SandboxFileWriter(sandbox.Root);

        if (TestLinks.TryFile(Path.Combine(sandbox.Root, "innocent.txt"), target))
        {
            await Assert.ThrowsAsync<SandboxViolationException>(
                () => writer.WriteAsync("innocent.txt", "pwned", CancellationToken.None));

            Assert.Equal("original", await File.ReadAllTextAsync(target));
            return;
        }

        // Windows will not let an unprivileged process make a link to a *file* at all: junctions
        // redirect directories only, and Developer Mode is off. So the same escape is built the
        // way it could be built there — the file reached through a linked directory — and the same
        // refusal is asserted. Nothing is left unchecked on this platform.
        Assert.False(TestLinks.FileLinksAvailable);

        TestLinks.Directory(Path.Combine(sandbox.Root, "escape"), sandbox.Outside);

        await Assert.ThrowsAsync<SandboxViolationException>(
            () => writer.WriteAsync("escape/target.txt", "pwned", CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Write_ResolvesSandboxRootThroughItsOwnSymlink()
    {
        using var sandbox = new TempSandbox();
        var linkedRoot = TestTemp.Path("link");
        TestLinks.Directory(linkedRoot, sandbox.Root);

        try
        {
            // A symlinked root is the operator's own choice and must keep working; only links
            // *inside* the sandbox are treated as an escape attempt.
            var writer = new SandboxFileWriter(linkedRoot);
            await writer.WriteAsync("notes.txt", "ok", CancellationToken.None);

            Assert.Equal("ok", await File.ReadAllTextAsync(Path.Combine(sandbox.Root, "notes.txt")));
        }
        finally
        {
            Directory.Delete(linkedRoot);
        }
    }

    // ---- docs/adr/0078: a hard link at the destination is a name, not a hole ----

    [Fact]
    public async Task Write_ThroughAHardLinkReplacesTheNameAndSpareTheLinkedFile()
    {
        // A hard link is not a reparse point, so the link-component check does not catch one at
        // the destination — and it does not need to. The writer lands content via a temp file and
        // a replacing rename, which swaps the directory entry rather than writing through it. So a
        // hard link planted at the destination, pointing at a file outside the sandbox, has its
        // own name replaced while the outside file keeps its content.
        //
        // Why this is a defence and not the whole story: planting the link at all needs write
        // access to the outside file (CreateHardLink's requirement), which the only actors who
        // could do it — Aurora's own owner — already have. The property proven here is that even
        // then, a WRITE cannot be turned into corruption of the linked target.
        using var sandbox = new TempSandbox();
        var outsideFile = Path.Combine(sandbox.Outside, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "original-secret");

        TestLinks.HardLink(Path.Combine(sandbox.Root, "notes.txt"), outsideFile);

        var writer = new SandboxFileWriter(sandbox.Root);
        await writer.WriteAsync("notes.txt", "replaced", CancellationToken.None);

        // The linked-to file outside the sandbox is untouched: the rename replaced the name.
        Assert.Equal("original-secret", await File.ReadAllTextAsync(outsideFile));

        // And the sandbox now holds a real file with the new content, not a link.
        Assert.Equal("replaced", await File.ReadAllTextAsync(Path.Combine(sandbox.Root, "notes.txt")));
    }

    // ---- docs/adr/0036: the residual TOCTOU risk, narrowed and made detectable ----

    [Fact]
    public async Task AWriteThatLandedOutsideTheSandboxIsRemovedAndReported()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating a directory symlink on Windows needs elevation or developer mode; the
            // detection path is identical, so it is exercised on the platforms that allow the setup.
            return;
        }

        var root = NewRoot();
        var outside = TestTemp.Path("outside");
        Directory.CreateDirectory(outside);

        try
        {
            var writer = new SandboxFileWriter(root);

            // Aurora creates the directory itself; then it is swapped for a link to somewhere else,
            // which is exactly the shape of the race that cannot be prevented portably.
            await writer.WriteAsync("nested/first.txt", "one", CancellationToken.None);

            var nested = Path.Combine(root, "nested");
            Directory.Delete(nested, recursive: true);
            Directory.CreateSymbolicLink(nested, outside);

            SandboxViolationException refused = await Assert.ThrowsAsync<SandboxViolationException>(
                () => writer.WriteAsync("nested/second.txt", "two", CancellationToken.None));

            Assert.False(string.IsNullOrWhiteSpace(refused.Message));

            // And nothing was left behind out there. A contained failure, not a silent escape.
            Assert.False(File.Exists(Path.Combine(outside, "second.txt")));
        }
        finally
        {
            TryDeleteTree(root);
            TryDeleteTree(outside);
        }
    }

    [Fact]
    public void TheSandboxRootIsRestrictedToItsOwnerRatherThanAssumedToBe()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = NewRoot();

        try
        {
            _ = new SandboxFileWriter(root);

            // ADR 0003 said the mitigation was operational — the root "should be" owner-only.
            // Should is not a control, so Aurora applies it.
            UnixFileMode mode = File.GetUnixFileMode(root);

            Assert.Equal(UnixFileMode.None, mode & UnixFileMode.GroupWrite);
            Assert.Equal(UnixFileMode.None, mode & UnixFileMode.OtherWrite);
            Assert.Equal(UnixFileMode.None, mode & UnixFileMode.OtherRead);
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static string NewRoot()
    {
        var root = TestTemp.Path("sbx");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
