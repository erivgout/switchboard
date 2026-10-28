using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Web;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Admin;

public sealed class RunModel(IPermissionService permissions) : AutomationAdminPageModel
{
    public AutRun Run { get; private set; } = default!;
    public AutWorkflow? Workflow { get; private set; }
    public string? TicketDisplayId { get; private set; }
    public string? TicketSubject { get; private set; }

    public sealed record Check(string Condition, bool Matched, string Actual);

    /// <summary>The conditions step's output, as a checklist.</summary>
    public static List<Check> Checks(string? json)
    {
        var list = new List<Check>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json ?? "[]");
            foreach (var group in doc.RootElement.EnumerateArray())
            {
                foreach (var rule in group.GetProperty("rules").EnumerateArray())
                {
                    list.Add(new Check(rule.GetProperty("condition").GetString() ?? "", rule.GetProperty("matched").GetBoolean(),
                        rule.TryGetProperty("actual", out var a) ? a.GetString() ?? "" : ""));
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
        return list;
    }

    public async Task<IActionResult> OnGetAsync(Guid runId, CancellationToken ct)
    {
        Run = await Db.Set<AutRun>().AsNoTracking().Include(r => r.Steps).FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException();
        Workflow = Run.WorkflowId is { } wid ? await Db.Set<AutWorkflow>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wid, ct) : null;
        var ticket = Run.TicketId is { } tid
            ? await Db.Tickets.AsNoTracking().Where(t => t.Id == tid).Select(t => new { t.WorkspaceId, t.Type, t.Number, t.Subject }).FirstOrDefaultAsync(ct)
            : null;
        // Runs belong to the workspace of their workflow (or ticket, for deleted workflows).
        var workspaceId = Workflow?.WorkspaceId ?? ticket?.WorkspaceId ?? throw new NotFoundException();
        if (!await permissions.HasAsync(AutomationModule.ManagePrivilege, workspaceId, ct))
        {
            throw new NotFoundException();
        }
        await LoadWorkspaceAsync(workspaceId, ct);
        if (ticket is not null)
        {
            TicketDisplayId = Core.Domain.Ticket.FormatDisplayId(ticket.Type, ticket.Number);
            TicketSubject = ticket.Subject;
        }
        return Page();
    }
}
