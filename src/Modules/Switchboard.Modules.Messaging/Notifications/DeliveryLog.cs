using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Modules.Messaging.Domain;

namespace Switchboard.Modules.Messaging.Notifications;

/// <summary>A delivery about to be attempted.</summary>
public sealed record DeliveryClaim(string Recipient, Guid? RecipientUserId);

/// <summary>
/// The delivery log and its dedupe rule (INV-NOT-1). Before sending, a <see cref="DeliveryStatus.Pending"/>
/// row is claimed per (event, recipient, channel). On a retry of the same event:
/// <list type="bullet">
/// <item>finished rows (sent, redirected, logged, suppressed, bounced) are skipped;</item>
/// <item>failed rows are tried again;</item>
/// <item>rows still pending were interrupted mid-send, so they become <see cref="DeliveryStatus.Unconfirmed"/> and are never resent: nobody gets the same message twice.</item>
/// </list>
/// </summary>
public sealed class DeliveryLog(SwitchboardDbContext db, TimeProvider time)
{
    private DbSet<MsgDelivery> Deliveries => db.Set<MsgDelivery>();

    /// <summary>Claims rows for the recipients that still need this message. Saves.</summary>
    public async Task<IReadOnlyList<MsgDelivery>> ClaimAsync(
        Guid? eventId, IEnumerable<DeliveryClaim> recipients, DeliveryChannel channel, string templateKey,
        Guid? ticketId, string subject, bool automatic, string? messageId, CancellationToken ct)
    {
        var wanted = recipients.DistinctBy(r => Key(r.Recipient, channel)).ToList();
        var claimed = new List<MsgDelivery>();
        var now = time.GetUtcNow();

        Dictionary<string, MsgDelivery> existing = [];
        if (eventId is { } id)
        {
            var keys = wanted.Select(r => Key(r.Recipient, channel)).ToList();
            existing = (await Deliveries.Where(d => d.EventId == id && d.Channel == channel && keys.Contains(d.Recipient)).ToListAsync(ct))
                .ToDictionary(d => d.Recipient, StringComparer.Ordinal);
        }

        foreach (var r in wanted)
        {
            var key = Key(r.Recipient, channel);
            if (existing.TryGetValue(key, out var row))
            {
                switch (row.Status)
                {
                    case DeliveryStatus.Failed:
                        row.Status = DeliveryStatus.Pending;
                        row.Attempts++;
                        row.Error = null;
                        row.Subject = Limit(subject, 500);
                        row.MessageId = messageId;
                        claimed.Add(row);
                        break;
                    case DeliveryStatus.Pending:
                        row.Status = DeliveryStatus.Unconfirmed;
                        row.Error = "Sending was interrupted, so it may not have arrived. Not sent again, so nobody gets it twice.";
                        break;
                }
                continue;
            }

            var delivery = new MsgDelivery
            {
                EventId = eventId,
                Recipient = key,
                Channel = channel,
                RecipientUserId = r.RecipientUserId,
                TicketId = ticketId,
                TemplateKey = templateKey,
                Subject = Limit(subject, 500),
                Status = DeliveryStatus.Pending,
                IsAutomatic = automatic,
                Attempts = 1,
                MessageId = messageId,
                CreatedAt = now,
            };
            Deliveries.Add(delivery);
            claimed.Add(delivery);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent run claimed the same rows; it owns them.
            foreach (var entry in db.ChangeTracker.Entries<MsgDelivery>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }
            return [];
        }
        return claimed;
    }

    /// <summary>Records a deliberate non-delivery once per event, recipient and channel.</summary>
    public async Task RecordSuppressedAsync(Guid? eventId, DeliveryClaim recipient, DeliveryChannel channel, string templateKey, Guid? ticketId, string subject, bool automatic, string reason, CancellationToken ct)
    {
        var key = Key(recipient.Recipient, channel);
        if (eventId is { } id && await Deliveries.AnyAsync(d => d.EventId == id && d.Recipient == key && d.Channel == channel, ct))
        {
            return;
        }
        Deliveries.Add(new MsgDelivery
        {
            EventId = eventId,
            Recipient = key,
            Channel = channel,
            RecipientUserId = recipient.RecipientUserId,
            TicketId = ticketId,
            TemplateKey = templateKey,
            Subject = Limit(subject, 500),
            Status = DeliveryStatus.Suppressed,
            Error = Limit(reason, 1000),
            IsAutomatic = automatic,
            CreatedAt = time.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            foreach (var entry in db.ChangeTracker.Entries<MsgDelivery>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    public async Task CompleteAsync(IEnumerable<MsgDelivery> rows, DeliveryStatus status, string? error, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var row in rows)
        {
            row.Status = status;
            row.Error = error is null ? null : Limit(error, 1000);
            if (status is DeliveryStatus.Sent or DeliveryStatus.Redirected or DeliveryStatus.Logged)
            {
                row.SentAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>How many automatic emails went to this address, or about this ticket, since <paramref name="since"/>.</summary>
    public async Task<(int ToRecipient, int ForTicket)> AutomaticCountsAsync(string address, Guid? ticketId, DateTimeOffset since, CancellationToken ct)
    {
        var key = Key(address, DeliveryChannel.Email);
        var counted = new[] { DeliveryStatus.Sent, DeliveryStatus.Redirected, DeliveryStatus.Logged, DeliveryStatus.Pending, DeliveryStatus.Unconfirmed };
        var toRecipient = await Deliveries.CountAsync(d => d.Recipient == key && d.Channel == DeliveryChannel.Email && d.IsAutomatic
            && d.CreatedAt >= since && counted.Contains(d.Status), ct);
        var forTicket = ticketId is { } t
            ? await Deliveries.CountAsync(d => d.TicketId == t && d.Channel == DeliveryChannel.Email && d.IsAutomatic
                && d.CreatedAt >= since && counted.Contains(d.Status), ct)
            : 0;
        return (toRecipient, forTicket);
    }

    public static string Key(string recipient, DeliveryChannel channel) =>
        channel == DeliveryChannel.Email ? recipient.Trim().ToLowerInvariant() : recipient.Trim();

    internal static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
