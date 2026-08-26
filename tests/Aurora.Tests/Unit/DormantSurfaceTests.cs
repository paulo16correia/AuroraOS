using System.Reflection;
using Aurora.Core.Abstractions;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// Machinery that exists, is tested, and has nothing to drive it — asserted so that it stays a
/// decision rather than becoming an oversight (docs/adr/0059).
/// </summary>
/// <remarks>
/// Two of the findings in the conformance pass were of exactly this shape: `DescribeAsync` was
/// implemented and unreachable, and RFC 08's `TESTING` state was declared and never entered. Both
/// went unnoticed for as long as they did because nothing said out loud that they were unused.
/// </remarks>
public sealed class DormantSurfaceTests
{
    [Fact]
    public void NoToolConnectorShipsWithAurora()
    {
        Type[] connectors =
        [
            .. typeof(IToolManager).Assembly.GetTypes(),
            .. typeof(Aurora.Adapters.Tools.SqliteToolManager).Assembly.GetTypes(),
        ];

        var shipped = connectors
            .Where(t => typeof(IToolConnector).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => t.Name)
            .ToList();

        // RFC 06's propose/authorize/dispatch/reconcile path is complete and dormant: Aurora runs
        // on one machine and reaches nothing outside it, so there is no connector to drive it.
        // The registry itself is not dormant — an incident disables a tool through it.
        //
        // If this fails, somebody added a connector. That is allowed and it is not free: the
        // kernel does not call the tool manager, so a connector added without wiring that up would
        // be a capability nothing can invoke, and LAW-002's compliance test would be the only
        // thing exercising it. Wire it, then delete this test.
        Assert.Empty(shipped);
    }

    /// <summary>
    /// Seams Aurora ships without filling, and why each one is empty on purpose.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownDormant =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // RFC 06's connector seam. Aurora reaches nothing outside this machine, so there is
            // nothing to connect to; the tool manager behind it is used, for incident containment.
            ["IToolConnector"] = "Aurora is local-only and ships no external connector",
        };

    [Fact]
    public void TheOnlyUnimplementedAbstractionsAreTheOnesThatAreMeantToBe()
    {
        Assembly core = typeof(IToolManager).Assembly;

        Type[] implementations =
        [
            .. typeof(Aurora.Adapters.Tools.SqliteToolManager).Assembly.GetTypes(),
            .. core.GetTypes(),
            .. typeof(Aurora.Server.Mcp.AuroraTools).Assembly.GetTypes(),
        ];

        var orphaned = core.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && t.Name.StartsWith('I'))
            .Where(contract => !implementations.Any(
                t => t is { IsInterface: false, IsAbstract: false } && contract.IsAssignableFrom(t)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // An interface with no implementation is a decision somebody described and did not make.
        // Sometimes that is right — but it should be found here, with a reason beside it, rather
        // than by a reader a year later wondering which half of the system is missing.
        Assert.Equal(KnownDormant.Keys.OrderBy(n => n, StringComparer.Ordinal), orphaned);
    }
}
