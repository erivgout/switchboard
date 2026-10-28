using Switchboard.Modules.ServiceLevels.Time;

namespace Switchboard.Modules.ServiceLevels.Tests;

/// <summary>INV-TIME-1, INV-SLA-2, INV-SLA-3: business-time arithmetic, including DST, overnight intervals, holidays and 24x7.</summary>
public sealed class BusinessCalendarTests
{
    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static BusinessCalendar NineToFive(string zone = "UTC", params DateOnly[] holidays) =>
        new(Zone(zone), Weekdays.Select(d => new WorkingInterval(d, 9 * 60, 17 * 60)), holidays);

    private static TimeZoneInfo Zone(string id) => BusinessCalendar.FindZone(id) ?? throw new InvalidOperationException($"No zone {id}");

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    // 2026-10-26 is a Monday.
    private static DateTimeOffset Mon(int hour, int minute = 0) => Utc(10, 26, hour, minute);

    [Fact]
    public void Adds_within_a_working_day() =>
        Assert.Equal(Mon(12), NineToFive().Add(Mon(10), TimeSpan.FromHours(2)));

    [Fact]
    public void Carries_past_closing_into_the_next_morning() =>
        Assert.Equal(Utc(10, 27, 10), NineToFive().Add(Mon(16), TimeSpan.FromHours(2)));

    [Fact]
    public void Skips_the_weekend() =>
        Assert.Equal(Utc(11, 2, 10), NineToFive().Add(Utc(10, 30, 16), TimeSpan.FromHours(2)));

    [Fact]
    public void Starts_counting_at_opening_time() =>
        Assert.Equal(Mon(10), NineToFive().Add(Mon(7), TimeSpan.FromHours(1)));

    [Fact]
    public void A_target_met_exactly_at_closing_is_due_at_closing() =>
        Assert.Equal(Mon(17), NineToFive().Add(Mon(9), TimeSpan.FromHours(8)));

    [Fact]
    public void Zero_duration_is_due_immediately() =>
        Assert.Equal(Utc(10, 31, 3), NineToFive().Add(Utc(10, 31, 3), TimeSpan.Zero));

    [Fact]
    public void Measures_elapsed_business_time()
    {
        var calendar = NineToFive();
        Assert.Equal(TimeSpan.FromHours(2), calendar.Elapsed(Mon(16), Utc(10, 27, 10)));
        Assert.Equal(TimeSpan.FromHours(2), calendar.Elapsed(Utc(10, 30, 16), Utc(11, 2, 10)));
        Assert.Equal(TimeSpan.Zero, calendar.Elapsed(Utc(10, 31, 0), Utc(11, 1, 23)));
        Assert.Equal(TimeSpan.Zero, calendar.Elapsed(Mon(12), Mon(11)));
    }

    [Fact]
    public void Holidays_are_skipped()
    {
        var calendar = NineToFive("UTC", new DateOnly(2026, 10, 28));
        Assert.Equal(Utc(10, 29, 10), calendar.Add(Utc(10, 27, 16), TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromHours(2), calendar.Elapsed(Utc(10, 27, 16), Utc(10, 29, 10)));
    }

    [Fact]
    public void Overnight_intervals_run_past_midnight()
    {
        // Monday 22:00 to Tuesday 06:00.
        var calendar = new BusinessCalendar(TimeZoneInfo.Utc, [new WorkingInterval(DayOfWeek.Monday, 22 * 60, 30 * 60)]);
        Assert.Equal(Utc(10, 27, 2), calendar.Add(Mon(23), TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.FromHours(8), calendar.Elapsed(Mon(0), Utc(10, 27, 12)));
        // Tuesday 05:00 is inside the shift that began on Monday.
        Assert.Equal(Utc(10, 27, 6), calendar.Add(Utc(10, 27, 5), TimeSpan.FromHours(1)));
        // The next shift starts the following Monday.
        Assert.Equal(Utc(11, 2, 23), calendar.Add(Utc(10, 27, 5), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_holiday_removes_the_part_of_an_overnight_shift_that_falls_on_it()
    {
        var calendar = new BusinessCalendar(TimeZoneInfo.Utc,
            [new WorkingInterval(DayOfWeek.Monday, 22 * 60, 30 * 60), new WorkingInterval(DayOfWeek.Tuesday, 22 * 60, 30 * 60)],
            [new DateOnly(2026, 10, 27)]);
        // Monday 23:00-24:00 counts; Tuesday (the holiday) doesn't, including Tuesday's own shift start;
        // Tuesday's shift spills onto Wednesday, which is a working date.
        Assert.Equal(Utc(10, 28, 1), calendar.Add(Mon(23), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Twenty_four_seven_counts_every_hour()
    {
        var calendar = BusinessCalendar.TwentyFourSeven(TimeZoneInfo.Utc);
        Assert.Equal(Utc(11, 1, 16), calendar.Add(Utc(10, 31, 10), TimeSpan.FromHours(30)));
        Assert.Equal(TimeSpan.FromDays(3), calendar.Elapsed(Mon(0), Utc(10, 29, 0)));
    }

    [Fact]
    public void Twenty_four_seven_still_honours_holidays()
    {
        var calendar = BusinessCalendar.TwentyFourSeven(TimeZoneInfo.Utc, [new DateOnly(2026, 10, 27)]);
        Assert.Equal(Utc(10, 28, 1), calendar.Add(Mon(23), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Spring_forward_shortens_an_interval_that_spans_the_gap()
    {
        // New York, 2026-03-08: 02:00 EST jumps to 03:00 EDT.
        var calendar = new BusinessCalendar(Zone("America/New_York"), [new WorkingInterval(DayOfWeek.Sunday, 0, 4 * 60)]);
        var start = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5));
        var end = new DateTimeOffset(2026, 3, 8, 4, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(TimeSpan.FromHours(3), calendar.Elapsed(start, end));
        Assert.Equal(end, calendar.Add(start, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void An_interval_inside_the_spring_forward_gap_is_empty()
    {
        var calendar = new BusinessCalendar(Zone("America/New_York"),
            [new WorkingInterval(DayOfWeek.Sunday, 2 * 60, 2 * 60 + 30), new WorkingInterval(DayOfWeek.Sunday, 10 * 60, 11 * 60)]);
        var start = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 10, 30, 0, TimeSpan.FromHours(-4)), calendar.Add(start, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Fall_back_lengthens_an_interval_that_spans_the_repeated_hour()
    {
        // New York, 2026-11-01: 02:00 EDT falls back to 01:00 EST.
        var calendar = new BusinessCalendar(Zone("America/New_York"), [new WorkingInterval(DayOfWeek.Sunday, 0, 4 * 60)]);
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));
        var end = new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(TimeSpan.FromHours(5), calendar.Elapsed(start, end));
        Assert.Equal(end, calendar.Add(start, TimeSpan.FromHours(5)));
    }

    [Fact]
    public void An_ambiguous_time_means_its_first_occurrence()
    {
        var calendar = new BusinessCalendar(Zone("America/New_York"), [new WorkingInterval(DayOfWeek.Sunday, 60, 90)]);
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));
        // 01:00-01:30 EDT, the first time the clock shows those minutes.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), calendar.Add(start, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Working_hours_follow_local_time_across_dst_changes()
    {
        var calendar = NineToFive("America/New_York");
        // Friday 16:00 EDT + 2h: one hour Friday, one hour Monday 9-10 EST, after the fall-back weekend.
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(-5)),
            calendar.Add(new DateTimeOffset(2026, 10, 30, 16, 0, 0, TimeSpan.FromHours(-4)), TimeSpan.FromHours(2)));
        // Friday 16:00 EST + 2h, across spring forward.
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.FromHours(-4)),
            calendar.Add(new DateTimeOffset(2026, 3, 6, 16, 0, 0, TimeSpan.FromHours(-5)), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Accepts_windows_and_iana_timezone_ids()
    {
        Assert.NotNull(BusinessCalendar.FindZone("Europe/London"));
        Assert.NotNull(BusinessCalendar.FindZone("GMT Standard Time"));
        Assert.Null(BusinessCalendar.FindZone("Mars/Olympus_Mons"));
        Assert.Null(BusinessCalendar.FindZone(""));
    }

    [Fact]
    public void Pauses_are_excluded_from_business_time()
    {
        var calendar = NineToFive();
        TimeRange[] paused = [new(Mon(10, 30), Mon(11))];
        Assert.Equal(Mon(12, 30), calendar.Add(Mon(10), TimeSpan.FromHours(2), paused));
        // A pause outside working hours changes nothing.
        Assert.Equal(Mon(12), calendar.Add(Mon(10), TimeSpan.FromHours(2), [new TimeRange(Mon(18), Mon(20))]));
        Assert.Equal(TimeSpan.FromMinutes(90), calendar.Elapsed(Mon(10), Mon(12), paused));
    }

    [Fact]
    public void Open_pauses_stop_accrual_but_cannot_produce_a_due_date()
    {
        var calendar = NineToFive();
        TimeRange[] open = [new(Mon(11), DateTimeOffset.MaxValue)];
        Assert.Equal(TimeSpan.FromHours(1), calendar.Elapsed(Mon(10), Mon(15), open));
        Assert.Throws<ArgumentException>(() => calendar.Add(Mon(10), TimeSpan.FromHours(2), open));
    }

    [Fact]
    public void A_calendar_without_working_time_cannot_produce_a_due_date()
    {
        var calendar = new BusinessCalendar(TimeZoneInfo.Utc, []);
        Assert.Throws<InvalidOperationException>(() => calendar.Add(Mon(10), TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.Zero, calendar.Elapsed(Mon(10), Mon(12)));
    }

    // ------------------------------------------------------------------ properties

    private static readonly string[] ZoneIds =
    [
        "UTC", "America/New_York", "Europe/London", "Australia/Lord_Howe", "Asia/Kolkata", "America/Santiago", "Pacific/Auckland", "America/St_Johns",
    ];

    private sealed record Sample(BusinessCalendar Calendar, DateTimeOffset Start, TimeSpan Duration, TimeRange[] Pauses);

    private static IEnumerable<Sample> Samples(int seed, int count)
    {
        var random = new Random(seed);
        var zones = ZoneIds.Select(BusinessCalendar.FindZone).Where(z => z is not null).Select(z => z!).ToList();
        Assert.True(zones.Count >= 6, "Expected the test timezones to resolve.");
        // Starts cluster around the 2026 DST changes in both hemispheres.
        DateTimeOffset[] anchors = [Utc(3, 7, 0), Utc(3, 28, 0), Utc(4, 4, 0), Utc(9, 26, 0), Utc(10, 24, 0), Utc(10, 31, 0), Utc(6, 1, 0)];
        for (var i = 0; i < count; i++)
        {
            var intervals = new List<WorkingInterval>();
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                var n = random.Next(0, 3);
                for (var k = 0; k < n; k++)
                {
                    var startMinute = random.Next(0, 96) * 15;
                    var length = random.Next(1, 97) * 15;
                    intervals.Add(new WorkingInterval(day, startMinute, startMinute + length));
                }
            }
            if (intervals.Count == 0)
            {
                intervals.Add(new WorkingInterval(DayOfWeek.Wednesday, 9 * 60, 17 * 60));
            }
            var start = anchors[random.Next(anchors.Length)].AddMinutes(random.Next(0, 14 * 1440));
            var holidays = Enumerable.Range(0, random.Next(0, 4))
                .Select(_ => DateOnly.FromDateTime(start.UtcDateTime).AddDays(random.Next(-2, 20)))
                .ToArray();
            var pauses = Enumerable.Range(0, random.Next(0, 3))
                .Select(_ =>
                {
                    var from = start.AddMinutes(random.Next(-600, 6000));
                    return new TimeRange(from, from.AddMinutes(random.Next(1, 3000)));
                })
                .ToArray();
            yield return new Sample(
                new BusinessCalendar(zones[random.Next(zones.Count)], intervals, holidays),
                start,
                TimeSpan.FromMinutes(random.Next(0, 72 * 60)),
                pauses);
        }
    }

    [Fact]
    public void Property_elapsed_time_to_the_due_date_equals_the_target()
    {
        foreach (var s in Samples(1, 400))
        {
            var due = s.Calendar.Add(s.Start, s.Duration, s.Pauses);
            Assert.Equal(s.Duration, s.Calendar.Elapsed(s.Start, due, s.Pauses));
            Assert.True(due >= s.Start);
        }
    }

    [Fact]
    public void Property_the_due_date_is_the_earliest_instant_the_target_is_reached()
    {
        foreach (var s in Samples(2, 300).Where(s => s.Duration > TimeSpan.Zero))
        {
            var due = s.Calendar.Add(s.Start, s.Duration, s.Pauses);
            Assert.True(s.Calendar.Elapsed(s.Start, due.AddMinutes(-1), s.Pauses) < s.Duration);
        }
    }

    [Fact]
    public void Property_adding_in_two_steps_equals_adding_once()
    {
        var random = new Random(3);
        foreach (var s in Samples(3, 300))
        {
            var first = TimeSpan.FromMinutes(random.Next(0, (int)s.Duration.TotalMinutes + 1));
            var rest = s.Duration - first;
            var stepwise = s.Calendar.Add(s.Calendar.Add(s.Start, first, s.Pauses), rest, s.Pauses);
            Assert.Equal(s.Calendar.Add(s.Start, s.Duration, s.Pauses), stepwise);
        }
    }

    [Fact]
    public void Property_due_dates_never_move_earlier_for_longer_targets_or_more_pauses()
    {
        foreach (var s in Samples(4, 300))
        {
            var due = s.Calendar.Add(s.Start, s.Duration, s.Pauses);
            Assert.True(s.Calendar.Add(s.Start, s.Duration + TimeSpan.FromMinutes(15), s.Pauses) >= due);
            Assert.True(due >= s.Calendar.Add(s.Start, s.Duration));
        }
    }

    [Fact]
    public void Property_elapsed_time_is_additive_and_bounded_by_wall_time()
    {
        var random = new Random(5);
        foreach (var s in Samples(5, 300))
        {
            var a = s.Start;
            var b = a.AddMinutes(random.Next(0, 5000));
            var c = b.AddMinutes(random.Next(0, 5000));
            var ab = s.Calendar.Elapsed(a, b, s.Pauses);
            var bc = s.Calendar.Elapsed(b, c, s.Pauses);
            Assert.Equal(s.Calendar.Elapsed(a, c, s.Pauses), ab + bc);
            Assert.True(ab >= TimeSpan.Zero && ab <= b - a);
        }
    }

    [Fact]
    public void Property_recomputing_is_idempotent()
    {
        foreach (var s in Samples(6, 200))
        {
            Assert.Equal(s.Calendar.Add(s.Start, s.Duration, s.Pauses), s.Calendar.Add(s.Start, s.Duration, s.Pauses.Reverse().ToArray()));
        }
    }
}
