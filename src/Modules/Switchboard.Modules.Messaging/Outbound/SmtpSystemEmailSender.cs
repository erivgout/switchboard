using System.Text.RegularExpressions;
using MimeKit;
using Switchboard.Core.Services;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Notifications;

namespace Switchboard.Modules.Messaging.Outbound;

/// <summary>
/// The real <see cref="ISystemEmailSender"/> for every module. Sends through the configured SMTP
/// server, honors the non-production redirect, and logs each recipient in the delivery log.
/// </summary>
/// <remarks>
/// Optional headers: <c>X-Switchboard-Event</c> (a GUID; makes the send idempotent per recipient for that
/// event), <c>X-Switchboard-Template</c> (a short key shown in the delivery log), <c>X-Switchboard-Ticket-Id</c>
/// (a ticket GUID; shows the mail on that ticket's Notifications tab), <c>Auto-Submitted</c>,
/// <c>References</c>, <c>List-Unsubscribe</c> and any <c>X-</c> header. Other headers are ignored.
/// </remarks>
public sealed partial class SmtpSystemEmailSender(
    MailDispatcher dispatcher,
    OutboundSettingsStore settingsStore,
    DeliveryLog log,
    IHtmlContent html,
    TimeProvider time) : ISystemEmailSender
{
    public const string EventHeader = "X-Switchboard-Event";
    public const string TemplateHeader = "X-Switchboard-Template";
    public const string TicketHeader = "X-Switchboard-Ticket-Id";

    public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var settings = await settingsStore.GetEffectiveAsync(ct);
        var headers = email.Headers ?? new Dictionary<string, string>();
        Guid? eventId = headers.TryGetValue(EventHeader, out var ev) && Guid.TryParse(ev, out var g) ? g : null;
        Guid? ticketId = headers.TryGetValue(TicketHeader, out var tk) && Guid.TryParse(tk, out var t) ? t : null;
        var template = headers.TryGetValue(TemplateHeader, out var tpl) && tpl.Length is > 0 and <= 100 ? tpl : NotificationCatalog.SystemMail;
        var automatic = headers.ContainsKey("Auto-Submitted");

        var to = Parse(email.To);
        var cc = Parse(email.Cc);
        var bcc = Parse(email.Bcc);
        var messageId = MessageIds.New(settings.Domain);
        var subject = MessageIds.SingleLine(email.Subject);

        var claimed = await log.ClaimAsync(eventId, to.Concat(cc).Concat(bcc).Select(a => new DeliveryClaim(a.Address, null)),
            DeliveryChannel.Email, template, ticketId, subject, automatic, messageId, ct);
        if (claimed.Count == 0)
        {
            return messageId;
        }
        var keys = claimed.Select(c => c.Recipient).ToHashSet(StringComparer.Ordinal);
        bool Claimed(MailboxAddress a) => keys.Contains(DeliveryLog.Key(a.Address, DeliveryChannel.Email));

        var message = new MimeMessage { MessageId = messageId, Subject = subject, Date = time.GetUtcNow() };
        message.To.AddRange(to.Where(Claimed));
        message.Cc.AddRange(cc.Where(Claimed));
        message.Bcc.AddRange(bcc.Where(Claimed));
        if (email.ReplyTo is { Length: > 0 } replyTo && MailboxAddress.TryParse(replyTo, out var rt))
        {
            message.ReplyTo.Add(rt);
        }
        if (email.InReplyTo is { Length: > 0 } inReplyTo)
        {
            message.InReplyTo = MessageIds.Normalize(inReplyTo);
        }
        foreach (var (name, value) in headers)
        {
            if (IsPassThrough(name))
            {
                message.Headers.Replace(name, MessageIds.SingleLine(value));
            }
        }

        var body = html.Sanitize(email.HtmlBody);
        message.Body = new BodyBuilder
        {
            HtmlBody = EmailLayout.Wrap(body, settings.FromName, null, false),
            TextBody = email.TextBody ?? html.ToPlainText(body),
        }.ToMessageBody();

        try
        {
            var result = await dispatcher.SendAsync(message, ct);
            await log.CompleteAsync(claimed, result.Status, result.Detail, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await log.CompleteAsync(claimed, DeliveryStatus.Failed, ex.Message, ct);
            throw;
        }
        return messageId;
    }

    private static List<MailboxAddress> Parse(IReadOnlyList<string>? addresses)
    {
        var list = new List<MailboxAddress>();
        foreach (var raw in addresses ?? [])
        {
            if (!InternetAddressList.TryParse(MessageIds.SingleLine(raw), out var parsed))
            {
                throw new Core.Security.ValidationException("to", $"{raw} isn't a valid email address.");
            }
            list.AddRange(parsed.Mailboxes);
        }
        return list;
    }

    private static bool IsPassThrough(string name) =>
        HeaderName().IsMatch(name)
        && name is not (EventHeader or TemplateHeader or TicketHeader)
        && (name.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
            || name is "Auto-Submitted" or "References" or "List-Unsubscribe" or "List-Id" or "Precedence");

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderName();
}
