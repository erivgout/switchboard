using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;
using Switchboard.Modules.Catalog.Web;
using Switchboard.UI;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Agent;

/// <summary>Stage changes from the ticket sidebar panel (htmx); returns the refreshed panel.</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class RequestedModel(ICatalogRequestService requests, SwitchboardDbContext db) : PageModel
{
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostStageAsync(Guid id, RequestedItemStage stage)
    {
        var ct = HttpContext.RequestAborted;
        var ticketId = await db.Set<CatRequestedItem>().AsNoTracking().Where(r => r.Id == id).Select(r => (Guid?)r.TicketId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException();
        try
        {
            await requests.ChangeStageAsync(id, stage, ct);
            Response.HxToast("Stage updated");
        }
        catch (ValidationException ex)
        {
            Response.HxToast(ex.Message, ToastKind.Error);
        }
        var view = await requests.GetForAgentAsync(ticketId, ct) ?? throw new NotFoundException();
        return Partial(CatalogViews.TicketItems, view);
    }
}
