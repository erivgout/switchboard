using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Domain;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Modules.Catalog.Services;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Portal;

/// <summary>The requester's view of one catalog request: items, approval progress, and the way back after a rejection.</summary>
public sealed class RequestModel(ICatalogRequestService requests, IApprovalService approvals, IPermissionService permissions, ModuleCatalog modules) : PageModel
{
    public TicketCatalogView View { get; private set; } = default!;
    public ApprovalDetails? Approval { get; private set; }
    public string? ConversationHref { get; private set; }
    public string StatusName { get; private set; } = "";
    public string? StatusLabel { get; private set; }
    public StatusCategory StatusCategory { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public async Task<IActionResult> OnGetAsync(int number)
    {
        var ct = HttpContext.RequestAborted;
        var ticket = await (await permissions.RequesterVisibleTicketsAsync(ct))
            .Where(t => t.Number == number)
            .Select(t => new { t.Id, t.CreatedAt, t.Status!.Name, t.Status.RequesterLabel, t.Status.Category })
            .FirstOrDefaultAsync(ct);
        if (ticket is null || await requests.GetForRequesterAsync(ticket.Id, ct) is not { } view)
        {
            return NotFound();
        }
        View = view;
        StatusName = ticket.Name;
        StatusLabel = ticket.RequesterLabel;
        StatusCategory = ticket.Category;
        CreatedAt = ticket.CreatedAt;
        Approval = view.Approval is { } a ? await approvals.GetAsync(a.Id, ct) : null;
        ConversationHref = modules.Has("Portal") ? Switchboard.Core.Tickets.TicketUrls.Requester(number) : null;
        return Page();
    }
}
