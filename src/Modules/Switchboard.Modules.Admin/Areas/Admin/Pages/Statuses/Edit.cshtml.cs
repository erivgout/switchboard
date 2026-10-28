using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Statuses;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.FieldsManage)]
public sealed class EditModel(TicketConfigService config, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty]
    public IndexModel.StatusForm Input { get; set; } = new();

    [BindProperty]
    public Guid? ReplacementId { get; set; }

    public TicketStatus Status { get; private set; } = default!;
    public int Tickets { get; private set; }
    public string WorkspaceKey { get; private set; } = "";
    public List<SelectListItem> Replacements { get; private set; } = [];

    public async Task OnGetAsync(Guid id)
    {
        await LoadAsync(id);
        Input = new IndexModel.StatusForm
        {
            Name = Status.Name,
            RequesterLabel = Status.RequesterLabel,
            Category = Status.Category,
            SlaTimerOn = Status.SlaTimerOn,
            IsDefault = Status.IsDefault,
        };
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        await LoadAsync(id);
        var input = new StatusInput(Input.Name, Input.RequesterLabel, Status.IsSystem ? Status.Category : Input.Category, Input.SlaTimerOn, Input.IsDefault);
        if (await TryAsync(() => config.UpdateStatusAsync(id, input)))
        {
            TempData.Toast("Status saved");
            return Redirect(Link("/admin/statuses", ("ws", WorkspaceKey)));
        }
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        await LoadAsync(id);
        var moved = 0;
        if (await TryAsync(async () => moved = await config.DeleteStatusAsync(id, ReplacementId), ""))
        {
            TempData.Toast(moved > 0 ? $"Status deleted. {moved} {(moved == 1 ? "ticket" : "tickets")} moved." : "Status deleted");
            return Redirect(Link("/admin/statuses", ("ws", WorkspaceKey)));
        }
        await OnGetAsync(id);
        return Page();
    }

    private async Task LoadAsync(Guid id)
    {
        var row = await config.GetStatusAsync(id);
        Status = row.Status;
        Tickets = row.Tickets;
        WorkspaceKey = await db.Workspaces.Where(w => w.Id == Status.WorkspaceId).Select(w => w.Key).FirstAsync();
        Replacements = (await db.TicketStatuses.AsNoTracking()
                .Where(s => s.WorkspaceId == Status.WorkspaceId && s.Category == Status.Category && s.Id != id)
                .OrderBy(s => s.Order).Select(s => new { s.Id, s.Name }).ToListAsync())
            .Select(s => new SelectListItem(s.Name, s.Id.ToString(), s.Id == ReplacementId))
            .Prepend(new SelectListItem("Choose a status", "", ReplacementId is null)).ToList();
    }
}
