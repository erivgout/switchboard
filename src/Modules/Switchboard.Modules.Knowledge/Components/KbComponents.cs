using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Domain;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Components;

/// <remarks>
/// Slot components that use their model declare its real type (<c>string?</c>, <c>Ticket?</c>), not <c>object?</c>:
/// with a single <c>object</c> parameter, ASP.NET Core passes the slot tag helper's whole <c>new { model }</c>
/// wrapper instead of the model.
/// </remarks>
internal static class KbViews
{
    public const string Root = "/Areas/Knowledge/Pages/Shared/Components/";
}

public sealed record KbAnnouncementsModel(IReadOnlyList<KbAnnouncementItem> Items, bool CanManage, bool Portal);

public sealed record KbArticleListModel(string Query, IReadOnlyList<KbSearchHit> Items, int Total, bool Portal);

public sealed record KbTicketSuggestion(KbSearchHit Hit, bool RequestersCanRead);

public sealed record KbTicketSuggestionsModel(Guid TicketId, IReadOnlyList<KbTicketSuggestion> Items, bool CanWrite);

public sealed record KbPortalHomeModel(IReadOnlyList<KbAnnouncementItem> Announcements, IReadOnlyList<KbSearchHit> Popular);

/// <summary><c>dashboard.widgets</c>: announcements for agents (FR-KB-10).</summary>
public sealed class KbDashboardAnnouncements(IKbAnnouncementService announcements, IKnowledgeVisibility visibility) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        var items = await announcements.ActiveAsync(asAgent: true, HttpContext.RequestAborted);
        var viewer = await visibility.ViewerAsync(HttpContext.RequestAborted);
        return View(KbViews.Root + "Announcements.cshtml", new KbAnnouncementsModel(items, viewer.CanManageAny, Portal: false));
    }
}

/// <summary><c>portal.home</c>: announcements for requesters and the most read articles.</summary>
public sealed class KbPortalHome(IKbAnnouncementService announcements, IKnowledgeService knowledge) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        var ct = HttpContext.RequestAborted;
        return View(KbViews.Root + "PortalHome.cshtml", new KbPortalHomeModel(
            await announcements.ActiveAsync(asAgent: false, ct),
            await knowledge.PopularAsync(6, ct)));
    }
}

/// <summary><c>portal.search</c>: published articles matching the requester's search.</summary>
public sealed class KbPortalSearchResults(IKnowledgeService knowledge) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string? model)
    {
        var query = model ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            return Content("");
        }
        var page = await knowledge.SearchAsync(new KbSearchQuery { Text = query, Mode = KbSearchMode.Published, PageSize = 6 }, HttpContext.RequestAborted);
        return View(KbViews.Root + "SearchResults.cshtml", new KbArticleListModel(query, page.Items, page.Total, Portal: true));
    }
}

/// <summary><c>search.results</c>: articles in the agent's global search, drafts included.</summary>
public sealed class KbAgentSearchResults(IKnowledgeService knowledge, IKnowledgeVisibility visibility) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string? model)
    {
        var query = model ?? "";
        var viewer = await visibility.ViewerAsync(HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(query) || !viewer.IsAnyKbAgent)
        {
            return Content("");
        }
        var page = await knowledge.SearchAsync(new KbSearchQuery { Text = query, Mode = KbSearchMode.Agent, PageSize = 6 }, HttpContext.RequestAborted);
        return View(KbViews.Root + "SearchResults.cshtml", new KbArticleListModel(query, page.Items, page.Total, Portal: false));
    }
}

/// <summary><c>portal.newticket.aside</c>: suggestions while the requester types a subject (FR-POR-7).</summary>
public sealed class KbDeflectionAside : ViewComponent
{
    public IViewComponentResult Invoke(object? model) => View(KbViews.Root + "Deflection.cshtml");
}

/// <summary><c>ticket.sidebar</c>: published articles matching the ticket subject, with copy-link (FR-KB-6).</summary>
public sealed class KbTicketSuggestions(IKnowledgeService knowledge, IKnowledgeVisibility visibility) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Ticket? model)
    {
        if (model is not Ticket ticket)
        {
            return Content("");
        }
        var ct = HttpContext.RequestAborted;
        var viewer = await visibility.ViewerAsync(ct);
        if (!viewer.IsAnyKbAgent)
        {
            return Content("");
        }
        var items = await knowledge.SuggestAsync(ticket.Subject, 4, ct);
        var index = await visibility.FoldersAsync(ct);
        var suggestions = items
            .Select(h => new KbTicketSuggestion(h, index.Get(h.FolderId)?.RequestersCanRead == true))
            .ToList();
        return View(KbViews.Root + "TicketSuggestions.cshtml",
            new KbTicketSuggestionsModel(ticket.Id, suggestions, viewer.CanWrite(ticket.WorkspaceId)));
    }
}

/// <summary><c>ticket.actions</c>: "Create article from ticket" (FR-KB-6).</summary>
public sealed class KbTicketActions(IKnowledgeVisibility visibility) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Ticket? model)
    {
        if (model is not Ticket ticket)
        {
            return Content("");
        }
        var viewer = await visibility.ViewerAsync(HttpContext.RequestAborted);
        return viewer.CanWrite(ticket.WorkspaceId)
            ? View(KbViews.Root + "TicketActions.cshtml", ticket.Id)
            : Content("");
    }
}
