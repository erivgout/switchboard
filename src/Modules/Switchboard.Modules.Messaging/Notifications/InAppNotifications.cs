using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Messaging.Domain;

namespace Switchboard.Modules.Messaging.Notifications;

public sealed record NotificationDraft(
    Guid UserId,
    string Type,
    string Title,
    string? Body,
    string? Link,
    string? Reference,
    Guid? TicketId);

/// <summary>Creates in-app notifications and pushes them to the user's open browsers.</summary>
public sealed class InAppNotifier(SwitchboardDbContext db, ILiveNotifier live, TimeProvider time, ILogger<InAppNotifier> logger)
{
    public const string LiveType = "notification.created";

    /// <summary>
    /// Writes the notification and its delivery row in one transaction, so a retried event never
    /// creates a second one (INV-NOT-1). Returns false when it already existed.
    /// </summary>
    public async Task<bool> NotifyAsync(Guid? eventId, NotificationDraft draft, CancellationToken ct)
    {
        var key = DeliveryLog.Key(draft.UserId.ToString(), DeliveryChannel.InApp);
        if (eventId is { } id && await db.Set<MsgDelivery>().AnyAsync(d => d.EventId == id && d.Recipient == key && d.Channel == DeliveryChannel.InApp, ct))
        {
            return false;
        }

        var now = time.GetUtcNow();
        var notification = new MsgNotification
        {
            UserId = draft.UserId,
            Type = draft.Type,
            Title = Limit(draft.Title, 300),
            Body = draft.Body is null ? null : Limit(draft.Body, 1000),
            Link = draft.Link,
            Reference = draft.Reference,
            TicketId = draft.TicketId,
            EventId = eventId,
            CreatedAt = now,
        };
        db.Add(notification);
        db.Add(new MsgDelivery
        {
            EventId = eventId,
            Recipient = key,
            Channel = DeliveryChannel.InApp,
            RecipientUserId = draft.UserId,
            TicketId = draft.TicketId,
            TemplateKey = draft.Type,
            Subject = Limit(draft.Title, 500),
            Status = DeliveryStatus.Sent,
            IsAutomatic = true,
            Attempts = 1,
            CreatedAt = now,
            SentAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DeliveryLog.IsUniqueViolation(ex))
        {
            db.Entry(notification).State = EntityState.Detached;
            foreach (var entry in db.ChangeTracker.Entries<MsgDelivery>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }
            return false;
        }

        var unread = await db.Set<MsgNotification>().CountAsync(n => n.UserId == draft.UserId && n.ReadAt == null, ct);
        try
        {
            await live.ToUserAsync(draft.UserId, LiveType, new
            {
                id = notification.Id,
                notification.Title,
                notification.Body,
                notification.Link,
                notification.Reference,
                unread,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Live push is best effort; the bell catches up on the next page load.
            logger.LogDebug(ex, "Live push of notification {Id} failed", notification.Id);
        }
        return true;
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

public sealed record NotificationListItem(Guid Id, string Type, string Title, string? Body, string? Link, string? Reference, DateTimeOffset CreatedAt, bool IsRead);

/// <summary>The signed-in user's own notifications. Every query is scoped to the current user.</summary>
public sealed class NotificationInbox(SwitchboardDbContext db, IActorAccessor actors, TimeProvider time)
{
    private Guid CurrentUser => actors.Current is { UserId: { } id, IsInternal: false } ? id : throw new ForbiddenException();

    private IQueryable<MsgNotification> Mine()
    {
        var userId = CurrentUser;
        return db.Set<MsgNotification>().Where(n => n.UserId == userId);
    }

    public Task<int> UnreadCountAsync(CancellationToken ct = default) => Mine().CountAsync(n => n.ReadAt == null, ct);

    public async Task<IReadOnlyList<NotificationListItem>> ListAsync(bool unreadOnly, int skip, int take, CancellationToken ct = default)
    {
        var query = Mine();
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }
        return await query.AsNoTracking()
            .OrderByDescending(n => n.CreatedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 100))
            .Select(n => new NotificationListItem(n.Id, n.Type, n.Title, n.Body, n.Link, n.Reference, n.CreatedAt, n.ReadAt != null))
            .ToListAsync(ct);
    }

    public Task<int> CountAsync(bool unreadOnly, CancellationToken ct = default) =>
        unreadOnly ? Mine().CountAsync(n => n.ReadAt == null, ct) : Mine().CountAsync(ct);

    /// <summary>Marks one of the user's notifications read and returns where it points. Someone else's id behaves like a missing one.</summary>
    public async Task<string?> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var notification = await Mine().FirstOrDefaultAsync(n => n.Id == id, ct) ?? throw new NotFoundException();
        notification.ReadAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return notification.Link;
    }

    public async Task SetReadAsync(Guid id, bool read, CancellationToken ct = default)
    {
        var notification = await Mine().FirstOrDefaultAsync(n => n.Id == id, ct) ?? throw new NotFoundException();
        notification.ReadAt = read ? notification.ReadAt ?? time.GetUtcNow() : null;
        await db.SaveChangesAsync(ct);
    }

    public Task<int> MarkAllReadAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return Mine().Where(n => n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
    }
}

public sealed record ChannelPreference(bool Email, bool InApp);

/// <summary>Per-user channel choices for agent notifications (FR-NOT-4).</summary>
public sealed class PreferenceService(SwitchboardDbContext db)
{
    public static IReadOnlyList<NotificationEvent> AgentEvents { get; } =
        NotificationCatalog.All.Where(e => e.Audience == NotificationAudience.Agent).ToList();

    public async Task<IReadOnlyDictionary<string, ChannelPreference>> GetAllAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Set<MsgPreference>().AsNoTracking().Where(p => p.UserId == userId).ToDictionaryAsync(p => p.EventKey, ct);
        return AgentEvents.ToDictionary(
            e => e.Key,
            e => rows.TryGetValue(e.Key, out var row) ? new ChannelPreference(row.Email, row.InApp) : new ChannelPreference(e.DefaultEmail, e.DefaultInApp));
    }

    public async Task<ChannelPreference> GetAsync(Guid userId, string eventKey, CancellationToken ct = default)
    {
        var row = await db.Set<MsgPreference>().AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId && p.EventKey == eventKey, ct);
        if (row is not null)
        {
            return new ChannelPreference(row.Email, row.InApp);
        }
        var definition = NotificationCatalog.Get(eventKey);
        return new ChannelPreference(definition.DefaultEmail, definition.DefaultInApp);
    }

    public async Task SaveAsync(Guid userId, IReadOnlyDictionary<string, ChannelPreference> choices, CancellationToken ct = default)
    {
        var rows = await db.Set<MsgPreference>().Where(p => p.UserId == userId).ToDictionaryAsync(p => p.EventKey, ct);
        foreach (var e in AgentEvents)
        {
            if (!choices.TryGetValue(e.Key, out var choice))
            {
                continue;
            }
            if (!rows.TryGetValue(e.Key, out var row))
            {
                row = new MsgPreference { UserId = userId, EventKey = e.Key };
                db.Add(row);
            }
            row.Email = choice.Email;
            row.InApp = choice.InApp;
        }
        await db.SaveChangesAsync(ct);
    }
}
