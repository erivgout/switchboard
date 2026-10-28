namespace Switchboard.Modules.ServiceLevels.Time;

/// <summary>A half-open span of time <c>[Start, End)</c>.</summary>
public readonly record struct TimeRange(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;

    /// <summary>An exclusion that has started but not ended (an open pause).</summary>
    public bool IsOpen => End == DateTimeOffset.MaxValue;
}

/// <summary>
/// A working interval on a weekday, in minutes after local midnight. <see cref="EndMinute"/> may pass
/// 1440, in which case the interval runs into the next day (an overnight shift).
/// </summary>
public readonly record struct WorkingInterval(DayOfWeek Day, int StartMinute, int EndMinute)
{
    public const int MinutesPerDay = 1440;

    public bool IsValid =>
        StartMinute is >= 0 and < MinutesPerDay && EndMinute > StartMinute && EndMinute - StartMinute <= MinutesPerDay;
}

/// <summary>
/// Pure business-time arithmetic over a calendar: working intervals per weekday in a timezone, minus
/// holidays (INV-TIME-1). Instants in and out are absolute; local wall-clock rules only decide which
/// instants are working time. No I/O and no clock: the same inputs always give the same answer
/// (INV-SLA-3).
/// </summary>
/// <remarks>
/// DST rules, in local wall-clock terms:
/// <list type="bullet">
/// <item>A local time that does not exist (spring forward) maps to the instant just after the gap, so
/// an interval inside the gap is empty and one spanning it is shorter by the gap.</item>
/// <item>A local time that occurs twice (fall back) maps to its first occurrence, so an interval spanning
/// the repeated hour is longer by it.</item>
/// <item>A holiday removes the whole local calendar day, including the part of an overnight interval that
/// spills into it.</item>
/// </list>
/// </remarks>
public sealed class BusinessCalendar
{
    /// <summary>Give up looking for working time after this many days (a calendar with no working time).</summary>
    private const int HorizonDays = 366 * 5;

    private readonly List<WorkingInterval>[] _byDay = new List<WorkingInterval>[7];
    private readonly HashSet<DateOnly> _holidays;

    public BusinessCalendar(TimeZoneInfo zone, IEnumerable<WorkingInterval> intervals, IEnumerable<DateOnly>? holidays = null)
    {
        Zone = zone;
        for (var i = 0; i < 7; i++)
        {
            _byDay[i] = [];
        }
        foreach (var interval in intervals)
        {
            if (!interval.IsValid)
            {
                throw new ArgumentException($"Invalid working interval {interval}.", nameof(intervals));
            }
            _byDay[(int)interval.Day].Add(interval);
        }
        _holidays = holidays is null ? [] : [.. holidays];
        HasWorkingTime = _byDay.Any(d => d.Count > 0);
    }

    /// <summary>Every minute of every day, in UTC, with no holidays: plain calendar time.</summary>
    public static BusinessCalendar AlwaysOpen { get; } = TwentyFourSeven(TimeZoneInfo.Utc);

    public TimeZoneInfo Zone { get; }

    public bool HasWorkingTime { get; }

    public static BusinessCalendar TwentyFourSeven(TimeZoneInfo zone, IEnumerable<DateOnly>? holidays = null) =>
        new(zone, Enum.GetValues<DayOfWeek>().Select(d => new WorkingInterval(d, 0, WorkingInterval.MinutesPerDay)), holidays);

    /// <summary>
    /// The instant at which <paramref name="duration"/> of working time has accrued after
    /// <paramref name="start"/>, skipping <paramref name="excluded"/> spans (closed pause intervals).
    /// Returns the earliest such instant, so a target that is met exactly at the end of a working
    /// window is due at that window's end. A zero duration is due at <paramref name="start"/>.
    /// </summary>
    public DateTimeOffset Add(DateTimeOffset start, TimeSpan duration, IReadOnlyList<TimeRange>? excluded = null)
    {
        if (duration <= TimeSpan.Zero)
        {
            return start;
        }
        var exclusions = Normalize(excluded);
        if (exclusions.Any(e => e.IsOpen))
        {
            throw new ArgumentException("Add needs closed exclusions; an open pause has no end to count from.", nameof(excluded));
        }

        var remaining = duration;
        foreach (var window in WindowsFrom(start))
        {
            foreach (var piece in Subtract(window, exclusions))
            {
                var length = piece.Duration;
                if (length >= remaining)
                {
                    return piece.Start + remaining;
                }
                remaining -= length;
            }
        }
        throw new InvalidOperationException("The calendar has no working time within five years.");
    }

    /// <summary>Working time between two instants, minus <paramref name="excluded"/> spans (which may be open).</summary>
    public TimeSpan Elapsed(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<TimeRange>? excluded = null)
    {
        if (to <= from)
        {
            return TimeSpan.Zero;
        }
        var exclusions = Normalize(excluded);
        var total = TimeSpan.Zero;
        foreach (var window in WindowsFrom(from))
        {
            if (window.Start >= to)
            {
                break;
            }
            var clipped = new TimeRange(window.Start, window.End < to ? window.End : to);
            foreach (var piece in Subtract(clipped, exclusions))
            {
                total += piece.Duration;
            }
        }
        return total;
    }

    /// <summary>
    /// Working windows in chronological order, never overlapping, starting with the one that contains
    /// (or follows) <paramref name="from"/>, which is clipped to start at <paramref name="from"/>.
    /// Windows on consecutive days may touch; callers treat them as one stretch.
    /// </summary>
    public IEnumerable<TimeRange> WindowsFrom(DateTimeOffset from)
    {
        if (!HasWorkingTime)
        {
            yield break;
        }

        // Start a day early so an overnight interval that began yesterday is included.
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, Zone).DateTime).AddDays(-1);
        var cursor = from;
        var emptyDays = 0;
        while (emptyDays <= HorizonDays)
        {
            var yielded = false;
            foreach (var piece in PiecesOn(date))
            {
                var start = piece.Start < cursor ? cursor : piece.Start;
                if (piece.End <= start)
                {
                    continue;
                }
                yield return new TimeRange(start, piece.End);
                cursor = piece.End;
                yielded = true;
            }
            date = date.AddDays(1);
            emptyDays = yielded ? 0 : emptyDays + 1;
        }
    }

    /// <summary>The working pieces that fall on one local date, in UTC, sorted and merged.</summary>
    private List<TimeRange> PiecesOn(DateOnly date)
    {
        var result = new List<TimeRange>();
        if (_holidays.Contains(date))
        {
            return result;
        }
        var midnight = date.ToDateTime(TimeOnly.MinValue);

        foreach (var interval in _byDay[(int)date.DayOfWeek])
        {
            AddPiece(result, midnight, interval.StartMinute, Math.Min(interval.EndMinute, WorkingInterval.MinutesPerDay));
        }
        // Overnight intervals from the previous weekday spill past midnight onto this date.
        foreach (var interval in _byDay[(int)date.AddDays(-1).DayOfWeek])
        {
            if (interval.EndMinute > WorkingInterval.MinutesPerDay)
            {
                AddPiece(result, midnight, 0, interval.EndMinute - WorkingInterval.MinutesPerDay);
            }
        }

        if (result.Count < 2)
        {
            return result;
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<TimeRange> { result[0] };
        for (var i = 1; i < result.Count; i++)
        {
            var last = merged[^1];
            if (result[i].Start <= last.End)
            {
                merged[^1] = new TimeRange(last.Start, result[i].End > last.End ? result[i].End : last.End);
            }
            else
            {
                merged.Add(result[i]);
            }
        }
        return merged;
    }

    private void AddPiece(List<TimeRange> into, DateTime midnight, int startMinute, int endMinute)
    {
        var start = LocalToUtc(midnight.AddMinutes(startMinute));
        var end = LocalToUtc(midnight.AddMinutes(endMinute));
        if (end > start)
        {
            into.Add(new TimeRange(start, end));
        }
    }

    internal DateTimeOffset LocalToUtc(DateTime local) => ToInstant(local, Zone);

    /// <summary>Converts a local wall-clock time to an instant using the DST rules in the class remarks.</summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local))
        {
            // First occurrence = the larger offset (UTC = local - offset).
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }
        if (zone.IsInvalidTime(local))
        {
            return GapEnd(local, zone);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>
    /// For a local time inside a spring-forward gap, the instant the clocks jumped: every time in the
    /// gap maps there, so an interval inside the gap is empty.
    /// </summary>
    private static DateTimeOffset GapEnd(DateTime local, TimeZoneInfo zone)
    {
        var before = zone.GetUtcOffset(local.AddHours(-12));
        var after = zone.GetUtcOffset(local.AddHours(12));
        // Read with the later offset the time is before the jump; with the earlier offset, after it.
        var lo = new DateTimeOffset(local, after).UtcTicks;
        var hi = new DateTimeOffset(local, before).UtcTicks;
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            if (zone.GetUtcOffset(new DateTimeOffset(mid, TimeSpan.Zero)) == before)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return new DateTimeOffset(hi, TimeSpan.Zero);
    }

    /// <summary>Sorts and merges exclusions so subtraction is a single pass.</summary>
    private static List<TimeRange> Normalize(IReadOnlyList<TimeRange>? excluded)
    {
        if (excluded is null || excluded.Count == 0)
        {
            return [];
        }
        var sorted = excluded.Where(e => e.End > e.Start).OrderBy(e => e.Start).ToList();
        var merged = new List<TimeRange>(sorted.Count);
        foreach (var range in sorted)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = new TimeRange(last.Start, range.End > last.End ? range.End : last.End);
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    private static IEnumerable<TimeRange> Subtract(TimeRange window, List<TimeRange> exclusions)
    {
        var cursor = window.Start;
        foreach (var e in exclusions)
        {
            if (e.End <= cursor)
            {
                continue;
            }
            if (e.Start >= window.End)
            {
                break;
            }
            if (e.Start > cursor)
            {
                yield return new TimeRange(cursor, e.Start);
            }
            cursor = e.End;
            if (cursor >= window.End)
            {
                yield break;
            }
        }
        if (cursor < window.End)
        {
            yield return new TimeRange(cursor, window.End);
        }
    }

    /// <summary>Resolves an IANA or Windows timezone id, or null when unknown.</summary>
    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
