using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Scheduling;
using Switchboard.Modules.Automation.Web;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Admin;

public sealed class SchedulesModel(ScheduleService schedules) : AutomationAdminPageModel
{
    public sealed record Row(AutSchedule Schedule, string Subject, string When, string? LastTicket);

    public List<Row> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadWorkspaceAsync(ct: ct))
        {
            return Forbid();
        }
        var list = await schedules.ListAsync(Workspace.Id, ct);
        var ticketIds = list.Where(s => s.LastTicketId != null).Select(s => s.LastTicketId!.Value).ToList();
        var tickets = await Db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Type, t.Number }).ToDictionaryAsync(t => t.Id, t => Core.Domain.Ticket.FormatDisplayId(t.Type, t.Number), ct);
        Rows = list.Select(s => new Row(s, TicketTemplate.Parse(s.TemplateJson).Subject, Recurrence.Describe(s),
            s.LastTicketId is { } id ? tickets.GetValueOrDefault(id) : null)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await LoadWorkspaceAsync(ct: ct);
        await schedules.SetEnabledAsync(id, enabled, ct);
        return Done(enabled ? "Schedule turned on" : "Schedule turned off", "/admin/automations/schedules" + WsQuery);
    }
}
