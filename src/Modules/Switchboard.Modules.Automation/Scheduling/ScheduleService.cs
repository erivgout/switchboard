using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;

namespace Switchboard.Modules.Automation.Scheduling;

public sealed record ScheduleInput(
    string Name,
    bool IsEnabled,
    TicketTemplate Template,
    ScheduleFrequency Frequency,
    int DaysOfWeek,
    int? DayOfMonth,
    TimeOnly TimeOfDay,
    string TimeZone,
    DateOnly StartDate,
    DateOnly? EndDate);

/// <summary>Manages recurring ticket schedules (FR-AUT-10).</summary>
public sealed class ScheduleService(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors, IAuditLog audit, TimeProvider time)
{
    public async Task<List<AutSchedule>> ListAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await permissions.DemandAsync(AlertService.ManagePrivilege, workspaceId, ct);
        return await db.Set<AutSchedule>().AsNoTracking().Where(s => s.WorkspaceId == workspaceId).OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<AutSchedule> GetAsync(Guid id, CancellationToken ct = default)
    {
        var schedule = await db.Set<AutSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException();
        await permissions.DemandAsync(AlertService.ManagePrivilege, schedule.WorkspaceId, ct);
        return schedule;
    }

    public async Task<AutSchedule> SaveAsync(Guid? id, Guid workspaceId, ScheduleInput input, CancellationToken ct = default)
    {
        await permissions.DemandAsync(AlertService.ManagePrivilege, workspaceId, ct);
        var errors = new Dictionary<string, string[]>();
        void Error(string field, string message) => errors[field] = [message];

        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            Error("Name", name.Length == 0 ? "Give the schedule a name." : "Keep the name under 200 characters.");
        }
        var t = input.Template;
        t.Subject = (t.Subject ?? "").Trim();
        if (t.Subject.Length is 0 or > 400)
        {
            Error("Subject", t.Subject.Length == 0 ? "Add a subject for the tickets." : "Keep the subject under 400 characters.");
        }
        if (t.Description?.Length > 20_000)
        {
            Error("Description", "The description is too long.");
        }
        if (t.RequesterId is not { } requester || !await db.Users.AnyAsync(u => u.Id == requester && u.IsActive, ct))
        {
            Error("RequesterEmail", "Enter the email of an active person to file the tickets for.");
        }
        if (t.GroupId is { } g && !await db.AgentGroups.AnyAsync(x => x.Id == g && x.WorkspaceId == workspaceId, ct))
        {
            Error("GroupId", "Choose a group in this workspace.");
        }
        if (t.AgentId is { } a && !await db.Users.AnyAsync(u => u.Id == a && u.IsActive && (u.IsOrgAdmin || u.Memberships.Any(m => m.WorkspaceId == workspaceId)), ct))
        {
            Error("AgentId", "Choose an active agent in this workspace.");
        }
        if (t.CategoryId is { } c && !await db.TicketCategories.AnyAsync(x => x.Id == c && x.WorkspaceId == workspaceId, ct))
        {
            Error("CategoryId", "Choose a category in this workspace.");
        }
        t.Tags = Definitions.Describer.SplitTags(string.Join(",", t.Tags ?? []));
        if (!Recurrence.IsValidZone(input.TimeZone))
        {
            Error("TimeZone", "Choose a time zone from the list.");
        }
        if (input.Frequency == ScheduleFrequency.Weekly && (input.DaysOfWeek & 0x7F) == 0)
        {
            Error("DaysOfWeek", "Pick at least one day of the week.");
        }
        if (input.Frequency == ScheduleFrequency.Monthly && input.DayOfMonth is not (>= 1 and <= 31))
        {
            Error("DayOfMonth", "Enter a day of the month from 1 to 31.");
        }
        if (input.EndDate is { } end && end < input.StartDate)
        {
            Error("EndDate", "The end date must be on or after the start date.");
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        AutSchedule schedule;
        if (id is { } existing)
        {
            schedule = await GetAsync(existing, ct);
        }
        else
        {
            schedule = new AutSchedule { WorkspaceId = workspaceId, CreatedById = actors.Current.UserId };
            db.Set<AutSchedule>().Add(schedule);
        }
        schedule.Name = name;
        schedule.IsEnabled = input.IsEnabled;
        schedule.TemplateJson = t.ToJson();
        schedule.Frequency = input.Frequency;
        schedule.DaysOfWeek = input.DaysOfWeek & 0x7F;
        schedule.DayOfMonth = input.Frequency == ScheduleFrequency.Monthly ? input.DayOfMonth : null;
        schedule.TimeOfDay = input.TimeOfDay;
        schedule.TimeZone = input.TimeZone;
        schedule.StartDate = input.StartDate;
        schedule.EndDate = input.EndDate;
        // Never schedule into the past: the next occurrence is after now (or after the last run).
        var after = schedule.LastRunAt is { } last && last > time.GetUtcNow() ? last : time.GetUtcNow();
        schedule.NextRunAt = schedule.IsEnabled ? Recurrence.Next(schedule, after) : null;
        audit.Record(id is null ? "automation.schedule_created" : "automation.schedule_saved", "schedule", schedule.Id.ToString(), new { schedule.Name, schedule.IsEnabled });
        await db.SaveChangesAsync(ct);
        return schedule;
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var schedule = await GetAsync(id, ct);
        schedule.IsEnabled = enabled;
        schedule.NextRunAt = enabled ? Recurrence.Next(schedule, time.GetUtcNow()) : null;
        audit.Record(enabled ? "automation.schedule_enabled" : "automation.schedule_disabled", "schedule", schedule.Id.ToString(), new { schedule.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var schedule = await GetAsync(id, ct);
        db.Set<AutSchedule>().Remove(schedule);
        audit.Record("automation.schedule_deleted", "schedule", schedule.Id.ToString(), new { schedule.Name });
        await db.SaveChangesAsync(ct);
    }
}
