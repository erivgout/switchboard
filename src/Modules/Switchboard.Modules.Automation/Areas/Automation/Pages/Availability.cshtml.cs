using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Web;
using Switchboard.UI;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages;

/// <summary>Sets the signed-in agent's availability for automatic assignment (FR-RTE-2).</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class AvailabilityModel(SwitchboardDbContext db, IActorAccessor actors) : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync(string? status, string? returnUrl, CancellationToken ct)
    {
        if (!Enum.TryParse<AgentAvailability>(status, out var availability) || availability is not (AgentAvailability.Available or AgentAvailability.Away))
        {
            return BadRequest();
        }
        var userId = actors.Current.UserId ?? throw new ForbiddenException();
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.Availability = availability;
        await db.SaveChangesAsync(ct);

        var message = availability == AgentAvailability.Available
            ? "You're available. New tickets can be assigned to you."
            : "You're away. Automatic assignment skips you until you're back.";
        if (Request.IsHtmx())
        {
            Response.HxToast(message);
            return Partial("~/Areas/Automation/Pages/Shared/_Availability.cshtml", new AvailabilityState(availability, returnUrl ?? "/"));
        }
        TempData.Toast(message);
        return Redirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
