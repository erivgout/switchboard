using System.Net;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Switchboard.Core.Data;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Outbound;

namespace Switchboard.Modules.Messaging.Inbound;

/// <summary>
/// Bounce processing (SR-EML-4, FR-NOT-5): reads the delivery status report, marks the delivery as
/// bounced, remembers permanently failing addresses so automatic mail skips them, and tells the
/// ticket's agents with a private note.
/// </summary>
public sealed class BounceProcessor(SwitchboardDbContext db, ITicketService tickets, TimeProvider time)
{
    public sealed record Failure(string Address, bool Permanent, string? Diagnostic);

    public async Task<string> ProcessAsync(MimeMessage message, MsgInboundMessage record, CancellationToken ct)
    {
        var originalIds = OriginalMessageIds(message);
        var failures = Failures(message);
        if (failures.Count == 0)
        {
            return "A delivery failure notice, but it didn't say which address failed.";
        }

        var now = time.GetUtcNow();
        foreach (var failure in failures)
        {
            var address = failure.Address.ToLowerInvariant();
            var deliveries = await db.Set<MsgDelivery>()
                .Where(d => d.Channel == DeliveryChannel.Email && d.Recipient == address && d.MessageId != null && originalIds.Contains(d.MessageId))
                .ToListAsync(ct);
            foreach (var delivery in deliveries)
            {
                delivery.Status = DeliveryStatus.Bounced;
                delivery.Error = Limit(failure.Diagnostic ?? "The recipient's mail server rejected it.", 1000);
            }
            if (failure.Permanent)
            {
                var bounced = await db.Set<MsgBouncedAddress>().FirstOrDefaultAsync(b => b.Address == address, ct);
                if (bounced is null)
                {
                    db.Add(new MsgBouncedAddress { Address = address, Count = 1, FirstBouncedAt = now, LastBouncedAt = now, Reason = Limit(failure.Diagnostic, 500) });
                }
                else
                {
                    bounced.Count++;
                    bounced.LastBouncedAt = now;
                    bounced.Reason = Limit(failure.Diagnostic, 500) ?? bounced.Reason;
                }
            }
        }
        await db.SaveChangesAsync(ct);

        var ticketId = await db.Set<MsgMessageMap>().AsNoTracking()
            .Where(m => originalIds.Contains(m.MessageId))
            .Select(m => (Guid?)m.TicketId)
            .FirstOrDefaultAsync(ct);
        var addresses = string.Join(", ", failures.Select(f => f.Address));
        if (ticketId is { } id)
        {
            record.TicketId = id;
            var detail = failures.Select(f => f.Diagnostic).FirstOrDefault(d => d is not null);
            await tickets.AddNoteAsync(id, new NoteCommand
            {
                BodyHtml = $"<p><strong>Email to {WebUtility.HtmlEncode(addresses)} couldn't be delivered.</strong></p>"
                    + (detail is null ? "" : $"<p>The mail server said: {WebUtility.HtmlEncode(detail)}</p>"),
                IsPrivate = true,
            }, ct);
        }
        return $"Delivery to {addresses} failed{(failures.Any(f => f.Permanent) ? "; automatic mail to that address is paused for 30 days" : "")}.";
    }

    public static List<string> OriginalMessageIds(MimeMessage message)
    {
        var ids = new List<string>();
        if (message.InReplyTo is { Length: > 0 } irt)
        {
            ids.Add(MessageIds.Normalize(irt));
        }
        ids.AddRange(message.References.Select(MessageIds.Normalize));
        foreach (var part in message.BodyParts.OfType<MessagePart>())
        {
            if (part.Message?.MessageId is { Length: > 0 } original)
            {
                ids.Add(MessageIds.Normalize(original));
            }
        }
        return ids.Where(i => i.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    public static List<Failure> Failures(MimeMessage message)
    {
        var failures = new List<Failure>();
        foreach (var status in message.BodyParts.OfType<MessageDeliveryStatus>())
        {
            foreach (var group in status.StatusGroups.Skip(1))
            {
                var recipient = group["Final-Recipient"] ?? group["Original-Recipient"];
                var action = group["Action"]?.Trim().ToLowerInvariant();
                if (recipient is null || action != "failed")
                {
                    continue;
                }
                var address = recipient.Contains(';') ? recipient[(recipient.IndexOf(';') + 1)..].Trim() : recipient.Trim();
                if (!Core.Services.IUserDirectory.IsValidEmail(address))
                {
                    continue;
                }
                var code = group["Status"]?.Trim() ?? "";
                var diagnostic = group["Diagnostic-Code"]?.Trim();
                if (diagnostic is not null && diagnostic.Contains(';'))
                {
                    diagnostic = diagnostic[(diagnostic.IndexOf(';') + 1)..].Trim();
                }
                failures.Add(new Failure(address, code.StartsWith('5'), diagnostic is { Length: > 0 } ? diagnostic : code.Length > 0 ? $"Status {code}" : null));
            }
        }
        return failures.DistinctBy(f => f.Address.ToLowerInvariant()).ToList();
    }

    private static string? Limit(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
