using System.Reflection;
using Aurora.Core.Contracts;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Nothing Aurora declares as mandatory is declared and then never used.
/// </summary>
/// <remarks>
/// The conformance pass found four of these and each took reading to notice: an event type nobody
/// published, a state nothing could reach, an interface with no implementation, a column pointing
/// at an object that did not exist. They are invisible precisely because every part of them
/// compiles and every test of the part passes.
/// <para>
/// So they are asserted instead of looked for. A constant that stops being reachable fails here,
/// on the day it stops, rather than in a review six months later.
/// </para>
/// </remarks>
public sealed class DeadContractTests
{
    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src");
    }

    private static string AllSource()
    {
        var text = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            text.Add(File.ReadAllText(file));
        }

        return string.Join("\n", text);
    }

    [Fact]
    public void EveryDeclaredEventTypeIsPublishedBySomething()
    {
        var source = AllSource();

        var never = EventCatalogue.Declared
            .Select(contract => contract.Type)
            .Distinct(StringComparer.Ordinal)

            // Published means named at a call site as EventCatalogue.{Type}. The catalogue's own
            // declaration uses the bare constant, so it does not count itself.
            .Where(type => CountOf(source, $"EventCatalogue.{type}") < 1)
            .ToList();

        // A declared event nobody emits is a contract consumers can subscribe to and never hear
        // from — worse than an absent one, because the subscription looks like it is working.
        Assert.Empty(never);
    }

    [Theory]
    [InlineData(typeof(InstallationStatus))]
    [InlineData(typeof(LearningProposalState))]
    [InlineData(typeof(ApprovalStatus))]
    [InlineData(typeof(WorkItemStatus))]
    [InlineData(typeof(MindChangeSetStatus))]
    [InlineData(typeof(MindStatus))]
    [InlineData(typeof(IncidentStatus))]
    [InlineData(typeof(EvaluationVerdict))]
    [InlineData(typeof(ConstitutionalResult))]
    public void EveryDeclaredStateCanBeReached(Type states)
    {
        var source = AllSource();

        var unreachable = states.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => $"{states.Name}.{f.Name}")

            // Reachable means the constant is used somewhere other than where it is declared.
            .Where(name => CountOf(source, name) < 1)
            .ToList();

        // A state that nothing assigns is a promise about a lifecycle that does not exist. Every
        // one of these was found the hard way: REMOVED could not be reached, TESTING was never
        // entered, EXPIRED did not exist and its absence deadlocked a scope.
        Assert.Empty(unreachable);
    }

    [Fact]
    public void EveryDeclaredRefusalReasonIsProducedBySomething()
    {
        var source = AllSource();

        var never = typeof(PluginRefusal).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => $"PluginRefusal.{f.Name}")
            .Where(name => CountOf(source, name) < 1)
            .ToList();

        // A refusal reason nothing returns is a reason nobody will ever be given, and a closed set
        // that is not actually closed.
        Assert.Empty(never);
    }

    [Fact]
    public void EverySecurityEventTypeIsRaisedBySomething()
    {
        var source = AllSource();

        var never = typeof(SecurityEventType).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => $"SecurityEventType.{f.Name}")
            .Where(name => CountOf(source, name) < 1)
            .ToList();

        // Two of these were declared and raised by nothing until docs/adr/0064. An incident type
        // nobody produces is a category of attack Aurora says it watches for and does not.
        Assert.Empty(never);
    }

    /// <summary>Occurrences outside the declaration itself.</summary>
    private static int CountOf(string source, string token)
    {
        var count = 0;
        var at = 0;

        while ((at = source.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += token.Length;
        }

        return count;
    }
}
