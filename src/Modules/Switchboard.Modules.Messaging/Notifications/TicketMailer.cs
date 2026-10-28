using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Outbound;

namespace Switchboard.Modules.Messaging.Notifications;

public sealed record EmailRecipient(string Address, string? Name = null, Guid? UserId = null);

public sealed record TicketEmailRequest
{
    public required Guid? EventId { get; init; }
    public required string TemplateKey { get; init; }
    public required TicketFacts Facts { get; init; }
    public IReadOnlyList<EmailRecipient> To { get; init; } = [];
    public IReadOnlyList<EmailRecipient> Cc { get; init; } = [];
    public IReadOnlyList<EmailRecipient> Bcc { get; init; } = [];
    public MessageFacts? Message { get; init; }
    public string? ActorName { get; init; }
    /// <summary>The conversation this email carries; its attachments go with it and it gets the Message-ID.</summary>
    public Guid? ConversationId { get; init; }
    /// <summary>Requester-facing mail starts with the reply marker so quoted history can be cut from replies.</summary>
    public bool ReplyMarker { get; init; }
    public string? FromDisplayName { get; init; }
    public string? Footer { get; init; }
}

public enum MailOutcome
{
    Disabled,
    NothingToSend,
    Sent,
    Failed,
}

public sealed record TicketEmailResult(MailOutcome Outcome, string? MessageId = null, string? Error = null);

/// <summary>
/// Sends one ticket email: renders the template, drops recipients who must not get it (own addresses,
/// deactivated users, bounced or rate-limited addresses for automatic mail), dedupes per recipient,
/// threads it (Message-ID, In-Reply-To, References, subject token), sends and logs.
/// </summary>
public sealed partial class TicketMailer(
    SwitchboardDbContext db,
    TemplateService templates,
    DeliveryLog log,
    MailDispatcher dispatcher,
    OutboundSettingsStore settingsStore,
    IUserDirectory directory,
    IHtmlContent html,
    IFileStore files,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<TicketMailer> logger)
{
    private const long MaxAttachmentBytes = 15 * 1024 * 1024;

    public async Task<TicketEmailResult> SendAsync(TicketEmailRequest request, CancellationToken ct)
    {
        var facts = request.Facts;
        var ticket = facts.Ticket;
        var template = await templates.ResolveAsync(ticket.WorkspaceId, request.TemplateKey, ct);
        if (!template.IsEnabled)
        {
            return new TicketEmailResult(MailOutcome.Disabled);
        }
        var automatic = template.Event.Automatic;

        var recipientName = request.To.FirstOrDefault()?.Name;
        var values = TicketFactsLoader.Values(facts, request.Message, request.ActorName, recipientName, html);
        var subject = WithToken(TemplateRenderer.RenderSubject(template.Subject, values), ticket.DisplayId);
        var body = html.Sanitize(TemplateRenderer.RenderHtml(template.BodyHtml, values));

        // Who may get it.
        var own = await settingsStore.OwnAddressesAsync(ct);
        var now = time.GetUtcNow();
        var allowed = new List<EmailRecipient>();
        foreach (var r in request.To.Concat(request.Cc).Concat(request.Bcc).DistinctBy(r => r.Address.Trim().ToLowerInvariant()))
        {
            var claim = new DeliveryClaim(r.Address, r.UserId);
            var reason = await SuppressionReasonAsync(r, own, automatic, ticket.Id, now, ct);
            if (reason is not null)
            {
                await log.RecordSuppressedAsync(request.EventId, claim, DeliveryChannel.Email, request.TemplateKey, ticket.Id, subject, automatic, reason, ct);
                continue;
            }
            allowed.Add(r);
        }
        if (allowed.Count == 0)
        {
            return new TicketEmailResult(MailOutcome.NothingToSend);
        }

        var settings = await settingsStore.GetEffectiveAsync(ct);
        var messageId = MessageIds.New(settings.Domain);
        var claimed = await log.ClaimAsync(request.EventId, allowed.Select(r => new DeliveryClaim(r.Address, r.UserId)), DeliveryChannel.Email,
            request.TemplateKey, ticket.Id, subject, automatic, messageId, ct);
        if (claimed.Count == 0)
        {
            return new TicketEmailResult(MailOutcome.NothingToSend);
        }
        var claimedKeys = claimed.Select(c => c.Recipient).ToHashSet(StringComparer.Ordinal);
        bool Claimed(EmailRecipient r) => claimedKeys.Contains(DeliveryLog.Key(r.Address, DeliveryChannel.Email));

        var (inReplyTo, references) = await ThreadHeadersAsync(ticket.Id, ct);
        db.Add(new MsgMessageMap
        {
            MessageId = messageId,
            TicketId = ticket.Id,
            ConversationId = request.ConversationId,
            Direction = MessageDirection.Outbound,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        var message = new MimeMessage { MessageId = messageId, Subject = subject, Date = now };
        message.From.Add(new MailboxAddress(MessageIds.SingleLine(request.FromDisplayName ?? settings.FromName), settings.FromAddress));
        if (await ReplyToAsync(ticket, ct) is { } replyTo)
        {
            message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        }
        message.To.AddRange(request.To.Where(Claimed).Select(ToAddress));
        message.Cc.AddRange(request.Cc.Where(Claimed).Select(ToAddress));
        message.Bcc.AddRange(request.Bcc.Where(Claimed).Select(ToAddress));
        if (message.To.Count == 0 && message.Cc.Count == 0 && message.Bcc.Count == 0)
        {
            return new TicketEmailResult(MailOutcome.NothingToSend);
        }
        if (inReplyTo is not null)
        {
            message.InReplyTo = inReplyTo;
        }
        foreach (var reference in references)
        {
            message.References.Add(reference);
        }
        message.Headers.Add("X-Switchboard-Ticket", ticket.DisplayId);
        if (automatic)
        {
            // RFC 3834: mark automatic mail so other systems don't auto-reply to it (SR-EML-1).
            message.Headers.Add("Auto-Submitted", "auto-generated");
            message.Headers.Add("X-Auto-Response-Suppress", "All");
        }

        var builder = new BodyBuilder();
        body = await AttachAsync(builder, request.ConversationId, body, ct);
        body = EmailLayout.AbsolutizeLinks(body, facts.BaseUrl);
        var footer = request.Footer ?? DefaultFooter(facts);
        builder.HtmlBody = EmailLayout.Wrap(body, facts.OrganizationName, footer, request.ReplyMarker);
        builder.TextBody = (request.ReplyMarker ? EmailLayout.ReplyMarker + "\n\n" : "") + html.ToPlainText(body) + "\n\n" + footer;
        message.Body = builder.ToMessageBody();

        try
        {
            var result = await dispatcher.SendAsync(message, ct);
            await log.CompleteAsync(claimed, result.Status, result.Detail, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Email {Template} for {Ticket} failed: {Error}", request.TemplateKey, ticket.DisplayId, ex.Message);
            await log.CompleteAsync(claimed, DeliveryStatus.Failed, ex.Message, ct);
            return new TicketEmailResult(MailOutcome.Failed, messageId, ex.Message);
        }

        if (request.ConversationId is { } conversationId)
        {
            // Bypass the change tracker: threading data isn't a ticket change worth an activity entry.
            await db.TicketConversations.Where(c => c.Id == conversationId && c.EmailMessageId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.EmailMessageId, messageId), ct);
        }
        return new TicketEmailResult(MailOutcome.Sent, messageId);
    }

    private async Task<string?> SuppressionReasonAsync(EmailRecipient r, HashSet<string> own, bool automatic, Guid ticketId, DateTimeOffset now, CancellationToken ct)
    {
        var address = r.Address.Trim();
        if (!IUserDirectory.IsValidEmail(address))
        {
            return "Not a valid email address.";
        }
        if (own.Contains(address))
        {
            return "This is one of your own support addresses.";
        }
        var user = await directory.FindByEmailAsync(address, ct);
        if (user is { IsActive: false } || user is { IsForgotten: true })
        {
            return "This person is deactivated.";
        }
        if (!automatic)
        {
            return null;
        }
        var lower = address.ToLowerInvariant();
        var bounced = await db.Set<MsgBouncedAddress>().AsNoTracking()
            .AnyAsync(b => b.Address == lower && b.LastBouncedAt >= now.AddDays(-30), ct);
        if (bounced)
        {
            return "Mail to this address bounced recently.";
        }
        var perRecipient = configuration.GetValue("Switchboard:Mail:AutomaticPerRecipientPerHour", 10);
        var perTicket = configuration.GetValue("Switchboard:Mail:AutomaticPerTicketPerHour", 20);
        var (toRecipient, forTicket) = await log.AutomaticCountsAsync(address, ticketId, now.AddHours(-1), ct);
        if (toRecipient >= perRecipient)
        {
            return $"Rate limit: {perRecipient} automatic emails to this address in the last hour.";
        }
        if (forTicket >= perTicket)
        {
            return $"Rate limit: {perTicket} automatic emails about this ticket in the last hour.";
        }
        return null;
    }

    /// <summary>In-Reply-To is the newest known message on the ticket; References carries the first and the most recent ones.</summary>
    private async Task<(string? InReplyTo, IReadOnlyList<string> References)> ThreadHeadersAsync(Guid ticketId, CancellationToken ct)
    {
        var known = await db.Set<MsgMessageMap>().AsNoTracking()
            .Where(m => m.TicketId == ticketId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.MessageId, m.Direction })
            .ToListAsync(ct);
        if (known.Count == 0)
        {
            return (null, []);
        }
        var inReplyTo = known.LastOrDefault(k => k.Direction == MessageDirection.Inbound)?.MessageId ?? known[^1].MessageId;
        var references = known.Take(1).Concat(known.Skip(1).TakeLast(9)).Select(k => k.MessageId).Distinct().ToList();
        if (!references.Contains(inReplyTo))
        {
            references.Add(inReplyTo);
        }
        return (inReplyTo, references);
    }

    /// <summary>Replies go to the mailbox the ticket came in through, else the workspace's first mailbox.</summary>
    private async Task<string?> ReplyToAsync(Ticket ticket, CancellationToken ct)
    {
        var mailboxId = await db.Set<MsgMessageMap>().AsNoTracking()
            .Where(m => m.TicketId == ticket.Id && m.MailboxId != null)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.MailboxId)
            .FirstOrDefaultAsync(ct);
        var mailboxes = db.Set<MsgMailbox>().AsNoTracking().Where(m => m.IsEnabled);
        var address = mailboxId is { } id
            ? await mailboxes.Where(m => m.Id == id).Select(m => m.Address).FirstOrDefaultAsync(ct)
            : null;
        return address ?? await mailboxes.Where(m => m.WorkspaceId == ticket.WorkspaceId).OrderBy(m => m.CreatedAt).Select(m => m.Address).FirstOrDefaultAsync(ct);
    }

    /// <summary>Attaches the conversation's files. Images shown in the body travel as inline parts.</summary>
    private async Task<string> AttachAsync(BodyBuilder builder, Guid? conversationId, string body, CancellationToken ct)
    {
        if (conversationId is not { } id)
        {
            return body;
        }
        var list = await db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == AttachmentOwners.Conversation && a.OwnerId == id)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
        long total = 0;
        foreach (var attachment in list)
        {
            if (total + attachment.Size > MaxAttachmentBytes)
            {
                body += $"<p>Attachment too large to send by email: {System.Net.WebUtility.HtmlEncode(attachment.FileName)}. Find it on the request.</p>";
                continue;
            }
            await using var stream = await files.OpenReadAsync(attachment.StorageKey, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var contentType = ContentType.TryParse(attachment.ContentType, out var parsed) ? parsed : new ContentType("application", "octet-stream");
            var referenced = FileLink(attachment.Id).IsMatch(body);
            if (referenced && attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var resource = builder.LinkedResources.Add(attachment.FileName, buffer.ToArray(), contentType);
                resource.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId();
                body = FileLink(attachment.Id).Replace(body, $"cid:{resource.ContentId}");
            }
            else
            {
                builder.Attachments.Add(attachment.FileName, buffer.ToArray(), contentType);
            }
            total += attachment.Size;
        }
        return body;
    }

    private static Regex FileLink(Guid id) =>
        new($"(?:https?://[^\"'\\s]+)?/files/{id}(?:/[^\"'\\s]*)?", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));

    private static MailboxAddress ToAddress(EmailRecipient r) => new(MessageIds.SingleLine(r.Name ?? ""), r.Address.Trim());

    private static string DefaultFooter(TicketFacts facts) => $"Reference {facts.Ticket.DisplayId}";

    /// <summary>Every ticket email carries <c>[INC-1042]</c> so replies thread even when headers are lost (FR-CH-2).</summary>
    public static string WithToken(string subject, string displayId)
    {
        var token = $"[{displayId}]";
        return subject.Contains(token, StringComparison.OrdinalIgnoreCase) ? subject : $"{subject} {token}".Trim();
    }
}
