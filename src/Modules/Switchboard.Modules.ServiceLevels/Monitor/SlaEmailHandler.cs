using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Events;

namespace Switchboard.Modules.ServiceLevels.Monitor;

public static class SlaEmailSettings
{
    /// <summary>Email reminder, breach and escalation recipients (default on). Turn off once the messaging module sends these.</summary>
    public const string Enabled = "sla.email_notifications";
}

/// <summary>
/// Emails the agents an SLA notification names, after commit. Each recipient must still be able to see
/// the ticket, so an email never shows more than its reader may see (SR-AUTHZ-5).
/// </summary>
internal sealed class SlaEmailHandler(
    SwitchboardDbContext db,
    IPermissionService permissions,
    ISettingsService settings,
    ISystemEmailSender email,
    IOptions<SwitchboardOptions> options) :
    IEventHandler<SlaReminderDue>,
    IEventHandler<SlaBreached>,
    IEventHandler<SlaEscalated>
{
    public Task HandleAsync(SlaReminderDue e, CancellationToken ct) =>
        SendAsync(e.TicketId, e.WorkspaceId, e.NotifyUserIds,
            t => $"Reminder: {SlaText.Target(e.Target)} due soon on {t.DisplayId}",
            $"The {SlaText.Target(e.Target)} target for this ticket is due in {e.MinutesBefore} minutes.", ct);

    public Task HandleAsync(SlaBreached e, CancellationToken ct) =>
        SendAsync(e.TicketId, e.WorkspaceId, e.NotifyUserIds,
            t => $"Missed {SlaText.Target(e.Target)} target on {t.DisplayId}",
            $"This ticket missed its {SlaText.Target(e.Target)} target.", ct);

    public Task HandleAsync(SlaEscalated e, CancellationToken ct) =>
        SendAsync(e.TicketId, e.WorkspaceId, e.NotifyUserIds,
            t => $"Escalation level {e.Level}: {t.DisplayId} missed its {SlaText.Target(e.Target)} target",
            $"This ticket missed its {SlaText.Target(e.Target)} target and has reached escalation level {e.Level}.", ct);

    private async Task SendAsync(Guid ticketId, Guid workspaceId, IReadOnlyList<Guid> userIds, Func<Ticket, string> subject, string lead, CancellationToken ct)
    {
        if (userIds.Count == 0 || !await settings.GetAsync(SlaEmailSettings.Enabled, true, workspaceId, ct))
        {
            return;
        }
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId && t.PurgedAt == null, ct);
        if (ticket is null)
        {
            return;
        }

        var recipients = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.IsActive && u.Email != null)
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(ct);
        var to = new List<string>();
        foreach (var r in recipients)
        {
            var p = await permissions.ForUserAsync(r.Id, ct);
            if (p is not null && PermissionService.CanAgentSee(ticket, p))
            {
                to.Add(r.Email!);
            }
        }
        if (to.Count == 0)
        {
            return;
        }

        var url = $"{options.Value.BaseUrl.TrimEnd('/')}/tickets/{ticket.Number}";
        var title = $"{ticket.DisplayId}: {ticket.Subject}";
        var html = $"""
            <p>{WebUtility.HtmlEncode(lead)}</p>
            <p><strong>{WebUtility.HtmlEncode(title)}</strong></p>
            <p><a href="{WebUtility.HtmlEncode(url)}">Open the ticket</a></p>
            """;
        var text = $"{lead}\n\n{title}\n{url}\n";
        // One message per recipient, so addresses aren't shared.
        foreach (var address in to.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await email.SendAsync(new OutboundEmail([address], subject(ticket), html, text), ct);
        }
    }
}
