using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;

namespace Switchboard.Modules.Automation.Scheduling;

/// <summary>The ticket a schedule creates. <c>{{date}}</c> and <c>{{month}}</c> in the subject become the occurrence date.</summary>
public sealed class TicketTemplate
{
    public string Subject { get; set; } = "";
    public string? Description { get; set; }
    public TicketType Type { get; set; } = TicketType.ServiceRequest;
    public Guid? RequesterId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? AgentId { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    public Guid? CategoryId { get; set; }
    public List<string> Tags { get; set; } = [];

    public static TicketTemplate Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new TicketTemplate() : JsonSerializer.Deserialize<TicketTemplate>(json, WorkflowDefinition.Json) ?? new TicketTemplate();

    public string ToJson() => JsonSerializer.Serialize(this, WorkflowDefinition.Json);
}

/// <summary>Occurrence arithmetic in the schedule's time zone, including daylight-saving changes.</summary>
public static class Recurrence
{
    public static TimeZoneInfo Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsValidZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The first occurrence strictly after <paramref name="after"/>, or null when the schedule has ended.</summary>
    public static DateTimeOffset? Next(AutSchedule s, DateTimeOffset after)
    {
        var zone = Zone(s.TimeZone);
        var localAfter = TimeZoneInfo.ConvertTime(after, zone);
        var day = DateOnly.FromDateTime(localAfter.DateTime);
        if (day < s.StartDate)
        {
            day = s.StartDate;
        }
        for (var i = 0; i < 800; i++, day = day.AddDays(1))
        {
            if (s.EndDate is { } end && day > end)
            {
                return null;
            }
            if (!Matches(s, day))
            {
                continue;
            }
            var local = day.ToDateTime(s.TimeOfDay);
            while (zone.IsInvalidTime(local))
            {
                // Spring forward: 02:30 doesn't exist, so use the first valid minute after it.
                local = local.AddMinutes(30);
            }
            var occurrence = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
            if (occurrence > after)
            {
                return occurrence;
            }
        }
        return null;
    }

    public static bool Matches(AutSchedule s, DateOnly day) => s.Frequency switch
    {
        ScheduleFrequency.Daily => true,
        ScheduleFrequency.Weekdays => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        ScheduleFrequency.Weekly => (s.DaysOfWeek & (1 << (int)day.DayOfWeek)) != 0,
        ScheduleFrequency.Monthly => day.Day == Math.Min(s.DayOfMonth ?? 1, DateTime.DaysInMonth(day.Year, day.Month)),
        _ => false,
    };

    public static string Describe(AutSchedule s)
    {
        var time = s.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
        var when = s.Frequency switch
        {
            ScheduleFrequency.Daily => "Every day",
            ScheduleFrequency.Weekdays => "Every weekday",
            ScheduleFrequency.Weekly => "Every " + JoinDays(s.DaysOfWeek),
            ScheduleFrequency.Monthly => $"On day {s.DayOfMonth ?? 1} of every month",
            _ => "Never",
        };
        return $"{when} at {time} ({s.TimeZone})";
    }

    private static string JoinDays(int mask)
    {
        var days = Enumerable.Range(0, 7).Where(d => (mask & (1 << d)) != 0)
            .Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)d)).ToList();
        return days.Count == 0 ? "week (no days chosen)" : string.Join(", ", days);
    }
}

/// <summary>
/// Creates tickets for due schedule occurrences. An occurrence row with a unique (schedule, time) key
/// commits in the same transaction as the ticket, so each occurrence creates exactly one ticket.
/// </summary>
public sealed class ScheduleRunner(IServiceScopeFactory scopes, TimeProvider time, ILogger<ScheduleRunner> logger)
{
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>().Set<AutSchedule>().AsNoTracking()
                .Where(s => s.IsEnabled && s.NextRunAt != null && s.NextRunAt <= now)
                .OrderBy(s => s.NextRunAt).Select(s => s.Id).Take(50).ToListAsync(ct);
        }
        var created = 0;
        foreach (var id in due)
        {
            if (await RunOccurrenceAsync(id, now, ct))
            {
                created++;
            }
        }
        return created;
    }

    /// <summary>Runs the latest due occurrence of one schedule. Missed older occurrences are skipped, not backfilled.</summary>
    public async Task<bool> RunOccurrenceAsync(Guid scheduleId, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        var schedule = await db.Set<AutSchedule>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        if (schedule is not { IsEnabled: true, NextRunAt: { } first } || first > now)
        {
            return false;
        }

        var occurrence = first;
        while (Recurrence.Next(schedule, occurrence) is { } later && later <= now)
        {
            occurrence = later;
        }
        var next = Recurrence.Next(schedule, occurrence);
        var paused = await sp.GetRequiredService<AutomationSettings>().IsPausedAsync(schedule.WorkspaceId, ct);

        using var actor = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.Automation($"Schedule: {schedule.Name}", 0));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = new AutScheduleOccurrence { ScheduleId = schedule.Id, OccurrenceAt = occurrence, CreatedAt = now };
        db.Set<AutScheduleOccurrence>().Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Sql.IsUniqueViolation(ex))
        {
            // Another worker created this occurrence; just move the schedule on.
            await tx.RollbackAsync(CancellationToken.None);
            await AdvanceAsync(scheduleId, first, next, null, null, ct);
            return false;
        }

        if (paused)
        {
            await tx.RollbackAsync(CancellationToken.None);
            await AdvanceAsync(scheduleId, first, next, null, "Skipped while automations were paused.", ct);
            return false;
        }

        string? error = null;
        Guid? ticketId = null;
        try
        {
            var template = TicketTemplate.Parse(schedule.TemplateJson);
            var localDate = TimeZoneInfo.ConvertTime(occurrence, Recurrence.Zone(schedule.TimeZone));
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["date"] = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["month"] = localDate.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            };
            var category = template.CategoryId is { } c ? await CategoryChainAsync(db, c, ct) : [null, null, null];
            var ticket = await sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                WorkspaceId = schedule.WorkspaceId,
                Type = template.Type,
                Subject = PlaceholderRenderer.Render(template.Subject, values, PlaceholderEncoding.SingleLine),
                DescriptionHtml = sp.GetRequiredService<Core.Services.IHtmlContent>().FromPlainText(PlaceholderRenderer.Render(template.Description, values, PlaceholderEncoding.Text)),
                RequesterId = template.RequesterId,
                GroupId = template.GroupId,
                AgentId = template.AgentId,
                Priority = template.Priority,
                CategoryId = category[0],
                SubCategoryId = category[1],
                ItemCategoryId = category[2],
                Tags = template.Tags,
                SourceKey = AutomationSources.Scheduler,
            }, ct);
            ticketId = ticket.Id;
            row.TicketId = ticket.Id;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException or NotFoundException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            error = $"Couldn't create the ticket: {ex.Message}";
            logger.LogWarning("Schedule {Schedule} failed: {Error}", schedule.Name, error);
        }

        await AdvanceAsync(scheduleId, first, next, ticketId, error, ct);
        return ticketId is not null;
    }

    private async Task AdvanceAsync(Guid scheduleId, DateTimeOffset expected, DateTimeOffset? next, Guid? ticketId, string? error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        var now = time.GetUtcNow();
        // Conditional on the old value, so two workers never move it twice.
        await db.Set<AutSchedule>().Where(s => s.Id == scheduleId && s.NextRunAt == expected)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, next), ct);
        // The worker that created (or failed to create) the ticket always records the outcome.
        if (ticketId is not null || error is not null)
        {
            await db.Set<AutSchedule>().Where(s => s.Id == scheduleId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastRunAt, now)
                .SetProperty(x => x.LastTicketId, x => ticketId ?? x.LastTicketId)
                .SetProperty(x => x.LastError, error), ct);
        }
    }

    private static async Task<Guid?[]> CategoryChainAsync(SwitchboardDbContext db, Guid id, CancellationToken ct)
    {
        var chain = new List<Guid>();
        Guid? current = id;
        while (current is { } c && chain.Count < 3)
        {
            var row = await db.TicketCategories.AsNoTracking().Where(x => x.Id == c).Select(x => new { x.Id, x.ParentId }).FirstOrDefaultAsync(ct);
            if (row is null)
            {
                return [null, null, null];
            }
            chain.Insert(0, row.Id);
            current = row.ParentId;
        }
        return [chain.ElementAtOrDefault(0), chain.Count > 1 ? chain[1] : null, chain.Count > 2 ? chain[2] : null];
    }
}

public static class AutomationSources
{
    /// <summary>Ticket source seeded by this module for tickets the scheduler creates.</summary>
    public const string Scheduler = "scheduler";
}
