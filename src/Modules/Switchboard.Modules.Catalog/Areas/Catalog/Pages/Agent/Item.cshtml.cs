using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Data;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Services;
using Switchboard.Modules.Catalog.Web;
using Switchboard.UI;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Agent;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class ItemModel(ICatalogService catalog, ICatalogRequestService requests, SwitchboardDbContext db, IActorAccessor actors, ModuleCatalog modules)
    : CatalogItemPageBase(catalog, requests, db, actors)
{
    protected override bool ForAgent => true;

    protected override IActionResult Submitted(SubmitResult result)
    {
        var waiting = result.Approval is null ? "" : ", waiting for approval";
        TempData.Toast($"Request {result.Ticket.DisplayId} created{waiting}");
        return Redirect(modules.Has("ServiceDesk") ? $"/tickets/{result.Ticket.Number}" : "/catalog");
    }
}
