using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Reports;

namespace Switchboard.Modules.ServiceLevels.Components;

internal static class SlaSlots
{
    public const string ReportsPrivilege = "reports.view";

    public static void Register(IServiceCollection services)
    {
        services.AddSlot<SlaSidebarViewComponent>("ticket.sidebar", order: 20);
        services.AddSlot<SlaListBadgeViewComponent>("ticket.list.badges", order: 20);
        services.AddSlot<SlaPerformanceViewComponent>("dashboard.widgets", order: 50);
        services.AddSlot<SlaReportLinkViewComponent>("reports.catalog", order: 30);
        services.AddScoped<SlaReportService>();
    }

    internal const string Views = "~/Areas/ServiceLevels/Pages/Shared/Components/";
}

public sealed record SlaSidebarModel(
    Ticket Ticket,
    StatusCategory Category,
    SlaTicketState? State,
    IReadOnlyList<SlaEvent> Events)
{
    public bool Paused => State?.PausedSince is not null;
}

/// <summary>The "Service level" card on the agent ticket page (FR-SLA-10).</summary>
public sealed class SlaSidebarViewComponent(SwitchboardDbContext db, IPermissionService permissions) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        if (model is not Ticket ticket || (await permissions.CurrentAsync(HttpContext.RequestAborted))?.IsAgent != true)
        {
            return Content("");
        }
        var ct = HttpContext.RequestAborted;
        var category = ticket.Status?.Id == ticket.StatusId
            ? ticket.Status.Category
            : await db.TicketStatuses.AsNoTracking().Where(s => s.Id == ticket.StatusId).Select(s => s.Category).FirstAsync(ct);
        var state = await db.Set<SlaTicketState>().AsNoTracking().FirstOrDefaultAsync(s => s.TicketId == ticket.Id, ct);
        var events = await db.Set<SlaEvent>().AsNoTracking()
            .Where(e => e.TicketId == ticket.Id && e.Kind != SlaEventKind.Reminder)
            .OrderBy(e => e.FiredAt).ThenBy(e => e.Kind).ThenBy(e => e.Target).ThenBy(e => e.Level)
            .Take(20)
            .ToListAsync(ct);
        return View(SlaSlots.Views + "SlaSidebar.cshtml", new SlaSidebarModel(ticket, category, state, events));
    }
}

/// <summary>A "Breached" marker on queue rows: text and icon, never color alone.</summary>
public sealed class SlaListBadgeViewComponent(SwitchboardDbContext db, TimeProvider time) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        if (model is not TicketListItem item)
        {
            return Content("");
        }
        var now = time.GetUtcNow();
        var resolved = item.StatusCategory is StatusCategory.Resolved or StatusCategory.Closed;
        // Facts the row already carries: a target met late.
        var missed = (item.FirstRespondedAt is { } responded && item.FirstResponseDueBy is { } frDue && responded > frDue)
            || (resolved && item.ResolvedAt is { } at && item.DueBy is { } due && at > due);
        // A running ticket past its date only counts once the monitor recorded the breach: a paused
        // ticket keeps its old date until it resumes, and that isn't a breach.
        if (!missed && ((item.FirstRespondedAt is null && item.FirstResponseDueBy < now) || (!resolved && item.DueBy < now)))
        {
            var id = item.Id;
            missed = await db.Set<SlaEvent>().AsNoTracking().AnyAsync(e => e.TicketId == id && e.Kind == SlaEventKind.Breach
                && (e.Target == SlaTargetKind.FirstResponse || db.Set<SlaTicketState>().Any(s => s.TicketId == id && s.Cycle == e.Cycle)),
                HttpContext.RequestAborted);
        }
        return missed ? View(SlaSlots.Views + "SlaListBadge.cshtml") : Content("");
    }
}

public sealed record SlaPerformanceModel(int FirstResponseMet, int FirstResponseTotal, int ResolutionMet, int ResolutionTotal);

/// <summary>Dashboard widget: share of first-response and resolution targets met in the last 7 days, over visible tickets (FR-ANL-8).</summary>
public sealed class SlaPerformanceViewComponent(SlaReportService reports, TimeProvider time) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        var now = time.GetUtcNow();
        var summary = await reports.PerformanceAsync(now.AddDays(-7), now, HttpContext.RequestAborted);
        return View(SlaSlots.Views + "SlaPerformance.cshtml", summary);
    }
}

/// <summary>Adds "SLA compliance" to the reports catalog for people who can view reports.</summary>
public sealed class SlaReportLinkViewComponent(IPermissionService permissions) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model) =>
        await permissions.HasAsync(SlaSlots.ReportsPrivilege, ct: HttpContext.RequestAborted)
            ? View(SlaSlots.Views + "SlaReportLink.cshtml")
            : Content("");
}
