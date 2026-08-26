using Aurora.Core.Files;
using Xunit;
using Aurora.Tests.Support;

namespace Aurora.Tests.Unit;

public sealed class SandboxPathTests
{
    private static readonly string Root =
        TestTemp.Path("validator-root");

    [Theory]
    // Traversal, in the shapes that actually get tried.
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("a/./b.txt")]
    // Absolute and separator-anchored.
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32\\cfg")]
    // UNC and device namespaces.
    [InlineData("//server/share/f.txt")]
    [InlineData("\\\\server\\share\\f.txt")]
    // Drive-relative and alternate data streams both need ':'.
    [InlineData("C:file.txt")]
    [InlineData("notes.txt:hidden")]
    // Windows reserved device names, bare and with an extension.
    [InlineData("NUL")]
    [InlineData("con.txt")]
    [InlineData("sub/LPT1.log")]
    // Names Windows silently rewrites, which would let two inputs collide on one file.
    [InlineData("trailing .txt ")]
    [InlineData("trailing.")]
    // Structural junk.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a//b.txt")]
    public void Validate_RejectsUnsafePath(string relativePath)
    {
        SandboxPathResult result = SandboxPathValidator.Validate(Root, relativePath);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Reason);
        Assert.Null(result.FullPath);
    }

    [Fact]
    public void Validate_RejectsControlCharacters()
    {
        Assert.False(SandboxPathValidator.Validate(Root, "a\0b.txt").IsValid);
        Assert.False(SandboxPathValidator.Validate(Root, "a\nb.txt").IsValid);
    }

    [Fact]
    public void Validate_RejectsOverlongPath()
    {
        var tooLong = new string('a', SandboxPathValidator.MaxRelativePathChars + 1);

        Assert.False(SandboxPathValidator.Validate(Root, tooLong).IsValid);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("sub/notes.txt")]
    [InlineData("a/b/c/deep.txt")]
    [InlineData("dotted.name.txt")]
    [InlineData("consortium.txt")] // starts with "con" but is not the CON device
    public void Validate_AcceptsSafeRelativePath(string relativePath)
    {
        SandboxPathResult result = SandboxPathValidator.Validate(Root, relativePath);

        Assert.True(result.IsValid, result.Reason);
        Assert.NotNull(result.FullPath);
        Assert.StartsWith(Path.GetFullPath(Root), result.FullPath!, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptedPathStaysUnderRoot()
    {
        var result = SandboxPathValidator.Validate(Root, "sub/notes.txt");

        var expected = Path.Combine(Path.GetFullPath(Root), "sub", "notes.txt");
        Assert.Equal(expected, result.FullPath);
    }
}
