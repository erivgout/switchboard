using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;

namespace Switchboard.Modules.Approvals.Web;

/// <remarks>Slot view components take the slot model as a typed <c>model</c> parameter.</remarks>
internal static class ApprovalViews
{
    public const string Root = "/Areas/Approvals/Pages/Components/";
}

/// <summary>Tickets with an approval in progress, loaded once per request for queue badges.</summary>
public sealed class PendingApprovalTickets(SwitchboardDbContext db)
{
    private HashSet<Guid>? _ids;

    public async Task<bool> IsPendingAsync(Guid ticketId, CancellationToken ct)
    {
        _ids ??= (await db.Set<AprRequest>().AsNoTracking()
            .Where(r => r.SubjectType == ApprovalSubjectTypes.Ticket && r.Status == ApprovalStatus.Requested)
            .Select(r => r.SubjectId)
            .ToListAsync(ct)).ToHashSet();
        return _ids.Contains(ticketId);
    }
}

public sealed record TicketApprovalsModel(IReadOnlyList<ApprovalDetails> Approvals, string ReturnUrl, bool ForRequester);

/// <summary><c>ticket.sidebar</c>: approval status, chain and actions on the agent ticket page.</summary>
public sealed class TicketApprovalsViewComponent(IApprovalService approvals) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Ticket? model)
    {
        if (model is not { } ticket)
        {
            return Content("");
        }
        var list = await approvals.ListForSubjectAsync(ApprovalSubjectTypes.Ticket, ticket.Id, HttpContext.RequestAborted);
        if (list.Count == 0)
        {
            return Content("");
        }
        return View(ApprovalViews.Root + "TicketPanel.cshtml", new TicketApprovalsModel(list, Request.Path + Request.QueryString, false));
    }
}

/// <summary><c>portal.ticket</c>: the requester sees who is approving and what was decided.</summary>
public sealed class RequesterTicketApprovalsViewComponent(IApprovalService approvals) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Ticket? model)
    {
        if (model is not { } ticket)
        {
            return Content("");
        }
        var list = await approvals.ListForSubjectAsync(ApprovalSubjectTypes.Ticket, ticket.Id, HttpContext.RequestAborted);
        if (list.Count == 0)
        {
            return Content("");
        }
        return View(ApprovalViews.Root + "TicketPanel.cshtml", new TicketApprovalsModel(list.Take(1).ToList(), Request.Path + Request.QueryString, true));
    }
}

/// <summary><c>ticket.list.badges</c>: marks queue rows that are waiting for approval.</summary>
public sealed class TicketApprovalBadgeViewComponent(PendingApprovalTickets pending) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(TicketListItem? model)
    {
        if (model is not { } item || !await pending.IsPendingAsync(item.Id, HttpContext.RequestAborted))
        {
            return Content("");
        }
        return View(ApprovalViews.Root + "ListBadge.cshtml");
    }
}

/// <summary><c>topbar.end</c>: "N to approve" for agents with approvals waiting on them.</summary>
public sealed class ApprovalsTopbarViewComponent(IApprovalService approvals) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        var count = await approvals.CountPendingAsync(HttpContext.RequestAborted);
        return count == 0 ? Content("") : View(ApprovalViews.Root + "Topbar.cshtml", count);
    }
}

public sealed record PortalHomeApprovalsModel(IReadOnlyList<ApprovalListItem> Items, int Total);

/// <summary><c>portal.home</c>: approvals waiting on the signed-in person.</summary>
public sealed class PortalHomeApprovalsViewComponent(IApprovalService approvals) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        var total = await approvals.CountPendingAsync(HttpContext.RequestAborted);
        if (total == 0)
        {
            return Content("");
        }
        var items = await approvals.ListPendingAsync(3, HttpContext.RequestAborted);
        return View(ApprovalViews.Root + "PortalHome.cshtml", new PortalHomeApprovalsModel(items, total));
    }
}

public sealed record DelegationSectionModel(IReadOnlyList<DelegationView> Given, IReadOnlyList<DelegationView> Received, Guid UserId);

/// <summary><c>profile.sections</c>: hand approvals to someone else while you're away (FR-IAM-4).</summary>
public sealed class DelegationProfileViewComponent(IApprovalDelegationService delegation, IActorAccessor actors) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid? model)
    {
        if (model is not { } userId || actors.Current.UserId != userId)
        {
            return Content("");
        }
        var all = await delegation.ListForCurrentUserAsync(HttpContext.RequestAborted);
        return View(ApprovalViews.Root + "Delegation.cshtml", new DelegationSectionModel(
            all.Where(d => d.UserId == userId).ToList(),
            all.Where(d => d.DelegateUserId == userId).ToList(),
            userId));
    }
}

/// <summary><c>portal.home.attention</c>: the pending-approvals count in the portal's "Waiting on you" pane.</summary>
public sealed class PortalAttentionApprovalsViewComponent(IApprovalService approvals) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        var total = await approvals.CountPendingAsync(HttpContext.RequestAborted);
        return total == 0 ? Content("") : View(ApprovalViews.Root + "PortalAttention.cshtml", total);
    }
}
