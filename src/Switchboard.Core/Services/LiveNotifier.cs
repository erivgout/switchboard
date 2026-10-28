namespace Switchboard.Core.Services;

/// <summary>
/// Pushes real-time messages to browsers (SignalR in the web host). Message <c>type</c> values
/// use <c>area.event</c> naming, e.g. <c>ticket.updated</c>, <c>notification.created</c>.
/// </summary>
public interface ILiveNotifier
{
    Task ToUserAsync(Guid userId, string type, object payload, CancellationToken ct = default);

    /// <summary>
    /// Agents currently viewing the ticket (collision detection, live timeline). Payloads may contain
    /// internal data: requesters watching the same ticket do not receive these messages.
    /// </summary>
    Task ToTicketAsync(Guid ticketId, string type, object payload, CancellationToken ct = default);

    /// <summary>
    /// Requesters watching the ticket in the portal. Send only requester-safe payloads: no private notes,
    /// internal fields, agent names or presence (SR-AUTHZ-7).
    /// </summary>
    Task ToTicketRequestersAsync(Guid ticketId, string type, object payload, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>All agents of a workspace (queue updates, lamps).</summary>
    Task ToWorkspaceAgentsAsync(Guid workspaceId, string type, object payload, CancellationToken ct = default);
}

public sealed class NullLiveNotifier : ILiveNotifier
{
    public Task ToUserAsync(Guid userId, string type, object payload, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToTicketAsync(Guid ticketId, string type, object payload, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToWorkspaceAgentsAsync(Guid workspaceId, string type, object payload, CancellationToken ct = default) => Task.CompletedTask;
}
