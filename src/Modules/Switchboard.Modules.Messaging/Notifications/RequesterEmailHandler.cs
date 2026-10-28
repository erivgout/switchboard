using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Modules.Messaging.Domain;

namespace Switchboard.Modules.Messaging.Notifications;

/// <summary>
/// Email to requesters and third parties: acknowledgements, agent replies, forwards, public notes
/// and resolved/closed/pending notices. Runs after commit, at least once; the delivery log dedupes.
/// Requesters never get private-note content (FR-NOT-6, INV-TKT-7): only public kinds are emailed.
/// </summary>
public sealed class RequesterEmailHandler(SwitchboardDbContext db, TicketFactsLoader loader, TicketMailer mailer) :
    IEventHandler<TicketCreated>,
    IEventHandler<TicketConversationAdded>,
    IEventHandler<TicketStatusChanged>
{
    public async Task HandleAsync(TicketCreated e, CancellationToken ct)
    {
        var facts = await LoadAsync(e.TicketId, ct);
        if (facts is null || !CanEmail(facts.Requester))
        {
            return;
        }

        var byRequester = e.Actor is { Kind: ActorKind.User } a && (a.UserId == facts.Ticket.RequesterId || a.UserId == facts.Ticket.RequestedForId);
        var byEmail = e.Actor?.Channel == Channel.Email;
        if (!byRequester && !byEmail)
        {
            return;
        }
        if (byEmail && await SuppressedByInboundAsync(e.TicketId, ct))
        {
            // Mail that was automatic or failed sender authentication never gets an automatic answer (SR-EML-1, SR-EML-3).
            return;
        }

        await SendAsync(new TicketEmailRequest
        {
            EventId = e.EventId,
            TemplateKey = NotificationCatalog.RequesterAcknowledgement,
            Facts = facts,
            To = [Requester(facts)],
            ReplyMarker = true,
        }, ct);
    }

    public async Task HandleAsync(TicketConversationAdded e, CancellationToken ct)
    {
        if (!e.ByAgent || e.Kind is not (ConversationKind.AgentReply or ConversationKind.Forward or ConversationKind.Note))
        {
            return;
        }
        if (e.Kind == ConversationKind.Note && e.IsPrivate)
        {
            return;
        }

        var facts = await LoadAsync(e.TicketId, ct);
        var conversation = await db.TicketConversations.AsNoTracking().Include(c => c.Author)
            .FirstOrDefaultAsync(c => c.Id == e.ConversationId && c.DeletedAt == null, ct);
        if (facts is null || conversation is null)
        {
            return;
        }

        var author = conversation.Author;
        var shownAuthor = author is not null && facts.ShowAgentNames ? author.DisplayName : null;
        var fromName = shownAuthor is null ? null : $"{shownAuthor} ({facts.OrganizationName})";
        var message = new MessageFacts(conversation.BodyHtml, shownAuthor, facts.ShowAgentNames ? author : null);

        var request = e.Kind switch
        {
            ConversationKind.AgentReply => new TicketEmailRequest
            {
                EventId = e.EventId,
                TemplateKey = NotificationCatalog.RequesterAgentReply,
                Facts = facts,
                To = conversation.To.Select(a => Recipient(a, facts)).ToList(),
                Cc = conversation.Cc.Select(a => Recipient(a, facts)).ToList(),
                Bcc = conversation.Bcc.Select(a => Recipient(a, facts)).ToList(),
                Message = message,
                ActorName = author?.DisplayName,
                ConversationId = conversation.Id,
                ReplyMarker = true,
                FromDisplayName = fromName,
            },
            ConversationKind.Forward => new TicketEmailRequest
            {
                EventId = e.EventId,
                TemplateKey = NotificationCatalog.Forward,
                Facts = facts,
                To = conversation.To.Select(a => new EmailRecipient(a)).ToList(),
                Cc = conversation.Cc.Select(a => new EmailRecipient(a)).ToList(),
                Message = new MessageFacts(conversation.BodyHtml, author?.DisplayName, author),
                ActorName = author?.DisplayName,
                ConversationId = conversation.Id,
                FromDisplayName = author is null ? null : $"{author.DisplayName} ({facts.OrganizationName})",
                Footer = $"Forwarded from {facts.Ticket.DisplayId}. Replies to this email are added to the ticket as private notes.",
            },
            _ => CanEmail(facts.Requester)
                ? new TicketEmailRequest
                {
                    EventId = e.EventId,
                    TemplateKey = NotificationCatalog.RequesterPublicNote,
                    Facts = facts,
                    To = [Requester(facts)],
                    Message = message,
                    ActorName = author?.DisplayName,
                    ConversationId = conversation.Id,
                    ReplyMarker = true,
                    FromDisplayName = fromName,
                }
                : null,
        };
        if (request is not null)
        {
            await SendAsync(request, ct);
        }
    }

    public async Task HandleAsync(TicketStatusChanged e, CancellationToken ct)
    {
        if (e.FromCategory == e.ToCategory)
        {
            return;
        }
        var key = e.ToCategory switch
        {
            StatusCategory.Resolved => NotificationCatalog.RequesterResolved,
            StatusCategory.Closed => NotificationCatalog.RequesterClosed,
            StatusCategory.Pending => NotificationCatalog.RequesterPending,
            _ => null,
        };
        if (key is null)
        {
            return;
        }
        var facts = await LoadAsync(e.TicketId, ct);
        if (facts is null || !CanEmail(facts.Requester) || e.Actor?.UserId == facts.Requester.Id)
        {
            // The requester made this change themselves; they don't need telling.
            return;
        }
        await SendAsync(new TicketEmailRequest
        {
            EventId = e.EventId,
            TemplateKey = key,
            Facts = facts,
            To = [Requester(facts)],
            ActorName = e.Actor?.DisplayName,
            ReplyMarker = true,
        }, ct);
    }

    /// <summary>Trashed, spam and merged-away tickets get no requester mail.</summary>
    private async Task<TicketFacts?> LoadAsync(Guid ticketId, CancellationToken ct)
    {
        var facts = await loader.LoadAsync(ticketId, ct);
        return facts is null || facts.Ticket.IsSpam || facts.Ticket.DeletedAt is not null || facts.Ticket.MergedIntoId is not null ? null : facts;
    }

    private async Task SendAsync(TicketEmailRequest request, CancellationToken ct)
    {
        var result = await mailer.SendAsync(request, ct);
        if (result.Outcome == MailOutcome.Failed)
        {
            // Let the outbox retry with backoff; delivered recipients are skipped next time.
            throw new InvalidOperationException($"Email {request.TemplateKey} for {request.Facts.Ticket.DisplayId} wasn't sent: {result.Error}");
        }
    }

    private async Task<bool> SuppressedByInboundAsync(Guid ticketId, CancellationToken ct)
    {
        var messageIds = db.Set<MsgMessageMap>().Where(m => m.TicketId == ticketId && m.Direction == MessageDirection.Inbound).Select(m => m.MessageId);
        return await db.Set<MsgInboundMessage>().AnyAsync(i => (i.TicketId == ticketId || messageIds.Contains(i.MessageId)) && i.SuppressAutoResponses, ct);
    }

    private static bool CanEmail(User user) => user.IsActive && !user.IsForgotten && !string.IsNullOrWhiteSpace(user.Email);

    private static EmailRecipient Requester(TicketFacts facts) =>
        new(facts.Requester.Email!, facts.Requester.DisplayName, facts.Requester.Id);

    private static EmailRecipient Recipient(string address, TicketFacts facts) =>
        string.Equals(address, facts.Requester.Email, StringComparison.OrdinalIgnoreCase) ? Requester(facts) : new EmailRecipient(address);
}
