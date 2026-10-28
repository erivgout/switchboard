using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Scenarios;
using Switchboard.UI;

namespace Switchboard.Modules.Automation.Web;

public sealed record TicketScenariosModel(Guid TicketId, IReadOnlyList<AutScenario> Scenarios, string ReturnUrl);

/// <summary>Fills the ticket page's "More" menu (<c>ticket.actions</c>) with the agent's scenarios.</summary>
public sealed class TicketScenariosViewComponent(ScenarioService scenarios, IPermissionService permissions) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object model)
    {
        if (model is not Ticket ticket || (await permissions.CurrentAsync())?.IsAgent != true)
        {
            return Content("");
        }
        var visible = await scenarios.VisibleAsync(ticket.WorkspaceId);
        var returnUrl = HttpContext.Request.Path + HttpContext.Request.QueryString;
        return View("~/Areas/Automation/Pages/Shared/_TicketScenarios.cshtml", new TicketScenariosModel(ticket.Id, visible, returnUrl));
    }
}

public sealed record AvailabilityState(AgentAvailability Availability, string ReturnUrl);

/// <summary>Adds the agent's availability switch (Available / Away) to the user menu (<c>usermenu.items</c>).</summary>
public sealed class AvailabilityViewComponent(SwitchboardDbContext db) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object model)
    {
        if (model is not ShellModel { User: { IsAgent: true } user })
        {
            return Content("");
        }
        var availability = await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.Availability).FirstOrDefaultAsync();
        var returnUrl = HttpContext.Request.Path + HttpContext.Request.QueryString;
        return View("~/Areas/Automation/Pages/Shared/_Availability.cshtml", new AvailabilityState(availability, returnUrl));
    }
}
