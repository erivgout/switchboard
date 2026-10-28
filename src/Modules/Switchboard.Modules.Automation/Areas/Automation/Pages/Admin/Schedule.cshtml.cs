using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Scheduling;
using Switchboard.Modules.Automation.Web;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Admin;

public sealed class ScheduleModel(ScheduleService schedules, IUserDirectory directory, CatalogLoader catalogs) : AutomationAdminPageModel
{
    public static readonly string[] CommonZones =
    [
        "UTC", "America/Los_Angeles", "America/Denver", "America/Chicago", "America/New_York", "America/Toronto", "America/Sao_Paulo",
        "Europe/London", "Europe/Dublin", "Europe/Lisbon", "Europe/Paris", "Europe/Berlin", "Europe/Madrid", "Europe/Amsterdam",
        "Europe/Stockholm", "Europe/Warsaw", "Europe/Athens", "Africa/Johannesburg", "Asia/Dubai", "Asia/Kolkata", "Asia/Singapore",
        "Asia/Hong_Kong", "Asia/Tokyo", "Australia/Sydney", "Pacific/Auckland",
    ];

    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = "";

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public bool IsEnabled { get; set; } = true;
    [BindProperty] public string Subject { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public TicketType Type { get; set; } = TicketType.ServiceRequest;
    [BindProperty] public Priority Priority { get; set; } = Priority.Medium;
    [BindProperty] public string? RequesterEmail { get; set; }
    [BindProperty] public Guid? GroupId { get; set; }
    [BindProperty] public Guid? AgentId { get; set; }
    [BindProperty] public Guid? CategoryId { get; set; }
    [BindProperty] public string? Tags { get; set; }
    [BindProperty] public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Monthly;
    [BindProperty] public List<int> Days { get; set; } = [];
    [BindProperty] public int? DayOfMonth { get; set; } = 1;
    [BindProperty] public string Time { get; set; } = "09:00";
    [BindProperty] public string TimeZone { get; set; } = "UTC";
    [BindProperty] public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    [BindProperty] public DateOnly? EndDate { get; set; }

    public AutSchedule? Schedule { get; private set; }
    public BuilderCatalog Catalog { get; private set; } = default!;
    public IReadOnlyDictionary<string, string[]> Errors { get; private set; } = new Dictionary<string, string[]>();
    public List<(AutScheduleOccurrence Occurrence, string? DisplayId)> Recent { get; private set; } = [];
    public IReadOnlyList<string> Zones { get; private set; } = CommonZones;

    public string? ErrorFor(string key) => Errors.TryGetValue(key, out var e) ? e.FirstOrDefault() : null;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        if (Schedule is { } s)
        {
            var t = TicketTemplate.Parse(s.TemplateJson);
            Name = s.Name;
            IsEnabled = s.IsEnabled;
            Subject = t.Subject;
            Description = t.Description;
            Type = t.Type;
            Priority = t.Priority;
            RequesterEmail = t.RequesterId is { } r ? await Db.Users.Where(u => u.Id == r).Select(u => u.Email).FirstOrDefaultAsync(ct) : null;
            GroupId = t.GroupId;
            AgentId = t.AgentId;
            CategoryId = t.CategoryId;
            Tags = string.Join(", ", t.Tags);
            Frequency = s.Frequency;
            Days = Enumerable.Range(0, 7).Where(d => (s.DaysOfWeek & (1 << d)) != 0).ToList();
            DayOfMonth = s.DayOfMonth ?? 1;
            Time = s.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
            TimeZone = s.TimeZone;
            StartDate = s.StartDate;
            EndDate = s.EndDate;
        }
        else
        {
            TimeZone = await Db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";
            Days = [(int)DayOfWeek.Monday];
        }
        await PrepareAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        var requester = string.IsNullOrWhiteSpace(RequesterEmail) ? null : await directory.FindByEmailAsync(RequesterEmail, ct);
        var time = TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new TimeOnly(9, 0);
        var input = new ScheduleInput(
            Name,
            IsEnabled,
            new TicketTemplate
            {
                Subject = Subject,
                Description = Description,
                Type = Type,
                Priority = Priority,
                RequesterId = requester?.Id,
                GroupId = GroupId,
                AgentId = AgentId,
                CategoryId = CategoryId,
                Tags = Describer.SplitTags(Tags),
            },
            Frequency,
            Days.Where(d => d is >= 0 and <= 6).Aggregate(0, (mask, d) => mask | (1 << d)),
            DayOfMonth,
            time,
            TimeZone,
            StartDate,
            EndDate);
        try
        {
            var saved = await schedules.SaveAsync(Schedule?.Id, Workspace.Id, input, ct);
            return Done(saved.IsEnabled && saved.NextRunAt is not null ? "Schedule saved" : "Schedule saved. It's turned off.", $"/admin/automations/schedules/{saved.Id}");
        }
        catch (ValidationException ex)
        {
            Errors = ex.Errors;
            await PrepareAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Schedule is null)
        {
            return NotFound();
        }
        await schedules.DeleteAsync(Schedule.Id, ct);
        return Done("Schedule deleted", "/admin/automations/schedules" + WsQuery);
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (Id == "new")
        {
            return await LoadWorkspaceAsync(ct: ct);
        }
        if (!Guid.TryParse(Id, out var id))
        {
            return false;
        }
        Schedule = await schedules.GetAsync(id, ct);
        return await LoadWorkspaceAsync(Schedule.WorkspaceId, ct);
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        Catalog = await catalogs.LoadAsync(Workspace.Id, ct);
        Zones = CommonZones.Contains(TimeZone) ? CommonZones : [TimeZone, .. CommonZones];
        if (Schedule is not null)
        {
            var occurrences = await Db.Set<AutScheduleOccurrence>().AsNoTracking().Where(o => o.ScheduleId == Schedule.Id)
                .OrderByDescending(o => o.OccurrenceAt).Take(8).ToListAsync(ct);
            var ids = occurrences.Where(o => o.TicketId != null).Select(o => o.TicketId!.Value).ToList();
            var tickets = await Db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id))
                .Select(t => new { t.Id, t.Type, t.Number }).ToDictionaryAsync(t => t.Id, t => Ticket.FormatDisplayId(t.Type, t.Number), ct);
            Recent = occurrences.Select(o => (o, o.TicketId is { } id ? tickets.GetValueOrDefault(id) : null)).ToList();
        }
    }
}
