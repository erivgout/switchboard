using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Catalog.Services;

namespace Switchboard.Modules.Catalog.Web;

/// <remarks>Slot view components take the slot model as a typed <c>model</c> parameter.</remarks>
internal static class CatalogViews
{
    public const string Root = "/Areas/Catalog/Pages/Components/";
    public const string TicketItems = Root + "TicketItems.cshtml";
}

/// <summary><c>ticket.sidebar</c>: requested items, their answers and stages, with stage controls for agents.</summary>
public sealed class CatalogTicketItemsViewComponent(ICatalogRequestService requests) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Ticket? model)
    {
        if (model is not { Type: TicketType.ServiceRequest } ticket)
        {
            return Content("");
        }
        try
        {
            var view = await requests.GetForAgentAsync(ticket.Id, HttpContext.RequestAborted);
            return view is null ? Content("") : View(CatalogViews.TicketItems, view);
        }
        catch (NotFoundException)
        {
            return Content("");
        }
    }
}

/// <summary><c>portal.ticket</c>: what the requester asked for, where it is, and the way back after a rejection (FR-SRM-10).</summary>
public sealed class CatalogRequesterItemsViewComponent(ICatalogRequestService requests) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Ticket? model)
    {
        if (model is not { Type: TicketType.ServiceRequest } ticket)
        {
            return Content("");
        }
        try
        {
            var view = await requests.GetForRequesterAsync(ticket.Id, HttpContext.RequestAborted);
            return view is null ? Content("") : View(CatalogViews.Root + "RequesterItems.cshtml", view);
        }
        catch (NotFoundException)
        {
            return Content("");
        }
    }
}

/// <summary><c>approval.subject</c>: shows approvers exactly what they're approving.</summary>
public sealed class CatalogApprovalSubjectViewComponent(ICatalogRequestService requests) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(AprRequest? model)
    {
        if (model is not { } approval)
        {
            return Content("");
        }
        var view = await requests.GetForApprovalAsync(approval, HttpContext.RequestAborted);
        return view is null ? Content("") : View(CatalogViews.Root + "ApprovalItems.cshtml", view);
    }
}

/// <summary><c>topbar.new</c>: "Request from catalog" in the agent New menu.</summary>
public sealed class CatalogNewMenuViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(object? model) => View(CatalogViews.Root + "NewMenu.cshtml");
}
