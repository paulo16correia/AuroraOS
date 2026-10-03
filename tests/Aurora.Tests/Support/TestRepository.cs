namespace Aurora.Tests.Support;

/// <summary>
/// Where the repository is, and where the plugins are inside it.
/// </summary>
/// <remarks>
/// <para>
/// Nine test files had their own copy of the same loop: walk up from the test binary until a folder
/// named <c>plugins</c> appears, and call that the repository. It worked for as long as the plugins
/// sat at the root, and the day they moved under the server project it stopped — every one of them
/// walked past the repository, found nothing, and failed on an assertion about a directory rather
/// than about anything a reader of the test cares about.
/// </para>
/// <para>
/// So: one place, and a marker that is not going to move. <c>Aurora.slnx</c> is what makes a
/// directory the root of this repository; a folder that happens to be there is a coincidence the
/// tests were relying on.
/// </para>
/// </remarks>
public static class TestRepository
{
    /// <summary>What marks the repository root. The solution, because that is what one is.</summary>
    private const string Marker = "Aurora.slnx";

    private static DirectoryInfo? _root;

    /// <summary>The repository root, found by walking up from the test binary.</summary>
    public static DirectoryInfo Root()
    {
        if (_root is not null)
        {
            return _root;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, Marker)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            // Said as a sentence rather than as a null reference three frames later. A test run from
            // somewhere unexpected should say what it was looking for.
            throw new InvalidOperationException(
                $"no {Marker} above {AppContext.BaseDirectory}, so the repository root is unknown");
        }

        return _root = directory;
    }

    /// <summary>
    /// Where the plugins live in the source tree.
    /// </summary>
    /// <remarks>
    /// Not where they live once installed — that is a folder beside the database, in the owner's data
    /// directory, and these are the ones a test copies from.
    /// </remarks>
    public static string Plugins() =>
        Path.Combine(Root().FullName, "src", "Aurora.Server", "plugins");

    /// <summary>One plugin's source folder, by the name of its directory.</summary>
    public static string Plugin(string name) => Path.Combine(Plugins(), name);
}
