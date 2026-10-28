using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Core.Tickets;

/// <summary>
/// Hourly housekeeping: auto-close resolved tickets (FR-TKT-26), purge expired trash (FR-TKT-22),
/// and remove abandoned uploads.
/// </summary>
internal sealed class TicketMaintenanceJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<TicketMaintenanceJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Ticket maintenance failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var tickets = sp.GetRequiredService<ITicketService>();
        var actors = sp.GetRequiredService<IActorAccessor>();
        var store = sp.GetRequiredService<IFileStore>();
        var now = time.GetUtcNow();
        using var _ = actors.BeginScope(Actor.System("Ticket housekeeping"));

        foreach (var workspace in await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).ToListAsync(ct))
        {
            var days = await settings.GetAsync(SettingKeys.AutoCloseDays, 3, workspace.Id, ct);
            if (days > 0)
            {
                var closed = await db.TicketStatuses.AsNoTracking()
                    .Where(s => s.WorkspaceId == workspace.Id && s.Category == StatusCategory.Closed)
                    .OrderBy(s => s.Order).Select(s => s.Id).FirstOrDefaultAsync(ct);
                var cutoff = now.AddDays(-days);
                var due = await db.Tickets.AsNoTracking()
                    .Where(t => t.WorkspaceId == workspace.Id && t.Status!.Category == StatusCategory.Resolved
                        && t.ResolvedAt != null && t.ResolvedAt < cutoff && t.DeletedAt == null)
                    .Select(t => t.Id).Take(500).ToListAsync(ct);
                foreach (var id in due)
                {
                    await tickets.UpdateAsync(id, new TicketPatch { StatusId = closed }, ct: ct);
                }
            }

            var retention = await settings.GetAsync(SettingKeys.TrashRetentionDays, 30, workspace.Id, ct);
            if (retention > 0)
            {
                var purgeBefore = now.AddDays(-retention);
                var expired = await db.Tickets.AsNoTracking()
                    .Where(t => t.WorkspaceId == workspace.Id && t.DeletedAt != null && t.DeletedAt < purgeBefore && t.PurgedAt == null)
                    .Select(t => t.Id).Take(200).ToListAsync(ct);
                foreach (var id in expired)
                {
                    await tickets.PurgeAsync(id, ct);
                }
            }
        }

        var abandoned = await db.Attachments
            .Where(a => a.OwnerType == AttachmentOwners.Pending && a.CreatedAt < now.AddHours(-24))
            .Take(500).ToListAsync(ct);
        db.Attachments.RemoveRange(abandoned);
        await db.SaveChangesAsync(ct);
        foreach (var a in abandoned)
        {
            await store.DeleteAsync(a.StorageKey, ct);
        }
    }
}

/// <summary>Ticket and conversation attachments follow ticket visibility; requesters only see public conversations.</summary>
internal sealed class TicketAttachmentPolicy(SwitchboardDbContext db, IPermissionService permissions, string ownerType) : IAttachmentAccessPolicy
{
    public string OwnerType => ownerType;

    public async Task<bool> CanReadAsync(Attachment attachment, CancellationToken ct)
    {
        if (attachment.OwnerId is not { } ownerId)
        {
            return false;
        }

        Guid ticketId;
        var isPublic = true;
        if (ownerType == AttachmentOwners.Conversation)
        {
            var conversation = await db.TicketConversations.AsNoTracking()
                .Where(c => c.Id == ownerId)
                .Select(c => new { c.TicketId, c.IsPrivate, c.Kind, c.DeletedAt })
                .FirstOrDefaultAsync(ct);
            if (conversation is null)
            {
                return false;
            }
            ticketId = conversation.TicketId;
            isPublic = !conversation.IsPrivate && conversation.Kind != ConversationKind.Forward && conversation.DeletedAt is null;
        }
        else
        {
            ticketId = ownerId;
        }

        if (await (await permissions.AgentVisibleTicketsAsync(ct)).AnyAsync(t => t.Id == ticketId, ct))
        {
            return true;
        }
        return isPublic && await (await permissions.RequesterVisibleTicketsAsync(ct)).AnyAsync(t => t.Id == ticketId, ct);
    }
}
