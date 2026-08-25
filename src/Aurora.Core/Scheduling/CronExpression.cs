using System.Globalization;

namespace Aurora.Core.Scheduling;

/// <summary>
/// A five-field cron expression: minute, hour, day-of-month, month, day-of-week.
/// </summary>
/// <remarks>
/// Written here rather than taken from a package. The syntax is small and closed, the semantics are
/// the classic ones, and a scheduling bug is easier to find in forty lines that can be read than in
/// a dependency that has to be trusted. Supports <c>*</c>, a value, <c>a-b</c>, a list with
/// <c>,</c>, and a step with <c>/</c>.
/// </remarks>
public sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];

    private bool _dayOfMonthRestricted;
    private bool _dayOfWeekRestricted;

    private CronExpression()
    {
    }

    public static bool TryParse(string? expression, out CronExpression? cron, out string? error)
    {
        cron = null;
        error = null;

        var fields = (expression ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length != 5)
        {
            error = "A cron expression has five fields: minute hour day-of-month month day-of-week.";
            return false;
        }

        var parsed = new CronExpression();

        if (!Fill(fields[0], 0, 59, parsed._minutes, "minute", ref error)
            || !Fill(fields[1], 0, 23, parsed._hours, "hour", ref error)
            || !Fill(fields[2], 1, 31, parsed._daysOfMonth, "day-of-month", ref error)
            || !Fill(fields[3], 1, 12, parsed._months, "month", ref error)
            || !FillDaysOfWeek(fields[4], parsed._daysOfWeek, ref error))
        {
            return false;
        }

        parsed._dayOfMonthRestricted = fields[2] != "*";
        parsed._dayOfWeekRestricted = fields[4] != "*";

        cron = parsed;
        return true;
    }

    /// <summary>
    /// The first matching wall-clock minute strictly after <paramref name="afterLocal"/>, or null
    /// if there is none within <paramref name="horizonDays"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately works in local wall-clock time and knows nothing about offsets. Which UTC
    /// instant a wall time corresponds to — and whether it exists at all, or exists twice — is a
    /// question about a time zone, and belongs to the caller that has one.
    /// </remarks>
    public DateTime? NextLocal(DateTime afterLocal, int horizonDays = 366)
    {
        DateTime candidate = Truncate(afterLocal).AddMinutes(1);
        DateTime horizon = candidate.AddDays(horizonDays);

        while (candidate < horizon)
        {
            if (!MatchesDate(candidate))
            {
                // Skip the whole day rather than its 1,440 minutes: a day-of-month or day-of-week
                // field that excludes today excludes every minute of today.
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return candidate;
        }

        return null;
    }

    private bool MatchesDate(DateTime candidate)
    {
        if (!_months[candidate.Month])
        {
            return false;
        }

        var dayOfMonth = _daysOfMonth[candidate.Day];
        var dayOfWeek = _daysOfWeek[(int)candidate.DayOfWeek];

        // The classic rule: when both fields are restricted they are OR-ed, because "the 1st and
        // every Monday" is what people mean by writing both. When only one is restricted, it alone
        // decides.
        return (_dayOfMonthRestricted, _dayOfWeekRestricted) switch
        {
            (true, true) => dayOfMonth || dayOfWeek,
            (true, false) => dayOfMonth,
            (false, true) => dayOfWeek,
            _ => true,
        };
    }

    private static DateTime Truncate(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);

    private static bool FillDaysOfWeek(string field, bool[] target, ref string? error)
    {
        // Both 0 and 7 name Sunday in the wild; accepting either costs nothing and surprises nobody.
        var normalized = string.Join(
            ',',
            field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part == "7" ? "0" : part));

        return Fill(normalized.Length == 0 ? field : normalized, 0, 6, target, "day-of-week", ref error);
    }

    private static bool Fill(string field, int min, int max, bool[] target, string name, ref string? error)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var step = 1;
            var value = part;

            var slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                value = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], CultureInfo.InvariantCulture, out step) || step <= 0)
                {
                    error = $"The {name} field has an invalid step in '{part}'.";
                    return false;
                }
            }

            int from;
            int to;
            if (value is "*")
            {
                from = min;
                to = max;
            }
            else if (value.IndexOf('-', StringComparison.Ordinal) is var dash && dash > 0)
            {
                if (!int.TryParse(value[..dash], CultureInfo.InvariantCulture, out from)
                    || !int.TryParse(value[(dash + 1)..], CultureInfo.InvariantCulture, out to))
                {
                    error = $"The {name} field has an invalid range in '{part}'.";
                    return false;
                }
            }
            else if (int.TryParse(value, CultureInfo.InvariantCulture, out from))
            {
                to = slash >= 0 ? max : from;
            }
            else
            {
                error = $"The {name} field has an invalid value in '{part}'.";
                return false;
            }

            if (from < min || to > max || from > to)
            {
                error = $"The {name} field is out of range in '{part}'; it accepts {min}-{max}.";
                return false;
            }

            for (var i = from; i <= to; i += step)
            {
                target[i] = true;
            }
        }

        if (Array.IndexOf(target, true) < 0)
        {
            error = $"The {name} field matches nothing.";
            return false;
        }

        return true;
    }
}
