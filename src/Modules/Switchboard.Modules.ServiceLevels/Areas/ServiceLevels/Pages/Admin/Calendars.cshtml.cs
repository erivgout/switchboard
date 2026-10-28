using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Services;
using Switchboard.Modules.ServiceLevels.Time;

namespace Switchboard.Modules.ServiceLevels.Areas.ServiceLevels.Pages.Admin;

public sealed class CalendarsModel(SwitchboardDbContext db, IPermissionService permissions, SlaAdminService service, TimeProvider time)
    : SlaAdminPage(db, permissions)
{
    public sealed record Row(SlaCalendar Calendar, string Hours, string ZoneName, SlaHoliday? NextHoliday, IReadOnlyList<string> Groups, int Policies);

    public List<Row> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? ws, CancellationToken ct)
    {
        if (!await UseWorkspaceAsync(ws, ct))
        {
            return Forbid();
        }
        var calendars = await service.CalendarsAsync(Workspace.Id, ct);
        var ids = calendars.Select(c => c.Id).ToList();
        var groups = await Db.AgentGroups.AsNoTracking().Where(g => g.BusinessCalendarId != null && ids.Contains(g.BusinessCalendarId.Value))
            .Select(g => new { g.BusinessCalendarId, g.Name }).ToListAsync(ct);
        var policies = await Db.Set<SlaPolicy>().AsNoTracking().Where(p => p.DeletedAt == null && p.CalendarId != null && ids.Contains(p.CalendarId.Value))
            .GroupBy(p => p.CalendarId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        foreach (var c in calendars)
        {
            var zone = BusinessCalendar.FindZone(c.TimeZoneId);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone ?? TimeZoneInfo.Utc).DateTime);
            Rows.Add(new Row(c, CalendarText.Hours(c), zone?.DisplayName ?? c.TimeZoneId,
                c.Holidays.Where(h => h.Date >= today).OrderBy(h => h.Date).FirstOrDefault(),
                groups.Where(g => g.BusinessCalendarId == c.Id).Select(g => g.Name).OrderBy(n => n).ToList(),
                policies.GetValueOrDefault(c.Id)));
        }
        return Page();
    }
}

public static class CalendarText
{
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>"Mon–Fri 09:00–17:00; Sat 10:00–14:00", grouping consecutive days with the same hours.</summary>
    public static string Hours(SlaCalendar c)
    {
        if (c.Is24x7)
        {
            return "24 hours, 7 days";
        }
        var perDay = Week.Select(d => (Day: d, Text: string.Join(", ", c.Hours.Where(h => h.Day == d).OrderBy(h => h.StartMinute).Select(Interval)))).ToList();
        var parts = new List<string>();
        var i = 0;
        while (i < perDay.Count)
        {
            var j = i;
            while (j + 1 < perDay.Count && perDay[j + 1].Text == perDay[i].Text)
            {
                j++;
            }
            if (perDay[i].Text.Length > 0)
            {
                var days = i == j ? Short(perDay[i].Day) : $"{Short(perDay[i].Day)}–{Short(perDay[j].Day)}";
                parts.Add($"{days} {perDay[i].Text}");
            }
            i = j + 1;
        }
        return parts.Count == 0 ? "No working hours" : string.Join("; ", parts);
    }

    public static string Interval(SlaWorkingHours h)
    {
        var end = h.EndMinute == WorkingInterval.MinutesPerDay ? "24:00" : SlaText.Clock(h.EndMinute);
        return $"{SlaText.Clock(h.StartMinute)}–{end}{(h.EndMinute > WorkingInterval.MinutesPerDay ? " next day" : "")}";
    }

    public static string Short(DayOfWeek d) => d.ToString()[..3];
}
