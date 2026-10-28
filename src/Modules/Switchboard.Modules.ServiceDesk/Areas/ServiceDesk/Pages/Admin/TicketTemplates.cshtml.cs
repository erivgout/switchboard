using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;
using Switchboard.UI;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Admin;

/// <summary>Ticket templates admin list (FR-TKT-23).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.SettingsManage)]
public sealed class TicketTemplatesModel(TicketTemplateService templates, SwitchboardDbContext db) : PageModel
{
    public IReadOnlyList<SdTicketTemplate> Templates { get; private set; } = [];
    public IReadOnlyDictionary<Guid, string> Categories { get; private set; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<Guid, string> Groups { get; private set; } = new Dictionary<Guid, string>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Templates = await templates.ManageableAsync(ct);
        Categories = await db.TicketCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        Groups = await db.AgentGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        ViewData["Title"] = "Ticket templates";
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await templates.DeleteAsync(id, ct);
            TempData.Toast("Template deleted.");
        }
        catch (Exception ex) when (ex is ForbiddenException or NotFoundException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect("/admin/ticket-templates");
    }
}
