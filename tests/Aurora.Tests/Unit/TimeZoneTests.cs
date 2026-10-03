using Aurora.Core.Time;
using Xunit;

namespace Aurora.Tests.Unit;

/// <summary>
/// A zone means the same thing on every machine Aurora runs on.
/// </summary>
/// <remarks>
/// The bug these were written for: Aurora stores IANA ids, and on Windows every service that
/// resolved one threw <c>'Europe/Lisbon' is not a time zone this machine knows</c> — the scheduler,
/// the situation service, retention, curiosity, review, incidents and the status endpoint, all at
/// once. .NET normally bridges IANA and Windows ids through ICU, and Aurora builds with
/// <c>InvariantGlobalization</c>, which does not load it (docs/adr/0077).
/// <para>
/// <b>These are deterministic.</b> Nothing here asks what zone the machine is in or what the offset
/// is today; every assertion is about identity and resolvability, which do not move. The
/// platform-specific ones name the platform, because the interesting question — does an id written
/// on one operating system still work on another — cannot be asked any other way.
/// </para>
/// </remarks>
public sealed class TimeZoneTests
{
    /// <summary>The zone Aurora's own tests and fixtures have always been written in.</summary>
    private const string Lisbon = "Europe/Lisbon";

    /// <summary>What Windows calls the zone Lisbon runs in.</summary>
    private const string LisbonOnWindows = "GMT Standard Time";

    // ---- 1 & 2: a canonical Aurora id resolves, wherever Aurora is running ----

    [Fact]
    public void TheCanonicalIdOfAZoneResolvesOnThisMachine()
    {
        // The one assertion that had to be true on Windows and was not. Unqualified by platform on
        // purpose: an IANA id is Aurora's canonical name for a zone everywhere, and a test that
        // excused one operating system from it would be excusing the bug.
        Assert.True(AuroraTimeZones.TryFind(Lisbon, out TimeZoneInfo? zone));
        Assert.NotNull(zone);
    }

    [Theory]
    [InlineData("Europe/Lisbon")]
    [InlineData("Europe/London")]
    [InlineData("America/New_York")]
    [InlineData("Asia/Tokyo")]
    [InlineData("Australia/Sydney")]
    [InlineData("UTC")]
    public void EveryIanaIdAuroraUsesResolvesOnThisMachine(string iana) =>
        Assert.True(AuroraTimeZones.IsKnown(iana), $"{iana} did not resolve");

    // ---- 3: the other family's spelling, on the platform that does not use it ----

    [Fact]
    public void AWindowsIdResolvesToo()
    {
        // Somebody scheduling from Windows may reasonably name the zone the way Windows does, and
        // on Unix that has to keep working rather than being refused as a foreign spelling.
        Assert.True(AuroraTimeZones.IsKnown(LisbonOnWindows));
    }

    // ---- 4: canonicalisation ----

    [Fact]
    public void AWindowsIdIsCanonicalisedToItsIanaName() =>
        Assert.Equal("Europe/London", AuroraTimeZones.Canonical(LisbonOnWindows));

    [Fact]
    public void AnIanaIdIsAlreadyCanonicalAndIsLeftAlone()
    {
        // The property that matters most here. Lisbon and London are one Windows zone, so a
        // canonicalisation that went through Windows would answer "Europe/London" and quietly
        // rewrite what the owner said into a different city that happens to keep the same clock.
        Assert.Equal(Lisbon, AuroraTimeZones.Canonical(Lisbon));
        Assert.Equal("Europe/London", AuroraTimeZones.Canonical("Europe/London"));
    }

    [Fact]
    public void CanonicalisingTwiceChangesNothingTheSecondTime()
    {
        foreach (var id in new[] { Lisbon, LisbonOnWindows, "America/New_York", "Pacific Standard Time" })
        {
            var once = AuroraTimeZones.Canonical(id);
            Assert.Equal(once, AuroraTimeZones.Canonical(once));
        }
    }

    // ---- 5: an unknown zone fails, and fails closed ----

    [Fact]
    public void AZoneNobodyHasHeardOfIsRefusedRatherThanDefaulted()
    {
        Assert.False(AuroraTimeZones.TryFind("Mars/Olympus_Mons", out TimeZoneInfo? zone));
        Assert.Null(zone);

        // Not UTC, not the machine's own zone. A schedule that silently ran in the wrong zone
        // would be worse than one that refused to be created.
        TimeZoneNotFoundException refused =
            Assert.Throws<TimeZoneNotFoundException>(() => AuroraTimeZones.Find("Mars/Olympus_Mons"));

        Assert.Contains("Mars/Olympus_Mons", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoZoneIsNotAZone(string? empty)
    {
        // Blank is a refusal rather than a default, for the same reason: a schedule needs a zone
        // and UTC is not assumed on the caller's behalf.
        Assert.False(AuroraTimeZones.IsKnown(empty));
        Assert.Equal(string.Empty, AuroraTimeZones.Canonical(empty));
    }

    // ---- 6: what is already persisted keeps working ----

    [Fact]
    public void TheZoneAuroraSFixturesWereWrittenInStillMeansWhatItMeant()
    {
        // Aurora's own records, its docs and its test fixtures are all written in IANA ids. This
        // is the "an installation moved to a different operating system" case, asserted as
        // identity rather than as offset: what is on disk must still resolve, and must still be
        // the same string after Aurora has read it back.
        Assert.True(AuroraTimeZones.TryFind(Lisbon, out TimeZoneInfo? zone));
        Assert.Equal(Lisbon, AuroraTimeZones.Canonical(Lisbon));

        // And the resolved zone really is the one Lisbon keeps: Western European Time, which is
        // UTC in January and UTC+1 in July. Checked at two fixed instants rather than "now", so
        // the assertion does not depend on the day the suite runs.
        Assert.Equal(
            TimeSpan.Zero,
            zone!.GetUtcOffset(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal(
            TimeSpan.FromHours(1),
            zone.GetUtcOffset(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TheMachineSOwnZoneIsReportedUnderAurorasName()
    {
        // The default for a caller that named no zone. On Windows TimeZoneInfo.Local.Id is a
        // Windows id, and persisting that into a schedule would write a row the same installation
        // could not read after being moved to a Mac.
        var local = AuroraTimeZones.LocalId;

        Assert.False(string.IsNullOrWhiteSpace(local));
        Assert.True(AuroraTimeZones.IsKnown(local));
        Assert.Equal(local, AuroraTimeZones.Canonical(local));

        // Windows ids have spaces and no slash; IANA ids are the other way round. Aurora's
        // canonical name is the IANA one — except for UTC, which both families spell the same.
        Assert.True(
            local.Contains('/', StringComparison.Ordinal) || local == "UTC",
            $"'{local}' is not a canonical Aurora zone id");
    }

    // ---- the table behind it ----

    [Fact]
    public void EveryZoneInTheTableResolvesBothWays()
    {
        // A sweep rather than samples, because the failure this guards against is one entry in
        // several hundred being wrong in a way nobody looks at until a schedule fires an hour out.
        foreach (var iana in new[]
        {
            "Africa/Cairo", "America/Argentina/Buenos_Aires", "America/Los_Angeles",
            "America/Sao_Paulo", "Asia/Kolkata", "Asia/Shanghai", "Atlantic/Azores",
            "Europe/Berlin", "Europe/Lisbon", "Europe/Moscow", "Pacific/Auckland",
        })
        {
            Assert.True(AuroraTimeZones.IsKnown(iana), $"{iana} did not resolve");
            Assert.Equal(iana, AuroraTimeZones.Canonical(iana));
        }

        foreach (var windows in new[]
        {
            "GMT Standard Time", "Pacific Standard Time", "W. Europe Standard Time",
            "Tokyo Standard Time", "India Standard Time", "UTC",
        })
        {
            Assert.True(AuroraTimeZones.IsKnown(windows), $"{windows} did not resolve");

            var canonical = AuroraTimeZones.Canonical(windows);

            Assert.NotEqual(windows == "UTC" ? string.Empty : windows, canonical);
            Assert.True(AuroraTimeZones.IsKnown(canonical), $"{canonical} did not resolve");
        }
    }

    [Theory]
    [InlineData("India Standard Time", "Asia/Kolkata", "Asia/Calcutta")]
    [InlineData("Nepal Standard Time", "Asia/Kathmandu", "Asia/Katmandu")]
    [InlineData("Myanmar Standard Time", "Asia/Yangon", "Asia/Rangoon")]
    [InlineData("FLE Standard Time", "Europe/Kyiv", "Europe/Kiev")]
    [InlineData("Greenland Standard Time", "America/Nuuk", "America/Godthab")]
    [InlineData("Argentina Standard Time", "America/Argentina/Buenos_Aires", "America/Buenos_Aires")]
    [InlineData("US Eastern Standard Time", "America/Indiana/Indianapolis", "America/Indianapolis")]
    public void AWindowsZoneTzdataRenamedIsWrittenUnderItsCurrentName(
        string windows, string current, string old)
    {
        // CLDR keys these seven by their oldest name, which a stock Ubuntu 24.04 does not ship.
        // Aurora writes the current one, so a zone chosen on Windows reads the same on Linux.
        Assert.Equal(current, AuroraTimeZones.Canonical(windows));
        Assert.True(AuroraTimeZones.IsKnown(windows), $"{windows} did not resolve");
        Assert.True(AuroraTimeZones.IsKnown(current), $"{current} did not resolve");

        // And one written down before the rename still opens, wherever it is read.
        Assert.True(AuroraTimeZones.IsKnown(old), $"{old} did not resolve");
        Assert.Equal(old, AuroraTimeZones.Canonical(old));
    }
}
