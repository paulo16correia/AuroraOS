using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Aurora.Core.Time;

/// <summary>
/// The one place Aurora turns a time zone id into a time zone.
/// </summary>
/// <remarks>
/// <b>Aurora's canonical name for a zone is its IANA id</b>, on every platform, and that is what is
/// written into a schedule, a review request or a situation context. A zone is a piece of the
/// owner's meaning — "nine in the morning in Lisbon" — and it must not change because Aurora was
/// restarted on a different operating system.
/// <para>
/// Windows disagrees. Its registry names the same zone <c>GMT Standard Time</c>, and .NET bridges
/// the two through ICU: <c>FindSystemTimeZoneById</c> accepts an IANA id on Windows, and
/// <see cref="TimeZoneInfo.TryConvertIanaIdToWindowsId(string, out string?)"/> converts between
/// them. Aurora builds with <c>InvariantGlobalization</c>, which does not load ICU — so on Windows
/// all three of those fail, and every service that resolves a zone fails with them. That is the
/// bug this type exists to close, and it is closed here rather than at each call site: a
/// <c>OperatingSystem.IsWindows()</c> check inside a scheduler is a platform seam in the middle of
/// business logic, and the fourth copy of it is the one that gets the fallback wrong.
/// </para>
/// <para>
/// <b>Order of resort.</b> The platform is asked first, so a runtime that does have ICU — or a
/// Windows registry that has the id verbatim — is believed before anything here. Only when the
/// platform does not know the id is <see cref="WindowsZoneTable"/> consulted, and only to translate
/// the id into the other family's spelling; the zone itself always comes from the operating system.
/// Aurora never invents offsets or DST rules.
/// </para>
/// <para>
/// <b>Fail closed.</b> An id that resolves nowhere is refused by name. Nothing falls back to UTC,
/// to the machine's local zone, or to the nearest offset: a schedule that silently ran an hour out
/// would be worse than one that refused to be created.
/// </para>
/// </remarks>
public static class AuroraTimeZones
{
    /// <summary>IANA id to Windows id, and the reverse, from CLDR.</summary>
    private static readonly Lazy<Mapping> Table = new(Mapping.Parse, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// This machine's own zone, under Aurora's canonical name.
    /// </summary>
    /// <remarks>
    /// The default for a caller that did not name one. On Windows <see cref="TimeZoneInfo.Local"/>
    /// is a Windows id, so it is canonicalised here — otherwise a request defaulted on Windows
    /// would persist <c>GMT Standard Time</c> into a schedule that a Mac then could not read.
    /// </remarks>
    public static string LocalId => Canonical(TimeZoneInfo.Local.Id);

    /// <summary>
    /// Aurora's canonical id for a zone: the IANA one, whichever family was handed in.
    /// </summary>
    /// <remarks>
    /// An IANA id is returned unchanged even when several of them share one Windows zone.
    /// <c>Europe/Lisbon</c> and <c>Europe/London</c> both run under <c>GMT Standard Time</c>, and
    /// canonicalising Lisbon to London would rewrite what the owner said into something that
    /// merely keeps the same clock this decade.
    /// <para>
    /// An id that is neither is returned unchanged too. This method names things; it does not
    /// decide whether they exist — <see cref="TryFind"/> does that, and a caller that has not
    /// resolved the zone yet should not be handed a different string than it supplied.
    /// </para>
    /// </remarks>
    public static string Canonical(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return string.Empty;
        }

        var id = timeZoneId.Trim();

        if (Table.Value.IanaToWindows.ContainsKey(id))
        {
            // Already IANA. CLDR knows it, and the id the owner wrote is the one that is kept.
            return id;
        }

        if (Table.Value.WindowsToIana.TryGetValue(id, out var iana))
        {
            return iana;
        }

        // The platform may know a conversion this table does not — a newer zone under an ICU
        // newer than the embedded CLDR. Asked second, because the answer must be the same on
        // every machine wherever both know it.
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var converted) ? converted : id;
    }

    /// <summary>
    /// Resolves a canonical Aurora zone id, or any id this machine happens to know, to a zone.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> and the zone, or <see langword="false"/> and nothing. A blank id is
    /// <see langword="false"/>: no zone is the caller's to default.
    /// </returns>
    public static bool TryFind(string? timeZoneId, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        var id = timeZoneId.Trim();

        // The platform first: IANA on Unix, Windows ids on Windows, and either on a runtime with
        // ICU. Everything below is for the one case this does not cover.
        if (Lookup(id) is { } known)
        {
            zone = known;
            return true;
        }

        var other = OperatingSystem.IsWindows()
            ? ToWindows(id)
            : ToIana(id);

        if (other is null)
        {
            return false;
        }

        zone = Lookup(other);
        return zone is not null;
    }

    /// <summary>
    /// The zone for an id, or <see cref="TimeZoneNotFoundException"/> naming the id that failed.
    /// </summary>
    public static TimeZoneInfo Find(string? timeZoneId) =>
        TryFind(timeZoneId, out TimeZoneInfo? zone)
            ? zone
            : throw new TimeZoneNotFoundException(
                $"'{timeZoneId}' is not a time zone this machine knows.");

    /// <summary>Whether this machine can resolve the id at all.</summary>
    public static bool IsKnown(string? timeZoneId) => TryFind(timeZoneId, out _);

    /// <summary>The Windows spelling of an IANA id, from the platform or from CLDR.</summary>
    private static string? ToWindows(string ianaId) =>
        TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windows)
            ? windows
            : Table.Value.IanaToWindows.GetValueOrDefault(ianaId);

    /// <summary>The IANA spelling of a Windows id, from the platform or from CLDR.</summary>
    private static string? ToIana(string windowsId) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var iana)
            ? iana
            : Table.Value.WindowsToIana.GetValueOrDefault(windowsId);

    private static TimeZoneInfo? Lookup(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception unknown)
            when (unknown is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Not knowing a zone is an ordinary answer to a question about an id somebody typed,
            // and the callers above turn it into a refusal that names the id. An exception
            // escaping here would make "try the other spelling" impossible to express.
            return null;
        }
    }

    /// <summary>The parsed table, both ways round.</summary>
    private sealed record Mapping(
        FrozenDictionary<string, string> IanaToWindows,
        FrozenDictionary<string, string> WindowsToIana)
    {
        internal static Mapping Parse()
        {
            var ianaToWindows = new Dictionary<string, string>(StringComparer.Ordinal);

            // Windows ids are matched case-insensitively because that is how Windows itself
            // compares them; IANA ids are not, because tzdata treats case as significant.
            var windowsToIana = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var windows = string.Empty;

            foreach (var line in WindowsZoneTable.Entries.Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                ReadOnlySpan<char> ids;

                if (char.IsWhiteSpace(line[0]))
                {
                    // A continuation of the entry above: more IANA ids for the same Windows zone.
                    ids = line.AsSpan().Trim();
                }
                else
                {
                    var split = line.IndexOf('=', StringComparison.Ordinal);
                    windows = line[..split];
                    ids = line.AsSpan(split + 1).Trim();

                    // The first id on the first line is CLDR's territory 001 default, which is
                    // what this Windows zone canonicalises to.
                    var primary = ids;
                    var space = ids.IndexOf(' ');

                    if (space >= 0)
                    {
                        primary = ids[..space];
                    }

                    windowsToIana[windows] = primary.ToString();
                }

                foreach (Range part in ids.Split(' '))
                {
                    ReadOnlySpan<char> iana = ids[part];

                    if (!iana.IsEmpty)
                    {
                        ianaToWindows[iana.ToString()] = windows;
                    }
                }
            }

            return new Mapping(ianaToWindows.ToFrozenDictionary(), windowsToIana.ToFrozenDictionary());
        }
    }
}
