using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Messaging.Notifications;

/// <summary>
/// Agent notifications (FR-NOT-1): assigned to me or my group, new ticket in my group, requester
/// replied on my ticket, mentioned in a note. In-app and email, per the agent's preferences.
/// </summary>
public sealed class AgentNotificationHandler(
    SwitchboardDbContext db,
    TicketFactsLoader loader,
    AgentNotifier notifier) :
    IEventHandler<TicketAssigned>,
    IEventHandler<TicketConversationAdded>,
    IEventHandler<AgentsMentioned>
{
    public async Task HandleAsync(TicketAssigned e, CancellationToken ct)
    {
        var facts = await loader.LoadAsync(e.TicketId, ct);
        if (facts is null || facts.Ticket.DeletedAt is not null || facts.Ticket.IsSpam)
        {
            return;
        }
        var actorId = e.Actor?.UserId;
        var by = ActorName(e.Actor);
        var actorForEmail = by ?? EmailActorName(e.Actor);

        if (e.ToAgentId is { } agentId && agentId != e.FromAgentId)
        {
            if (agentId != actorId)
            {
                await notifier.NotifyAsync(e.EventId, agentId, NotificationCatalog.AgentAssigned, facts,
                    by is null ? "Assigned to you" : $"Assigned to you by {by}", null, actorForEmail, ct);
            }
            return;
        }

        if (e.ToAgentId is null && e.ToGroupId is { } groupId && groupId != e.FromGroupId)
        {
            var isNew = e.FromGroupId is null && e.FromAgentId is null;
            var key = isNew ? NotificationCatalog.AgentNewInGroup : NotificationCatalog.AgentGroupAssigned;
            var groupName = facts.Group?.Name ?? "your group";
            var members = await db.AgentGroupMembers.AsNoTracking().Where(m => m.GroupId == groupId).Select(m => m.UserId).ToListAsync(ct);
            foreach (var member in members.Where(m => m != actorId))
            {
                await notifier.NotifyAsync(e.EventId, member, key, facts,
                    isNew ? $"New in {groupName}" : $"Assigned to {groupName}", null, actorForEmail, ct);
            }
        }
    }

    public async Task HandleAsync(TicketConversationAdded e, CancellationToken ct)
    {
        if (e.ByAgent || e.Kind != ConversationKind.RequesterMessage)
        {
            return;
        }
        var facts = await loader.LoadAsync(e.TicketId, ct);
        if (facts?.Ticket.AgentId is not { } agentId || agentId == e.Actor?.UserId || facts.Ticket.IsSpam)
        {
            return;
        }
        var conversation = await db.TicketConversations.AsNoTracking().Include(c => c.Author).FirstOrDefaultAsync(c => c.Id == e.ConversationId, ct);
        if (conversation is null)
        {
            return;
        }
        var author = conversation.Author?.DisplayName ?? conversation.FromEmail ?? facts.Requester.DisplayName;
        await notifier.NotifyAsync(e.EventId, agentId, NotificationCatalog.AgentRequesterReplied, facts,
            $"{author} replied", new MessageFacts(conversation.BodyHtml, author, conversation.Author), author, ct);
    }

    public async Task HandleAsync(AgentsMentioned e, CancellationToken ct)
    {
        var facts = await loader.LoadAsync(e.TicketId, ct);
        if (facts is null)
        {
            return;
        }
        var conversation = await db.TicketConversations.AsNoTracking().Include(c => c.Author).FirstOrDefaultAsync(c => c.Id == e.ConversationId, ct);
        var by = ActorName(e.Actor) ?? conversation?.Author?.DisplayName;
        var actorForEmail = by ?? EmailActorName(e.Actor);
        var message = conversation is null ? null : new MessageFacts(conversation.BodyHtml, conversation.Author?.DisplayName, conversation.Author);
        foreach (var userId in e.UserIds.Distinct().Where(u => u != e.Actor?.UserId))
        {
            await notifier.NotifyAsync(e.EventId, userId, NotificationCatalog.AgentMentioned, facts,
                by is null ? "You were mentioned" : $"{by} mentioned you", message, actorForEmail, ct);
        }
    }

    /// <summary>Named people only; system and automation changes read as plain "Assigned to you".</summary>
    private static string? ActorName(ActorSnapshot? actor) =>
        actor is { Kind: ActorKind.User or ActorKind.McpClient, UserId: not null } ? actor.DisplayName : null;

    /// <summary>Emails always name someone: an automation by its name, anything else as Switchboard.</summary>
    private static string EmailActorName(ActorSnapshot? actor) =>
        actor is { Kind: ActorKind.Automation, DisplayName.Length: > 0 } ? actor.DisplayName : "Switchboard";
}

/// <summary>Sends one agent notification on the channels the agent chose, if they can see the ticket.</summary>
public sealed class AgentNotifier(
    SwitchboardDbContext db,
    IPermissionService permissions,
    PreferenceService preferences,
    TemplateService templates,
    InAppNotifier inApp,
    TicketMailer mailer)
{
    public async Task NotifyAsync(Guid eventId, Guid userId, string eventKey, TicketFacts facts, string title, MessageFacts? message, string? actorName, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        // FR-NOT-6: never deactivated users. Agents only hear about tickets they can open.
        if (user is null || !user.IsActive || user.IsForgotten)
        {
            return;
        }
        var access = await permissions.ForUserAsync(userId, ct);
        if (access is null || !access.IsAgent || !PermissionService.CanAgentSee(facts.Ticket, access))
        {
            return;
        }
        var template = await templates.ResolveAsync(facts.Ticket.WorkspaceId, eventKey, ct);
        if (!template.IsEnabled)
        {
            return;
        }

        var choice = await preferences.GetAsync(userId, eventKey, ct);
        if (choice.InApp)
        {
            await inApp.NotifyAsync(eventId, new NotificationDraft(userId, eventKey, title, facts.Ticket.Subject,
                TicketLinks.Agent(facts.Ticket.Number), facts.Ticket.DisplayId, facts.Ticket.Id), ct);
        }
        if (choice.Email && !string.IsNullOrWhiteSpace(user.Email))
        {
            var result = await mailer.SendAsync(new TicketEmailRequest
            {
                EventId = eventId,
                TemplateKey = eventKey,
                Facts = facts,
                To = [new EmailRecipient(user.Email!, user.DisplayName, user.Id)],
                Message = message,
                ActorName = actorName,
                Footer = $"Reference {facts.Ticket.DisplayId}. Reply to this email to add a private note. Change which emails you get on your profile.",
            }, ct);
            if (result.Outcome == MailOutcome.Failed)
            {
                throw new InvalidOperationException($"Notification {eventKey} to an agent wasn't sent: {result.Error}");
            }
        }
    }
}
