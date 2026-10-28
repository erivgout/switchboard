using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Agent;

/// <summary>
/// Development only: renders a ticket's <c>ticket.sidebar</c> and <c>portal.ticket</c> slots, so the catalog and approval
/// panels can be checked without the service desk and portal modules. 404 outside Development.
/// </summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class DevTicketModel(ITicketService tickets, IPermissionService permissions, IHostEnvironment environment) : PageModel
{
    public Ticket Ticket { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(int number)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }
        var id = await (await permissions.AgentVisibleTicketsAsync()).Where(t => t.Number == number).Select(t => (Guid?)t.Id).FirstOrDefaultAsync();
        if (id is null)
        {
            return NotFound();
        }
        Ticket = await tickets.GetForAgentAsync(id.Value);
        return Page();
    }
}
