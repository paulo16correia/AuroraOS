using Aurora.Adapters.Files;
using Aurora.Core.Abstractions;
using Xunit;

namespace Aurora.Tests.Unit;

public sealed class SandboxFileWriterTests
{
    /// <summary>A throwaway sandbox root, plus an outside directory to attempt escapes into.</summary>
    private sealed class TempSandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"aurora-sbx-{Guid.NewGuid():N}");

        public string Outside { get; } = Path.Combine(Path.GetTempPath(), $"aurora-out-{Guid.NewGuid():N}");

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
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (IOException)
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
        Directory.CreateSymbolicLink(link, sandbox.Outside);

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
        File.CreateSymbolicLink(Path.Combine(sandbox.Root, "innocent.txt"), target);

        var writer = new SandboxFileWriter(sandbox.Root);

        await Assert.ThrowsAsync<SandboxViolationException>(
            () => writer.WriteAsync("innocent.txt", "pwned", CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Write_ResolvesSandboxRootThroughItsOwnSymlink()
    {
        using var sandbox = new TempSandbox();
        var linkedRoot = Path.Combine(Path.GetTempPath(), $"aurora-link-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(linkedRoot, sandbox.Root);

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
        var outside = Path.Combine(Path.GetTempPath(), $"aurora-outside-{Guid.NewGuid():N}");
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
        var root = Path.Combine(Path.GetTempPath(), $"aurora-sbx-{Guid.NewGuid():N}");
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
