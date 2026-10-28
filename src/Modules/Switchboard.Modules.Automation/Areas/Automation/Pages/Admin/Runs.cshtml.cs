using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Web;
using Switchboard.Modules.Automation.Workflows;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Admin;

public sealed class RunsModel(WorkflowService workflows) : AutomationAdminPageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTimeOffset? Before { get; set; }

    public AutWorkflow Workflow { get; private set; } = default!;
    public List<Row> Runs { get; private set; } = [];
    public bool HasMore { get; private set; }

    public sealed record Row(AutRun Run, string? TicketDisplayId, string? TicketSubject);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Workflow = await workflows.GetAsync(id, ct);
        await LoadWorkspaceAsync(Workflow.WorkspaceId, ct);
        var query = Db.Set<AutRun>().AsNoTracking().Where(r => r.WorkflowId == id);
        query = Status switch
        {
            "failed" => query.Where(r => r.Status == RunStatus.Failed),
            "blocked" => query.Where(r => r.Status == RunStatus.Blocked),
            "succeeded" => query.Where(r => r.Status == RunStatus.Succeeded),
            _ => query,
        };
        if (Before is { } before)
        {
            query = query.Where(r => r.StartedAt < before);
        }
        var runs = await query.OrderByDescending(r => r.StartedAt).Take(PageSize + 1).ToListAsync(ct);
        HasMore = runs.Count > PageSize;
        runs = runs.Take(PageSize).ToList();
        var ticketIds = runs.Where(r => r.TicketId != null).Select(r => r.TicketId!.Value).Distinct().ToList();
        var tickets = await Db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Type, t.Number, t.Subject }).ToDictionaryAsync(t => t.Id, ct);
        Runs = runs.Select(r => r.TicketId is { } t && tickets.TryGetValue(t, out var ticket)
            ? new Row(r, Core.Domain.Ticket.FormatDisplayId(ticket.Type, ticket.Number), ticket.Subject)
            : new Row(r, null, null)).ToList();
        return Page();
    }
}
