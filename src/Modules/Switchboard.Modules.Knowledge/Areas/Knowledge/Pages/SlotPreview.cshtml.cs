using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

/// <summary>
/// Development only: renders this module's slot components outside their host pages, so they can be
/// reviewed while the host modules are built elsewhere.
/// </summary>
[Authorize(Policy = "priv:" + KbPrivileges.View)]
public sealed class SlotPreviewModel(IHostEnvironment environment, IPermissionService permissions) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Q { get; set; } = "vpn";
    public Ticket? Ticket { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }
        var visible = await permissions.AgentVisibleTicketsAsync(ct);
        Ticket = await visible.AsNoTracking().Include(t => t.Status).OrderBy(t => t.Number).FirstOrDefaultAsync(t => t.Subject.Contains("Outlook") || t.Subject.Contains("VPN"), ct)
            ?? await visible.AsNoTracking().Include(t => t.Status).OrderBy(t => t.Number).FirstOrDefaultAsync(ct);
        return Page();
    }
}
