using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Scheduling;
using Switchboard.Testing;

namespace Switchboard.Modules.Automation.Tests;

/// <summary>FR-AUT-10 and SR-AUT-6: recurring tickets, one per occurrence however often the job runs.</summary>
public sealed class SchedulerTests : AutomationTestBase
{
    private Task<AutSchedule> CreateAsync(ScheduleFrequency frequency = ScheduleFrequency.Daily) =>
        Host.RunAsync(SwitchboardTestHost.As(Admin), async sp => await sp.GetRequiredService<ScheduleService>().SaveAsync(null, Host.Workspace.Id, new ScheduleInput(
            "Monthly patching",
            true,
            new TicketTemplate
            {
                Subject = "Patch servers for {{month}}",
                Description = "Run the patch checklist.",
                RequesterId = Requester.Id,
                GroupId = await GroupIdAsync("Network"),
                Priority = Priority.High,
                Tags = ["patching", "Maintenance"],
            },
            frequency,
            0,
            frequency == ScheduleFrequency.Monthly ? 1 : null,
            new TimeOnly(9, 0),
            "America/Chicago",
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            null)));

    [Fact]
    public async Task Each_occurrence_creates_exactly_one_ticket_even_when_run_concurrently()
    {
        var schedule = await CreateAsync();
        Assert.NotNull(schedule.NextRunAt);
        var due = DateTimeOffset.UtcNow.AddMinutes(-5);
        await Db(db => db.Set<AutSchedule>().Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, due)));

        var runner = Host.Services.GetRequiredService<ScheduleRunner>();
        var now = DateTimeOffset.UtcNow;
        await Task.WhenAll(runner.RunOccurrenceAsync(schedule.Id, now, default), runner.RunOccurrenceAsync(schedule.Id, now, default), runner.RunDueAsync(default));
        await runner.RunDueAsync(default);

        var tickets = await Db(db => db.Tickets.AsNoTracking().Include(t => t.Tags).Include(t => t.Source).Where(t => t.Subject.StartsWith("Patch servers")).ToListAsync());
        var ticket = Assert.Single(tickets);
        Assert.Equal(Priority.High, ticket.Priority);
        Assert.Equal(["maintenance", "patching"], ticket.Tags.Select(t => t.Tag).Order());
        Assert.Equal(AutomationSources.Scheduler, ticket.Source!.Key);
        Assert.Matches(@"^Patch servers for [A-Z][a-z]+ \d{4}$", ticket.Subject);

        var occurrence = Assert.Single(await Db(db => db.Set<AutScheduleOccurrence>().AsNoTracking().ToListAsync()));
        Assert.Equal(ticket.Id, occurrence.TicketId);
        var after = await Db(db => db.Set<AutSchedule>().AsNoTracking().FirstAsync(s => s.Id == schedule.Id));
        Assert.True(after.NextRunAt > DateTimeOffset.UtcNow);
        Assert.Equal(ticket.Id, after.LastTicketId);
    }

    [Fact]
    public async Task Missed_occurrences_are_skipped_not_backfilled()
    {
        var schedule = await CreateAsync();
        await Db(db => db.Set<AutSchedule>().Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, DateTimeOffset.UtcNow.AddDays(-5))));
        await Host.Services.GetRequiredService<ScheduleRunner>().RunDueAsync(default);
        Assert.Equal(1, await Db(db => db.Tickets.CountAsync(t => t.Subject.StartsWith("Patch servers"))));
    }

    [Fact]
    public void Recurrence_follows_the_time_zone_and_short_months()
    {
        var weekly = new AutSchedule
        {
            Frequency = ScheduleFrequency.Weekly,
            DaysOfWeek = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Thursday),
            TimeOfDay = new TimeOnly(9, 30),
            TimeZone = "America/New_York",
            StartDate = new DateOnly(2026, 1, 1),
        };
        // Wednesday 4 March 2026 12:00 UTC -> Thursday 5 March 09:30 EST (14:30 UTC).
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.Zero), Recurrence.Next(weekly, new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero)));
        // After the DST change: Monday 9 March 09:30 EDT is 13:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 13, 30, 0, TimeSpan.Zero), Recurrence.Next(weekly, new DateTimeOffset(2026, 3, 5, 15, 0, 0, TimeSpan.Zero)));

        var monthly = new AutSchedule { Frequency = ScheduleFrequency.Monthly, DayOfMonth = 31, TimeOfDay = new TimeOnly(0, 0), TimeZone = "UTC", StartDate = new DateOnly(2026, 1, 1) };
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero), Recurrence.Next(monthly, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));

        var weekdays = new AutSchedule { Frequency = ScheduleFrequency.Weekdays, TimeOfDay = new TimeOnly(8, 0), TimeZone = "UTC", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 3, 9) };
        // Saturday 7 March -> Monday 9 March; after that the schedule has ended.
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 8, 0, 0, TimeSpan.Zero), Recurrence.Next(weekdays, new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero)));
        Assert.Null(Recurrence.Next(weekdays, new DateTimeOffset(2026, 3, 9, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Schedules_validate_their_template_and_need_manage_privilege()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => Host.RunAsync(SwitchboardTestHost.As(Admin), sp =>
            sp.GetRequiredService<ScheduleService>().SaveAsync(null, Host.Workspace.Id, new ScheduleInput("", true, new TicketTemplate(),
                ScheduleFrequency.Weekly, 0, null, new TimeOnly(9, 0), "Mars/Olympus", new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 1)))));
        Assert.Equal(["DaysOfWeek", "EndDate", "Name", "RequesterEmail", "Subject", "TimeZone"], error.Errors.Keys.Order(StringComparer.Ordinal));

        await Assert.ThrowsAsync<ForbiddenException>(() => Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ScheduleService>().ListAsync(Host.Workspace.Id)));
    }
}
