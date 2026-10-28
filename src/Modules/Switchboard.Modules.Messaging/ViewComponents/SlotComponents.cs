using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.UI;

namespace Switchboard.Modules.Messaging.ViewComponents;

/// <summary><c>topbar.end</c>: the notification bell with the unread count. Agents only.</summary>
public sealed class NotificationBellViewComponent(NotificationInbox inbox) : ViewComponent
{
    // Typed parameters matter: with an object parameter MVC passes the slot's whole argument object instead of the model.
    public async Task<IViewComponentResult> InvokeAsync(ShellModel? model)
    {
        if (model is not { User.IsAgent: true })
        {
            return Content("");
        }
        return View("/Areas/Messaging/Components/NotificationBell.cshtml", await inbox.UnreadCountAsync());
    }
}

public sealed record PreferenceRow(NotificationEvent Event, ChannelPreference Choice);

/// <summary><c>profile.sections</c>: per-event channel choices for agents (FR-NOT-4).</summary>
public sealed class NotificationPreferencesViewComponent(PreferenceService preferences, IPermissionService permissions, IActorAccessor actors) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid model)
    {
        var userId = model;
        if (actors.Current.UserId != userId || (await permissions.CurrentAsync())?.IsAgent != true)
        {
            return Content("");
        }
        var choices = await preferences.GetAllAsync(userId);
        var rows = PreferenceService.AgentEvents.Select(e => new PreferenceRow(e, choices[e.Key])).ToList();
        return View("/Areas/Messaging/Components/NotificationPreferences.cshtml", rows);
    }
}

public sealed record DeliveryRow(MsgDelivery Delivery, string Recipient, string Notification);

public sealed record TicketDeliveries(Guid TicketId, IReadOnlyList<DeliveryRow> Rows, int Total);

/// <summary><c>ticket.tabs</c>: every notification sent about the ticket (FR-NOT-5). Agents who can see the ticket only.</summary>
public sealed class TicketNotificationsViewComponent(SwitchboardDbContext db, IPermissionService permissions) : ViewComponent
{
    private const int Shown = 100;

    public async Task<IViewComponentResult> InvokeAsync(Ticket? model)
    {
        if (model is not { } ticket)
        {
            return Content("");
        }
        var p = await permissions.CurrentAsync();
        if (p is null || !p.IsAgent || !PermissionService.CanAgentSee(ticket, p))
        {
            return Content("");
        }

        var query = db.Set<MsgDelivery>().AsNoTracking().Where(d => d.TicketId == ticket.Id);
        var total = await query.CountAsync();
        var deliveries = await query.OrderByDescending(d => d.CreatedAt).Take(Shown).ToListAsync();
        var userIds = deliveries.Where(d => d.RecipientUserId is not null).Select(d => d.RecipientUserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var rows = deliveries.Select(d => new DeliveryRow(
                d,
                d.Channel == DeliveryChannel.InApp && d.RecipientUserId is { } uid && names.TryGetValue(uid, out var name) ? name : d.Recipient,
                NotificationCatalog.Find(d.TemplateKey)?.Name ?? (d.TemplateKey == NotificationCatalog.SystemMail ? "System email" : d.TemplateKey)))
            .ToList();
        return View("/Areas/Messaging/Components/TicketNotifications.cshtml", new TicketDeliveries(ticket.Id, rows, total));
    }
}
