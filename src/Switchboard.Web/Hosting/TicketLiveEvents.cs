using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Services;

namespace Switchboard.Web.Hosting;

/// <summary>
/// Pushes ticket changes to open browsers so queues and ticket pages stay current. Agents get the full
/// payload; requesters watching in the portal get only that something public changed (SR-AUTHZ-7).
/// </summary>
public sealed class TicketLiveEvents(ILiveNotifier live) :
    IEventHandler<TicketCreated>,
    IEventHandler<TicketUpdated>,
    IEventHandler<TicketConversationAdded>
{
    public Task HandleAsync(TicketCreated e, CancellationToken ct) =>
        live.ToWorkspaceAgentsAsync(e.WorkspaceId, "ticket.created", new { e.TicketId }, ct);

    public async Task HandleAsync(TicketUpdated e, CancellationToken ct)
    {
        var payload = new { e.TicketId, changes = e.Changes.Select(c => c.Field), by = e.Actor?.DisplayName, byUserId = e.Actor?.UserId };
        await live.ToTicketAsync(e.TicketId, "ticket.updated", payload, ct);
        await live.ToWorkspaceAgentsAsync(e.WorkspaceId, "ticket.updated", payload, ct);
        await live.ToTicketRequestersAsync(e.TicketId, "ticket.updated", new { e.TicketId }, ct);
    }

    public async Task HandleAsync(TicketConversationAdded e, CancellationToken ct)
    {
        await live.ToTicketAsync(e.TicketId, "ticket.conversation", new
        {
            e.TicketId,
            e.ConversationId,
            kind = e.Kind.ToString(),
            e.IsPrivate,
            by = e.Actor?.DisplayName,
            byUserId = e.Actor?.UserId,
        }, ct);

        // Requesters only hear about content they can see, and never who wrote it.
        if (!e.IsPrivate && e.Kind is not ConversationKind.Forward)
        {
            await live.ToTicketRequestersAsync(e.TicketId, "ticket.conversation", new
            {
                e.TicketId,
                e.ConversationId,
                kind = e.Kind.ToString(),
                isPrivate = false,
            }, ct);
        }
    }
}
