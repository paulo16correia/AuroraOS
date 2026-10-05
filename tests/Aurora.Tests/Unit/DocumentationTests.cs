using System.Text.RegularExpressions;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// The documents an owner acts on say only what the code does.
/// </summary>
/// <remarks>
/// A runbook that names a setting which does not exist, or omits one that does, sends somebody who
/// is stuck to the wrong place at the moment they are reading it. These check the things a document
/// can get wrong by drifting rather than by argument: settings, commands, links, decision numbers
/// and the counts on the front page. Whether the prose is right is still a reviewer's question.
/// </remarks>
public sealed partial class DocumentationTests
{
    /// <summary>ADR numbers that were removed on purpose and are still cited by the records that removed them.</summary>
    private static readonly HashSet<string> RemovedAdrs = ["0039"];

    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Aurora.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test binaries.");
    }

    /// <summary>What an owner reads to install, run and secure Aurora — the documents that describe the current system.</summary>
    private static IEnumerable<string> OwnerDocuments() =>
        new[] { "README.md", "CONTRIBUTING.md", "SECURITY.md", Path.Combine("docs", "README.md") }
            .Select(path => Path.Combine(Root, path))
            .Concat(new[] { "guides", "reference", "integrations", "demo" }
                .SelectMany(folder => Directory.GetFiles(Path.Combine(Root, "docs", folder), "*.md")));

    private static IEnumerable<string> SourceFiles(string folder, string pattern) =>
        Directory.GetFiles(Path.Combine(Root, folder), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Source() =>
        string.Concat(SourceFiles("src", "*.cs").Select(File.ReadAllText));

    /// <summary>Every <c>Aurora:</c> key the code reads, and the sections it reads keys under.</summary>
    private static (HashSet<string> Keys, HashSet<string> Sections) Settings()
    {
        var source = Source();

        var keys = SettingLiteral().Matches(source)
            .Select(match => match.Groups[1].Value.TrimEnd(':'))
            .ToHashSet(StringComparer.Ordinal);

        var sections = SectionRead().Matches(source)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        return (keys, sections);
    }

    [Fact]
    public void EverySettingADocumentNamesExists()
    {
        (HashSet<string> keys, HashSet<string> sections) = Settings();
        var unknown = new List<string>();

        foreach (var document in OwnerDocuments())
        {
            var text = File.ReadAllText(document);

            IEnumerable<string> named = DocumentedSetting().Matches(text).Select(match => match.Value)
                .Concat(EnvironmentSetting().Matches(text).Select(match => match.Value.Replace("__", ":", StringComparison.Ordinal)));

            foreach (var key in named.Distinct(StringComparer.Ordinal))
            {
                // A key under a section the code enumerates — an interpreter per runtime — is real
                // whatever its last segment says.
                if (!keys.Contains(key) && !sections.Any(section => key.StartsWith(section + ":", StringComparison.Ordinal)))
                {
                    unknown.Add($"{Path.GetRelativePath(Root, document)}: {key}");
                }
            }
        }

        Assert.True(unknown.Count == 0, "Settings named in a document that Aurora never reads:\n" + string.Join('\n', unknown));
    }

    [Fact]
    public void EverySettingAuroraReadsIsInTheRunbook()
    {
        (HashSet<string> keys, _) = Settings();
        var runbook = File.ReadAllText(Path.Combine(Root, "docs", "guides", "operator-runbook.md"));

        // The runbook's settings table is where an owner looks to turn something on. A setting
        // that exists only in the code is one nobody can find at the moment they need it.
        var missing = keys.Where(key => !runbook.Contains(key, StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0, "Settings the runbook does not mention:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void EveryCommandADocumentRunsExists()
    {
        // The verbs are the string literals the console handlers compare the first argument with.
        var consoles = SourceFiles(Path.Combine("src", "Aurora.Server"), "*Console.cs")
            .Append(Path.Combine(Root, "src", "Aurora.Server", "Program.cs"))
            .Select(File.ReadAllText);

        var verbs = consoles
            .SelectMany(text => VerbLiteral().Matches(text).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var unknown = OwnerDocuments()
            .SelectMany(document => DocumentedCommand().Matches(File.ReadAllText(document))
                .Select(match => (Document: document, Verb: match.Groups[1].Value)))
            .Where(use => !verbs.Contains(use.Verb))
            .Select(use => $"{Path.GetRelativePath(Root, use.Document)}: {use.Verb}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0, "Commands a document runs that Aurora does not have:\n" + string.Join('\n', unknown));
    }

    [Fact]
    public void EveryRelativeLinkResolves()
    {
        var documents = Directory.GetFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.AllDirectories)
            .Concat(new[] { "README.md", "CONTRIBUTING.md", "SECURITY.md" }.Select(path => Path.Combine(Root, path)));

        var broken = new List<string>();

        foreach (var document in documents)
        {
            var text = CodeFence().Replace(File.ReadAllText(document), string.Empty);

            foreach (Match link in MarkdownLink().Matches(text))
            {
                var target = link.Groups[1].Value;
                if (target.StartsWith('#') || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal))
                {
                    continue;
                }

                var path = target.Split('#')[0];
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, path));

                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add($"{Path.GetRelativePath(Root, document)}: {target}");
                }
            }
        }

        Assert.True(broken.Count == 0, "Links to nothing:\n" + string.Join('\n', broken));
    }

    [Fact]
    public void EveryDecisionCitedExistsAndIsInTheIndex()
    {
        var records = Directory.GetFiles(Path.Combine(Root, "docs", "adr"), "*.md")
            .Select(Path.GetFileName)
            .Where(name => name is not null && AdrFile().IsMatch(name))
            .ToList();

        var numbers = records.Select(name => name![..4]).ToHashSet(StringComparer.Ordinal);
        var index = File.ReadAllText(Path.Combine(Root, "docs", "adr", "README.md"));

        Assert.All(records, name => Assert.Contains($"({name})", index, StringComparison.Ordinal));

        // "plugins" is not here because it is not a top-level tree any more — it lives under
        // src/Aurora.Server, which "src" already walks.
        var cited = new[] { "src", "tests", "docs" }
            .SelectMany(folder => SourceFiles(folder, "*.*"))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".md", StringComparison.Ordinal)
                || path.EndsWith(".py", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal))
            .Concat(new[] { "README.md", "CONTRIBUTING.md", "SECURITY.md" }.Select(path => Path.Combine(Root, path)))
            .SelectMany(path => AdrCitation().Matches(File.ReadAllText(path))
                .Select(match => (Path: path, Number: match.Groups[1].Value)))
            .Where(citation => !numbers.Contains(citation.Number) && !RemovedAdrs.Contains(citation.Number))
            .Select(citation => $"{Path.GetRelativePath(Root, citation.Path)}: docs/adr/{citation.Number}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(cited.Count == 0, "Decisions cited that do not exist:\n" + string.Join('\n', cited));
    }

    [Fact]
    public void TheFrontPageCountsWhatIsThere()
    {
        var adrs = Directory.GetFiles(Path.Combine(Root, "docs", "adr"), "*.md")
            .Count(path => AdrFile().IsMatch(Path.GetFileName(path)));

        var readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        var contributing = File.ReadAllText(Path.Combine(Root, "CONTRIBUTING.md"));

        Assert.Equal(adrs, int.Parse(AdrBadge().Match(readme).Groups[1].Value));
        Assert.Equal(adrs, int.Parse(AdrsInDocs().Match(readme).Groups[1].Value));
        Assert.Equal(adrs, int.Parse(AdrsInContributing().Match(contributing).Groups[1].Value));
    }

    [GeneratedRegex("\"(Aurora:[A-Za-z0-9:]+)")]
    private static partial Regex SettingLiteral();

    [GeneratedRegex("GetSection\\(\"(Aurora:[A-Za-z0-9:]+)\"\\)")]
    private static partial Regex SectionRead();

    [GeneratedRegex("Aurora:[A-Za-z0-9]+(?::[A-Za-z0-9]+)*")]
    private static partial Regex DocumentedSetting();

    [GeneratedRegex("Aurora__[A-Za-z0-9]+(?:__[A-Za-z0-9]+)*")]
    private static partial Regex EnvironmentSetting();

    [GeneratedRegex("\"([a-z][a-z-]+)\"")]
    private static partial Regex VerbLiteral();

    [GeneratedRegex("Aurora\\.Server\\s+--\\s+([a-z][a-z-]+)")]
    private static partial Regex DocumentedCommand();

    [GeneratedRegex("```.*?```", RegexOptions.Singleline)]
    private static partial Regex CodeFence();

    [GeneratedRegex("\\]\\(([^)\\s]+)\\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex("^\\d{4}-.+\\.md$")]
    private static partial Regex AdrFile();

    [GeneratedRegex("docs/adr/(\\d{4})")]
    private static partial Regex AdrCitation();

    [GeneratedRegex("ADRs-(\\d+)-")]
    private static partial Regex AdrBadge();

    [GeneratedRegex("of which (\\d+) are \\[ADRs\\]")]
    private static partial Regex AdrsInDocs();

    [GeneratedRegex("\\[(\\d+) of them\\]")]
    private static partial Regex AdrsInContributing();
}
