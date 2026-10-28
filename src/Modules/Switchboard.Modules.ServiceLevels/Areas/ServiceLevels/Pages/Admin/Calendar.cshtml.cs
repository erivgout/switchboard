using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Services;
using Switchboard.Modules.ServiceLevels.Time;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceLevels.Areas.ServiceLevels.Pages.Admin;

public sealed class CalendarModel(SwitchboardDbContext db, IPermissionService permissions, SlaAdminService service) : SlaAdminPage(db, permissions)
{
    private const string ImportErrorsKey = "sla.import_errors";

    public static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    [BindProperty]
    public CalendarForm Form { get; set; } = new();

    public bool IsNew { get; private set; }
    public Guid? CalendarId { get; private set; }
    public string Title { get; private set; } = "New business calendar";
    public List<SlaHoliday> Holidays { get; private set; } = [];
    public List<(Guid Id, string Name)> Groups { get; private set; } = [];
    public List<(Guid Id, string Name)> OtherCalendars { get; private set; } = [];
    public List<(string Id, string Name)> Zones { get; private set; } = [];
    public IReadOnlyList<string> ImportErrors { get; private set; } = [];

    [BindProperty]
    public string? HolidayText { get; set; }

    public async Task<IActionResult> OnGetAsync(string id, string? ws, CancellationToken ct)
    {
        var calendar = await LoadAsync(id, ws, ct);
        if (calendar is null && !IsNew)
        {
            return NotFound();
        }
        if (IsNew && Workspace is null)
        {
            return NotFound();
        }
        Form = calendar is null ? CalendarForm.Blank(await Db.Organizations.AsNoTracking().Select(o => o.TimeZone).FirstOrDefaultAsync(ct)) : CalendarForm.From(calendar, Groups.Select(g => g.Id).ToHashSet(), await GroupsUsingAsync(calendar.Id, ct));
        if (TempData[ImportErrorsKey] is string errors)
        {
            ImportErrors = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        Zones = ZoneOptions(Form);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id, string? ws, CancellationToken ct)
    {
        var calendar = await LoadAsync(id, ws, ct);
        if ((calendar is null && !IsNew) || Workspace is null)
        {
            return NotFound();
        }
        try
        {
            var saved = await service.SaveCalendarAsync(Form.ToInput(Workspace.Id, CalendarId), ct);
            TempData.Toast(IsNew ? $"Calendar “{saved.Name}” created" : "Calendar saved");
            return Redirect(WithWorkspace(IsNew ? $"/admin/service-levels/calendars/{saved.Id}" : "/admin/service-levels/calendars"));
        }
        catch (ValidationException ex)
        {
            AddErrors(ex);
            Zones = ZoneOptions(Form);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportAsync(string id, string? ws, CancellationToken ct)
    {
        if (await LoadAsync(id, ws, ct) is null)
        {
            return NotFound();
        }
        try
        {
            var result = await service.ImportHolidaysAsync(CalendarId!.Value, HolidayText, ct);
            var changed = result.Added + result.Updated;
            TempData.Toast(changed == 0 ? "No holidays imported" : result.Added == 1 && result.Updated == 0 ? "1 holiday imported" : $"{changed} holidays imported",
                result.Errors.Count > 0 && changed == 0 ? ToastKind.Error : ToastKind.Success);
            if (result.Errors.Count > 0)
            {
                TempData[ImportErrorsKey] = string.Join("\n", result.Errors);
            }
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect(WithWorkspace($"/admin/service-levels/calendars/{CalendarId}") + "#holidays");
    }

    public async Task<IActionResult> OnPostRemoveHolidayAsync(string id, string? ws, Guid holidayId, CancellationToken ct)
    {
        if (await LoadAsync(id, ws, ct) is null)
        {
            return NotFound();
        }
        await service.RemoveHolidayAsync(CalendarId!.Value, holidayId, ct);
        TempData.Toast("Holiday removed");
        return Redirect(WithWorkspace($"/admin/service-levels/calendars/{CalendarId}") + "#holidays");
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, string? ws, Guid replacementId, CancellationToken ct)
    {
        if (await LoadAsync(id, ws, ct) is null)
        {
            return NotFound();
        }
        try
        {
            await service.DeleteCalendarAsync(CalendarId!.Value, replacementId, ct);
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect(WithWorkspace($"/admin/service-levels/calendars/{CalendarId}"));
        }
        TempData.Toast("Calendar deleted");
        return Redirect(WithWorkspace("/admin/service-levels/calendars"));
    }

    public IEnumerable<string> Errors(string key) =>
        ModelState.TryGetValue(key, out var entry) ? entry.Errors.Select(e => e.ErrorMessage) : [];

    private async Task<SlaCalendar?> LoadAsync(string id, string? ws, CancellationToken ct)
    {
        IsNew = id == "new";
        SlaCalendar? calendar = null;
        if (IsNew)
        {
            if (!await UseWorkspaceAsync(ws, ct))
            {
                return null;
            }
        }
        else
        {
            if (!Guid.TryParse(id, out var calendarId))
            {
                return null;
            }
            try
            {
                calendar = await service.CalendarAsync(calendarId, ct);
            }
            catch (NotFoundException)
            {
                return null;
            }
            if (!await UseWorkspaceAsync(calendar.WorkspaceId, ct))
            {
                return null;
            }
            CalendarId = calendar.Id;
            Title = calendar.Name;
            Holidays = calendar.Holidays.OrderBy(h => h.Date).ToList();
        }

        var wsId = Workspace.Id;
        Groups = (await Db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == wsId).OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct))
            .Select(g => (g.Id, g.Name)).ToList();
        OtherCalendars = (await Db.Set<SlaCalendar>().AsNoTracking().Where(c => c.WorkspaceId == wsId && c.DeletedAt == null && c.Id != CalendarId)
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Select(c => (c.Id, c.Name)).ToList();
        return calendar;
    }

    private async Task<HashSet<Guid>> GroupsUsingAsync(Guid calendarId, CancellationToken ct) =>
        (await Db.AgentGroups.AsNoTracking().Where(g => g.BusinessCalendarId == calendarId).Select(g => g.Id).ToListAsync(ct)).ToHashSet();

    /// <summary>
    /// System timezones, keyed by IANA id where one exists so data moves between Windows and Linux hosts.
    /// A stored Windows id is shown as its IANA equivalent.
    /// </summary>
    private static List<(string Id, string Name)> ZoneOptions(CalendarForm form)
    {
        if (form.TimeZoneId is { } stored && TimeZoneInfo.TryConvertWindowsIdToIanaId(stored, out var canonical))
        {
            form.TimeZoneId = canonical;
        }
        var current = form.TimeZoneId;
        var zones = TimeZoneInfo.GetSystemTimeZones()
            .Select(z => (Id: z.HasIanaId ? z.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id, Name: z.DisplayName, z.BaseUtcOffset))
            .GroupBy(z => z.Id).Select(g => g.First())
            .OrderBy(z => z.BaseUtcOffset).ThenBy(z => z.Name, StringComparer.Ordinal)
            .Select(z => (z.Id, z.Name))
            .ToList();
        if (!string.IsNullOrWhiteSpace(current) && zones.All(z => z.Id != current))
        {
            zones.Insert(0, (current, BusinessCalendar.FindZone(current)?.DisplayName ?? current));
        }
        return zones;
    }
}

public sealed class CalendarForm
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? TimeZoneId { get; set; }
    public bool Is24x7 { get; set; }
    public bool IsDefault { get; set; }
    public List<DayRow> Days { get; set; } = [];
    public List<Guid> Groups { get; set; } = [];

    public sealed class DayRow
    {
        public DayOfWeek Day { get; set; }
        public bool Open { get; set; }
        public string? Start { get; set; }
        public string? End { get; set; }
        public string? Start2 { get; set; }
        public string? End2 { get; set; }
    }

    public static CalendarForm Blank(string? timeZone) => new()
    {
        Name = "",
        TimeZoneId = BusinessCalendar.FindZone(timeZone) is not null ? timeZone : "UTC",
        IsDefault = false,
        Days = CalendarModel.Week.Select(d => new DayRow
        {
            Day = d,
            Open = d is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            Start = "09:00",
            End = "17:00",
        }).ToList(),
    };

    public static CalendarForm From(SlaCalendar c, HashSet<Guid> knownGroups, HashSet<Guid> using_) => new()
    {
        Name = c.Name,
        Description = c.Description,
        TimeZoneId = c.TimeZoneId,
        Is24x7 = c.Is24x7,
        IsDefault = c.IsDefault,
        Groups = using_.Where(knownGroups.Contains).ToList(),
        Days = CalendarModel.Week.Select(d =>
        {
            var hours = c.Hours.Where(h => h.Day == d).OrderBy(h => h.StartMinute).ToList();
            return new DayRow
            {
                Day = d,
                Open = hours.Count > 0,
                Start = hours.Count > 0 ? SlaText.Clock(hours[0].StartMinute) : "09:00",
                End = hours.Count > 0 ? EndText(hours[0].EndMinute) : "17:00",
                Start2 = hours.Count > 1 ? SlaText.Clock(hours[1].StartMinute) : null,
                End2 = hours.Count > 1 ? EndText(hours[1].EndMinute) : null,
            };
        }).ToList(),
    };

    public CalendarInput ToInput(Guid workspaceId, Guid? id)
    {
        var hours = new List<CalendarHoursInput>();
        foreach (var day in Days.Where(d => d.Open))
        {
            hours.Add(Interval(day.Day, day.Start, day.End));
            if (!string.IsNullOrWhiteSpace(day.Start2) || !string.IsNullOrWhiteSpace(day.End2))
            {
                hours.Add(Interval(day.Day, day.Start2, day.End2));
            }
        }
        return new CalendarInput
        {
            Id = id,
            WorkspaceId = workspaceId,
            Name = Name,
            Description = Description,
            TimeZoneId = TimeZoneId ?? "",
            Is24x7 = Is24x7,
            IsDefault = IsDefault,
            Hours = hours,
            GroupIds = Groups,
        };
    }

    /// <summary>An end at or before the start runs into the next day (an overnight shift).</summary>
    private static CalendarHoursInput Interval(DayOfWeek day, string? start, string? end)
    {
        if (!TryMinutes(start, out var s) || !TryMinutes(end, out var e))
        {
            throw new ValidationException("hours", $"Enter a start and end time for {day}, like 09:00 and 17:00.");
        }
        if (e <= s)
        {
            e += WorkingInterval.MinutesPerDay;
        }
        return new CalendarHoursInput(day, s, e);
    }

    private static bool TryMinutes(string? value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (value.Trim() == "24:00")
        {
            minutes = WorkingInterval.MinutesPerDay;
            return true;
        }
        if (TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            minutes = t.Hour * 60 + t.Minute;
            return true;
        }
        return false;
    }

    private static string EndText(int endMinute) => endMinute == WorkingInterval.MinutesPerDay ? "00:00" : SlaText.Clock(endMinute);
}
