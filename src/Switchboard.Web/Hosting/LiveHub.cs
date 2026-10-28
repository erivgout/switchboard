using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Web.Hosting;

/// <summary>Who is looking at which ticket, and who is composing a reply (collision detection, FR-TKT-9).</summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, Viewer>> _tickets = new();

    public sealed record Viewer(Guid UserId, string Name, bool Composing);

    public IReadOnlyList<Viewer> Join(Guid ticketId, string connectionId, Guid userId, string name)
    {
        var viewers = _tickets.GetOrAdd(ticketId, _ => new());
        viewers[connectionId] = new Viewer(userId, name, false);
        return Snapshot(ticketId);
    }

    public IReadOnlyList<Viewer> Leave(Guid ticketId, string connectionId)
    {
        if (_tickets.TryGetValue(ticketId, out var viewers))
        {
            viewers.TryRemove(connectionId, out _);
        }
        return Snapshot(ticketId);
    }

    public IReadOnlyList<Viewer> SetComposing(Guid ticketId, string connectionId, bool composing)
    {
        if (_tickets.TryGetValue(ticketId, out var viewers) && viewers.TryGetValue(connectionId, out var v))
        {
            viewers[connectionId] = v with { Composing = composing };
        }
        return Snapshot(ticketId);
    }

    public IReadOnlyList<Guid> LeaveAll(string connectionId)
    {
        var left = new List<Guid>();
        foreach (var (ticketId, viewers) in _tickets)
        {
            if (viewers.TryRemove(connectionId, out _))
            {
                left.Add(ticketId);
            }
        }
        return left;
    }

    public IReadOnlyList<Viewer> Snapshot(Guid ticketId) =>
        _tickets.TryGetValue(ticketId, out var viewers)
            ? viewers.Values.GroupBy(v => v.UserId).Select(g => g.First() with { Composing = g.Any(x => x.Composing) }).ToList()
            : [];
}

/// <summary>
/// The real-time hub at <c>/hubs/live</c>. Clients receive <c>message(type, payload)</c>.
/// Groups: <c>user:{id}</c>, <c>ticket:{id}</c>, <c>ws:{id}:agents</c>.
/// </summary>
[Authorize]
public sealed class LiveHub(IServiceScopeFactory scopes, PresenceTracker presence) : Hub
{
    public static string UserGroup(Guid userId) => $"user:{userId}";

    /// <summary>Agents watching a ticket. Receives internal payloads and presence.</summary>
    public static string TicketGroup(Guid ticketId) => $"ticket:{ticketId}";

    /// <summary>Requesters watching a ticket in the portal. Receives requester-safe payloads only (SR-AUTHZ-7).</summary>
    public static string TicketRequestersGroup(Guid ticketId) => $"ticket:{ticketId}:public";
    public static string WorkspaceAgentsGroup(Guid workspaceId) => $"ws:{workspaceId}:agents";

    public override async Task OnConnectedAsync()
    {
        var actor = ActorAccessor.FromPrincipal(Context.User!, "/hubs/live");
        if (actor.UserId is not { } userId)
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));

        await using var scope = scopes.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<IActorAccessor>().BeginScope(actor);
        var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionService>().CurrentAsync();
        if (permissions?.IsAgent == true)
        {
            var workspaces = permissions.IsOrgAdmin
                ? await scope.ServiceProvider.GetRequiredService<Core.Data.SwitchboardDbContext>().Workspaces.Select(w => w.Id).ToListAsync()
                : permissions.WorkspacesWith(Privileges.TicketsView);
            foreach (var ws in workspaces)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, WorkspaceAgentsGroup(ws));
            }
        }
        await base.OnConnectedAsync();
    }

    public async Task WatchTicket(Guid ticketId)
    {
        var actor = ActorAccessor.FromPrincipal(Context.User!, "/hubs/live");
        await using var scope = scopes.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<IActorAccessor>().BeginScope(actor);
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        if (await (await permissions.AgentVisibleTicketsAsync()).AnyAsync(t => t.Id == ticketId))
        {
            // Agents get internal updates and take part in presence (collision detection).
            await Groups.AddToGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
            var viewers = presence.Join(ticketId, Context.ConnectionId, actor.UserId!.Value, actor.DisplayName);
            await BroadcastPresence(ticketId, viewers);
            return;
        }
        if (await (await permissions.RequesterVisibleTicketsAsync()).AnyAsync(t => t.Id == ticketId))
        {
            // Requesters get public updates only, and never see who of the staff is looking.
            await Groups.AddToGroupAsync(Context.ConnectionId, TicketRequestersGroup(ticketId));
            return;
        }
        throw new HubException("That ticket doesn't exist or you can't see it.");
    }

    public async Task UnwatchTicket(Guid ticketId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TicketRequestersGroup(ticketId));
        await BroadcastPresence(ticketId, presence.Leave(ticketId, Context.ConnectionId));
    }

    public Task SetComposing(Guid ticketId, bool composing) =>
        BroadcastPresence(ticketId, presence.SetComposing(ticketId, Context.ConnectionId, composing));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var ticketId in presence.LeaveAll(Context.ConnectionId))
        {
            await BroadcastPresence(ticketId, presence.Snapshot(ticketId));
        }
        await base.OnDisconnectedAsync(exception);
    }

    private Task BroadcastPresence(Guid ticketId, IReadOnlyList<PresenceTracker.Viewer> viewers) =>
        Clients.Group(TicketGroup(ticketId)).SendAsync("message", "ticket.presence", new
        {
            ticketId,
            viewers = viewers.Select(v => new { v.UserId, v.Name, v.Composing }),
        });
}

public sealed class SignalRLiveNotifier(IHubContext<LiveHub> hub) : ILiveNotifier
{
    public Task ToUserAsync(Guid userId, string type, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(LiveHub.UserGroup(userId)).SendAsync("message", type, payload, ct);

    public Task ToTicketAsync(Guid ticketId, string type, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(LiveHub.TicketGroup(ticketId)).SendAsync("message", type, payload, ct);

    public Task ToTicketRequestersAsync(Guid ticketId, string type, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(LiveHub.TicketRequestersGroup(ticketId)).SendAsync("message", type, payload, ct);

    public Task ToWorkspaceAgentsAsync(Guid workspaceId, string type, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(LiveHub.WorkspaceAgentsGroup(workspaceId)).SendAsync("message", type, payload, ct);
}
