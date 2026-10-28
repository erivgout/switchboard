using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Services;
using Switchboard.Modules.Catalog.Web;
using Switchboard.UI;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Portal;

public sealed class ItemModel(ICatalogService catalog, ICatalogRequestService requests, SwitchboardDbContext db, IActorAccessor actors)
    : CatalogItemPageBase(catalog, requests, db, actors)
{
    protected override bool ForAgent => false;

    protected override IActionResult Submitted(SubmitResult result)
    {
        TempData.Toast(result.Approval is null ? "Request submitted" : "Request submitted for approval");
        return Redirect($"/portal/catalog/requests/{result.Ticket.Number}");
    }
}
