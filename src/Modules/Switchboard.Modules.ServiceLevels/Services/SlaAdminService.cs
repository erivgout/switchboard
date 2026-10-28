using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Engine;
using Switchboard.Modules.ServiceLevels.Time;

namespace Switchboard.Modules.ServiceLevels.Services;

public sealed record CalendarHoursInput(DayOfWeek Day, int StartMinute, int EndMinute);

public sealed record CalendarInput
{
    public Guid? Id { get; init; }
    public Guid WorkspaceId { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string TimeZoneId { get; init; } = "UTC";
    public bool Is24x7 { get; init; }
    public bool IsDefault { get; init; }
    public IReadOnlyList<CalendarHoursInput> Hours { get; init; } = [];
    /// <summary>Agent groups that use this calendar.</summary>
    public IReadOnlyList<Guid> GroupIds { get; init; } = [];
}

public sealed record PolicyInput
{
    public Guid? Id { get; init; }
    public Guid WorkspaceId { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
    public Guid? CalendarId { get; init; }
    public IReadOnlyList<SlaCondition> Conditions { get; init; } = [];
    public IReadOnlyList<SlaTarget> Targets { get; init; } = [];
    public IReadOnlyList<SlaReminderRule> Reminders { get; init; } = [];
    public SlaEscalationPlan Escalations { get; init; } = new();
}

public sealed record HolidayImportResult(int Added, int Updated, IReadOnlyList<string> Errors);

/// <summary>Configuration of calendars and SLA policies. Every method demands <c>sla.manage</c> in the workspace.</summary>
public sealed class SlaAdminService(SwitchboardDbContext db, IPermissionService permissions, ISettingsService settings, IAuditLog audit, TimeProvider time)
{
    public const string Privilege = "sla.manage";
    private const int MaxTargetMinutes = 365 * 1440;
    private const int MaxHolidaysPerImport = 500;

    // ------------------------------------------------------------------ calendars

    public async Task<List<SlaCalendar>> CalendarsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privilege, workspaceId, ct);
        return await db.Set<SlaCalendar>().AsNoTracking()
            .Include(c => c.Hours)
            .Include(c => c.Holidays)
            .Where(c => c.WorkspaceId == workspaceId && c.DeletedAt == null)
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<SlaCalendar> CalendarAsync(Guid id, CancellationToken ct = default)
    {
        var calendar = await db.Set<SlaCalendar>().AsNoTracking()
            .Include(c => c.Hours)
            .Include(c => c.Holidays.OrderBy(h => h.Date))
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct) ?? throw new NotFoundException();
        await DemandOrHideAsync(calendar.WorkspaceId, ct);
        return calendar;
    }

    public async Task<SlaCalendar> SaveCalendarAsync(CalendarInput input, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privilege, input.WorkspaceId, ct);
        var errors = new Dictionary<string, string[]>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            errors["name"] = [name.Length == 0 ? "Give the calendar a name." : "Keep the name under 200 characters."];
        }
        var zone = BusinessCalendar.FindZone(input.TimeZoneId);
        if (zone is null)
        {
            errors["timeZoneId"] = ["Choose a timezone from the list."];
        }
        var hours = input.Is24x7 ? [] : input.Hours.ToList();
        var bad = hours.FirstOrDefault(h => !new WorkingInterval(h.Day, h.StartMinute, h.EndMinute).IsValid);
        if (bad is not null)
        {
            errors["hours"] = [$"The {bad.Day} hours don't make sense. Use a start and an end time that differ."];
        }
        else if (!input.Is24x7 && hours.Count == 0)
        {
            errors["hours"] = ["Add working hours for at least one day, or choose 24 hours, 7 days."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var groups = input.GroupIds.Distinct().ToList();
        var groupRows = await db.AgentGroups.Where(g => g.WorkspaceId == input.WorkspaceId).ToListAsync(ct);
        if (groups.Any(g => groupRows.All(r => r.Id != g)))
        {
            throw new ValidationException("groupIds", "One of those groups isn't in this workspace.");
        }

        SlaCalendar calendar;
        var created = input.Id is null;
        if (created)
        {
            calendar = new SlaCalendar { WorkspaceId = input.WorkspaceId };
            db.Set<SlaCalendar>().Add(calendar);
        }
        else
        {
            calendar = await db.Set<SlaCalendar>().Include(c => c.Hours)
                .FirstOrDefaultAsync(c => c.Id == input.Id && c.WorkspaceId == input.WorkspaceId && c.DeletedAt == null, ct)
                ?? throw new NotFoundException();
        }

        calendar.Name = name;
        calendar.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        calendar.TimeZoneId = input.TimeZoneId.Trim();
        calendar.Is24x7 = input.Is24x7;
        db.Set<SlaWorkingHours>().RemoveRange(calendar.Hours);
        // Rows get their key on construction, so add them explicitly rather than through the collection.
        db.Set<SlaWorkingHours>().AddRange(hours
            .OrderBy(h => ((int)h.Day + 6) % 7).ThenBy(h => h.StartMinute)
            .Select(h => new SlaWorkingHours { CalendarId = calendar.Id, Day = h.Day, StartMinute = h.StartMinute, EndMinute = h.EndMinute }));

        var others = await db.Set<SlaCalendar>().Where(c => c.WorkspaceId == input.WorkspaceId && c.Id != calendar.Id && c.DeletedAt == null).ToListAsync(ct);
        if (input.IsDefault || others.All(o => !o.IsDefault))
        {
            // Exactly one default per workspace.
            calendar.IsDefault = true;
            others.ForEach(o => o.IsDefault = false);
        }
        else
        {
            calendar.IsDefault = false;
        }

        foreach (var group in groupRows)
        {
            if (groups.Contains(group.Id))
            {
                group.BusinessCalendarId = calendar.Id;
            }
            else if (group.BusinessCalendarId == calendar.Id)
            {
                group.BusinessCalendarId = null;
            }
        }

        audit.Record(created ? "sla.calendar.created" : "sla.calendar.updated", "sla_calendar", calendar.Id.ToString(),
            new { calendar.Name, calendar.TimeZoneId, calendar.Is24x7, calendar.IsDefault, hours = hours.Count, groups });
        await db.SaveChangesAsync(ct);
        return calendar;
    }

    /// <summary>Policies and groups that used the calendar move to <paramref name="replacementId"/> (SR-DEL-2).</summary>
    public async Task DeleteCalendarAsync(Guid id, Guid replacementId, CancellationToken ct = default)
    {
        var calendar = await db.Set<SlaCalendar>().FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct) ?? throw new NotFoundException();
        await DemandOrHideAsync(calendar.WorkspaceId, ct);
        var replacement = await db.Set<SlaCalendar>()
            .FirstOrDefaultAsync(c => c.Id == replacementId && c.Id != id && c.WorkspaceId == calendar.WorkspaceId && c.DeletedAt == null, ct)
            ?? throw new ValidationException("replacementId", "Choose another calendar to use instead.");

        var policies = await db.Set<SlaPolicy>().Where(p => p.CalendarId == id).ToListAsync(ct);
        policies.ForEach(p => p.CalendarId = replacement.Id);
        var groups = await db.AgentGroups.Where(g => g.BusinessCalendarId == id).ToListAsync(ct);
        groups.ForEach(g => g.BusinessCalendarId = replacement.Id);
        if (calendar.IsDefault)
        {
            replacement.IsDefault = true;
            calendar.IsDefault = false;
        }
        calendar.DeletedAt = time.GetUtcNow();
        audit.Record("sla.calendar.deleted", "sla_calendar", id.ToString(),
            new { calendar.Name, replacement = replacement.Id, policies = policies.Count, groups = groups.Count });
        await db.SaveChangesAsync(ct);
    }

    public async Task<HolidayImportResult> ImportHolidaysAsync(Guid calendarId, string? csv, CancellationToken ct = default)
    {
        var calendar = await db.Set<SlaCalendar>().Include(c => c.Holidays)
            .FirstOrDefaultAsync(c => c.Id == calendarId && c.DeletedAt == null, ct) ?? throw new NotFoundException();
        await DemandOrHideAsync(calendar.WorkspaceId, ct);

        var (rows, errors) = HolidayCsv.Parse(csv, MaxHolidaysPerImport);
        if (rows.Count == 0 && errors.Count == 0)
        {
            throw new ValidationException("holidays", "Paste at least one line, like 2026-12-25, Christmas Day.");
        }
        int added = 0, updated = 0;
        foreach (var (date, name) in rows)
        {
            var existing = calendar.Holidays.FirstOrDefault(h => h.Date == date);
            if (existing is null)
            {
                db.Set<SlaHoliday>().Add(new SlaHoliday { CalendarId = calendar.Id, Date = date, Name = name });
                added++;
            }
            else if (existing.Name != name)
            {
                existing.Name = name;
                updated++;
            }
        }
        if (added + updated > 0)
        {
            audit.Record("sla.holidays.imported", "sla_calendar", calendar.Id.ToString(), new { added, updated, errors = errors.Count });
            await db.SaveChangesAsync(ct);
        }
        return new HolidayImportResult(added, updated, errors);
    }

    public async Task RemoveHolidayAsync(Guid calendarId, Guid holidayId, CancellationToken ct = default)
    {
        var calendar = await db.Set<SlaCalendar>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == calendarId && c.DeletedAt == null, ct) ?? throw new NotFoundException();
        await DemandOrHideAsync(calendar.WorkspaceId, ct);
        var holiday = await db.Set<SlaHoliday>().FirstOrDefaultAsync(h => h.Id == holidayId && h.CalendarId == calendarId, ct) ?? throw new NotFoundException();
        db.Set<SlaHoliday>().Remove(holiday);
        audit.Record("sla.holiday.removed", "sla_calendar", calendarId.ToString(), new { holiday.Date, holiday.Name });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ policies

    public async Task<List<SlaPolicy>> PoliciesAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privilege, workspaceId, ct);
        return await db.Set<SlaPolicy>().AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.DeletedAt == null)
            .OrderBy(p => p.Order).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<SlaPolicy> PolicyAsync(Guid id, CancellationToken ct = default)
    {
        var policy = await db.Set<SlaPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct) ?? throw new NotFoundException();
        await DemandOrHideAsync(policy.WorkspaceId, ct);
        return policy;
    }

    public async Task<SlaPolicy> SavePolicyAsync(PolicyInput input, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privilege, input.WorkspaceId, ct);
        var errors = await ValidateAsync(input, ct);
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        SlaPolicy policy;
        var created = input.Id is null;
        if (created)
        {
            var maxOrder = await db.Set<SlaPolicy>().Where(p => p.WorkspaceId == input.WorkspaceId && p.DeletedAt == null).MaxAsync(p => (int?)p.Order, ct) ?? 0;
            policy = new SlaPolicy { WorkspaceId = input.WorkspaceId, Order = maxOrder + 1 };
            db.Set<SlaPolicy>().Add(policy);
        }
        else
        {
            policy = await db.Set<SlaPolicy>().FirstOrDefaultAsync(p => p.Id == input.Id && p.WorkspaceId == input.WorkspaceId && p.DeletedAt == null, ct)
                ?? throw new NotFoundException();
        }

        policy.Name = input.Name.Trim();
        policy.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        policy.IsActive = input.IsActive;
        policy.CalendarId = input.CalendarId;
        policy.Conditions = input.Conditions
            .Select(c => c with { Values = c.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList() })
            .Where(c => c.Values.Count > 0)
            .ToList();
        policy.Targets = input.Targets
            .Where(t => t.FirstResponseMinutes is not null || t.NextResponseMinutes is not null || t.ResolutionMinutes is not null)
            .OrderByDescending(t => t.Priority)
            .ToList();
        policy.Reminders = input.Reminders.Where(r => r.MinutesBefore > 0).ToList();
        policy.Escalations = new SlaEscalationPlan
        {
            FirstResponse = input.Escalations.FirstResponse,
            Resolution = input.Escalations.Resolution.OrderBy(l => l.Level).ToList(),
        };

        audit.Record(created ? "sla.policy.created" : "sla.policy.updated", "sla_policy", policy.Id.ToString(),
            new { policy.Name, policy.IsActive, policy.CalendarId, conditions = policy.Conditions, targets = policy.Targets });
        await db.SaveChangesAsync(ct);
        await ResetMonitorAsync(policy.Id, ct);
        return policy;
    }

    /// <summary>Sets the evaluation order. <paramref name="orderedIds"/> must list every policy in the workspace.</summary>
    public async Task ReorderAsync(Guid workspaceId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privilege, workspaceId, ct);
        var policies = await db.Set<SlaPolicy>().Where(p => p.WorkspaceId == workspaceId && p.DeletedAt == null).ToListAsync(ct);
        if (orderedIds.Count != policies.Count || orderedIds.Distinct().Count() != policies.Count || policies.Any(p => !orderedIds.Contains(p.Id)))
        {
            throw new ValidationException("order", "The list changed while you were editing it. Reload and try again.");
        }
        for (var i = 0; i < orderedIds.Count; i++)
        {
            policies.First(p => p.Id == orderedIds[i]).Order = i + 1;
        }
        audit.Record("sla.policy.reordered", "workspace", workspaceId.ToString(), new { order = orderedIds });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var policy = await db.Set<SlaPolicy>().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct) ?? throw new NotFoundException();
        await DemandOrHideAsync(policy.WorkspaceId, ct);
        if (policy.IsActive == active)
        {
            return;
        }
        policy.IsActive = active;
        audit.Record(active ? "sla.policy.activated" : "sla.policy.deactivated", "sla_policy", id.ToString(), new { policy.Name });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes the policy from matching. The row stays (soft delete), so tickets it applied to keep
    /// their targets and a valid reference (INV-REF-1).
    /// </summary>
    public async Task DeletePolicyAsync(Guid id, CancellationToken ct = default)
    {
        var policy = await db.Set<SlaPolicy>().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct) ?? throw new NotFoundException();
        await DemandOrHideAsync(policy.WorkspaceId, ct);
        policy.DeletedAt = time.GetUtcNow();
        policy.IsActive = false;
        audit.Record("sla.policy.deleted", "sla_policy", id.ToString(), new { policy.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ReevaluateOnChangeAsync(Guid workspaceId, CancellationToken ct = default) =>
        await settings.GetAsync(SlaSettingKeys.ReevaluateOnChange, true, workspaceId, ct);

    public async Task SetReevaluateOnChangeAsync(Guid workspaceId, bool value, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privilege, workspaceId, ct);
        await settings.SetAsync(SlaSettingKeys.ReevaluateOnChange, value, workspaceId, ct);
        audit.Record("sla.settings.updated", "workspace", workspaceId.ToString(), new { reevaluateOnChange = value });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Existence must not leak to people without the privilege (SR-AUTHZ-6).</summary>
    private async Task DemandOrHideAsync(Guid workspaceId, CancellationToken ct)
    {
        if (!await permissions.HasAsync(Privilege, workspaceId, ct))
        {
            throw new NotFoundException();
        }
    }

    /// <summary>Policy edits change reminder and escalation plans; let the monitor look at those tickets again.</summary>
    private async Task ResetMonitorAsync(Guid policyId, CancellationToken ct) =>
        await db.Set<SlaMonitorCursor>().Where(c => c.PolicyId == policyId).ExecuteDeleteAsync(ct);

    private async Task<Dictionary<string, string[]>> ValidateAsync(PolicyInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string key, string message) => errors[key] = errors.TryGetValue(key, out var e) ? [.. e, message] : [message];

        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
        {
            Add("name", "Give the policy a name.");
        }
        else if (name.Length > 200)
        {
            Add("name", "Keep the name under 200 characters.");
        }

        if (input.CalendarId is { } calendarId
            && !await db.Set<SlaCalendar>().AnyAsync(c => c.Id == calendarId && c.WorkspaceId == input.WorkspaceId && c.DeletedAt == null, ct))
        {
            Add("calendarId", "That calendar isn't available. Choose another.");
        }

        var targets = input.Targets.Where(t => t.FirstResponseMinutes is not null || t.NextResponseMinutes is not null || t.ResolutionMinutes is not null).ToList();
        if (targets.Count == 0)
        {
            Add("targets", "Set at least one target, for example a resolution time for urgent tickets.");
        }
        if (targets.GroupBy(t => t.Priority).Any(g => g.Count() > 1))
        {
            Add("targets", "Each priority can have only one row of targets.");
        }
        foreach (var t in targets)
        {
            foreach (var minutes in new[] { t.FirstResponseMinutes, t.NextResponseMinutes, t.ResolutionMinutes })
            {
                if (minutes is { } m && (m < 1 || m > MaxTargetMinutes))
                {
                    Add("targets", $"{t.Priority} targets must be between 1 minute and 365 days.");
                    break;
                }
            }
        }

        if (input.Reminders.Count > 6)
        {
            Add("reminders", "Use at most six reminders.");
        }
        if (input.Reminders.Any(r => r.MinutesBefore is < 0 or > 10080 || !Enum.IsDefined(r.Target)))
        {
            Add("reminders", "Reminders must be between 1 minute and 7 days before a target.");
        }

        var levels = input.Escalations.Resolution;
        if (levels.Any(l => l.Level is < 1 or > 4) || levels.Select(l => l.Level).Distinct().Count() != levels.Count)
        {
            Add("escalations", "Resolution escalations use levels 1 to 4, once each.");
        }
        var ordered = levels.OrderBy(l => l.Level).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].DelayMinutes is < 0 or > 43200)
            {
                Add("escalations", $"Level {ordered[i].Level} must fire between 0 minutes and 30 days after the breach.");
            }
            else if (i > 0 && ordered[i].DelayMinutes < ordered[i - 1].DelayMinutes)
            {
                Add("escalations", $"Level {ordered[i].Level} can't fire before level {ordered[i - 1].Level}.");
            }
        }

        var notifyGroups = levels.SelectMany(l => l.Notify.GroupIds).Concat(input.Escalations.FirstResponse.GroupIds).Distinct().ToList();
        if (notifyGroups.Count > 0 && await db.AgentGroups.CountAsync(g => notifyGroups.Contains(g.Id) && g.WorkspaceId == input.WorkspaceId, ct) != notifyGroups.Count)
        {
            Add("escalations", "One of the groups to notify isn't in this workspace.");
        }
        var notifyUsers = levels.SelectMany(l => l.Notify.UserIds).Concat(input.Escalations.FirstResponse.UserIds).Distinct().ToList();
        if (notifyUsers.Count > 0 && await db.Users.CountAsync(u => notifyUsers.Contains(u.Id) && u.IsActive && (u.IsAgent || u.IsOrgAdmin), ct) != notifyUsers.Count)
        {
            Add("escalations", "Only active agents can be notified.");
        }

        await ValidateConditionsAsync(input, Add, ct);
        return errors;
    }

    private async Task ValidateConditionsAsync(PolicyInput input, Action<string, string> add, CancellationToken ct)
    {
        foreach (var condition in input.Conditions)
        {
            var values = condition.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (values.Count == 0)
            {
                continue;
            }
            var ids = values.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).ToList();
            var ok = condition.Field switch
            {
                SlaConditionFields.Type => values.All(v => v is "incident" or "request"),
                SlaConditionFields.Source => !ids.Contains(Guid.Empty) && await db.TicketSources.CountAsync(s => ids.Contains(s.Id), ct) == ids.Distinct().Count(),
                SlaConditionFields.RequesterGroup => !ids.Contains(Guid.Empty) && await db.RequesterGroups.CountAsync(s => ids.Contains(s.Id), ct) == ids.Distinct().Count(),
                SlaConditionFields.Department => !ids.Contains(Guid.Empty) && await db.Departments.CountAsync(s => ids.Contains(s.Id), ct) == ids.Distinct().Count(),
                SlaConditionFields.Category => !ids.Contains(Guid.Empty) && await db.TicketCategories.CountAsync(s => ids.Contains(s.Id) && s.WorkspaceId == input.WorkspaceId, ct) == ids.Distinct().Count(),
                SlaConditionFields.Group => !ids.Contains(Guid.Empty) && await db.AgentGroups.CountAsync(s => ids.Contains(s.Id) && s.WorkspaceId == input.WorkspaceId, ct) == ids.Distinct().Count(),
                _ when condition.Field.StartsWith(SlaConditionFields.CustomPrefix, StringComparison.Ordinal) =>
                    await db.CustomFieldDefinitions.AnyAsync(f => f.EntityType == "ticket" && f.Key == condition.Field.Substring(SlaConditionFields.CustomPrefix.Length)
                        && (f.WorkspaceId == null || f.WorkspaceId == input.WorkspaceId), ct)
                    && values.All(v => v.Length <= 200),
                _ => false,
            };
            if (!ok)
            {
                add("conditions", $"The {SlaText.ConditionLabel(condition.Field).ToLowerInvariant()} condition has a value that isn't available. Pick again.");
            }
        }
    }
}

/// <summary>Reads pasted holiday lists: one per line, a date and a name in either order, comma, semicolon or tab separated.</summary>
public static class HolidayCsv
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "d MMMM yyyy", "d MMM yyyy", "MMMM d yyyy", "MMMM d, yyyy", "MMM d yyyy", "MMM d, yyyy",
    ];

    public static (List<(DateOnly Date, string Name)> Rows, List<string> Errors) Parse(string? text, int maxLines)
    {
        var rows = new List<(DateOnly, string)>();
        var errors = new List<string>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (rows.Count + errors.Count >= maxLines)
            {
                errors.Add($"Stopped after {maxLines} lines. Import the rest separately.");
                break;
            }
            var cells = SplitCells(line);
            DateOnly? date = null;
            var nameParts = new List<string>();
            foreach (var cell in cells)
            {
                if (date is null && TryDate(cell, out var d))
                {
                    date = d;
                }
                else if (cell.Length > 0)
                {
                    nameParts.Add(cell);
                }
            }
            if (date is null)
            {
                // A header row such as "Date,Name" is fine to skip.
                if (lineNo == 1 && line.Contains("date", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                errors.Add($"Line {lineNo}: couldn't read a date in “{Clip(line)}”. Use a date like 2026-12-25.");
                continue;
            }
            var name = string.Join(" ", nameParts).Trim();
            rows.Add((date.Value, name.Length == 0 ? "Holiday" : Clip(name, 200)));
        }
        return (rows, errors);
    }

    private static List<string> SplitCells(string line)
    {
        var separator = line.Contains('\t') ? '\t' : line.Contains(';') ? ';' : ',';
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == separator && !quoted)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        cells.Add(current.ToString().Trim());
        // "Dec 25, 2026" split on its comma: rejoin month-day with year.
        for (var i = 0; i + 1 < cells.Count; i++)
        {
            if (!TryDate(cells[i], out _) && TryDate($"{cells[i]}, {cells[i + 1]}", out _))
            {
                cells[i] = $"{cells[i]}, {cells[i + 1]}";
                cells.RemoveAt(i + 1);
            }
        }
        return cells;
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);

    private static string Clip(string value, int max = 60) => value.Length <= max ? value : value[..max] + "…";
}
