using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;

namespace Switchboard.Core.Security;

public sealed record WorkspaceAccess(Guid WorkspaceId, Guid RoleId, string RoleName, TicketScope Scope, IReadOnlySet<string> Privileges)
{
    /// <summary>The workspace admin privilege grants every privilege in that workspace.</summary>
    public bool Has(string privilege) =>
        Privileges.Contains(privilege) || Privileges.Contains(Security.Privileges.WorkspaceAdmin);
}

/// <summary>A user's effective permissions, loaded once per request.</summary>
public sealed class UserPermissions
{
    public required Guid UserId { get; init; }
    public required bool IsOrgAdmin { get; init; }
    public required bool IsAgent { get; init; }
    public required IReadOnlyDictionary<Guid, WorkspaceAccess> Workspaces { get; init; }
    /// <summary>Groups the user is a working member of.</summary>
    public required IReadOnlySet<Guid> GroupIds { get; init; }
    /// <summary>Groups the user observes (visibility, no assignment).</summary>
    public required IReadOnlySet<Guid> ObserverGroupIds { get; init; }
    /// <summary>Departments where the user is head or prime user (portal visibility, FR-IAM-5).</summary>
    public required IReadOnlySet<Guid> LeadDepartmentIds { get; init; }

    public bool Has(string privilege, Guid? workspaceId = null)
    {
        if (IsOrgAdmin)
        {
            return true;
        }
        return workspaceId is { } ws
            ? Workspaces.TryGetValue(ws, out var access) && access.Has(privilege)
            : Workspaces.Values.Any(w => w.Has(privilege));
    }

    public IReadOnlyList<Guid> WorkspacesWith(string privilege) =>
        Workspaces.Values.Where(w => IsOrgAdmin || w.Has(privilege)).Select(w => w.WorkspaceId).ToList();
}

/// <summary>
/// Lets token-based channels (MCP, scoped API keys) narrow privileges to the scopes a user granted.
/// Called only when <see cref="Actor.Scopes"/> is set; every guard must allow.
/// </summary>
public interface IPrivilegeScopeGuard
{
    bool Allows(Actor actor, string privilege);
}

/// <summary>The one policy engine (SR-AUTHZ-1). UI, API, MCP, automations and reports all call it.</summary>
public interface IPermissionService
{
    /// <summary>Permissions of the current actor's user, or null for anonymous/internal actors.</summary>
    Task<UserPermissions?> CurrentAsync(CancellationToken ct = default);

    Task<UserPermissions?> ForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>True for internal (system/automation) actors and for users holding the privilege.</summary>
    Task<bool> HasAsync(string privilege, Guid? workspaceId = null, CancellationToken ct = default);

    /// <summary>Throws <see cref="ForbiddenException"/> unless <see cref="HasAsync"/> is true.</summary>
    Task DemandAsync(string privilege, Guid? workspaceId = null, CancellationToken ct = default);

    /// <summary>Tickets the current actor can see as an agent (ticket scope rules).</summary>
    Task<IQueryable<Ticket>> AgentVisibleTicketsAsync(CancellationToken ct = default);

    /// <summary>Tickets the current actor can see as a requester in the portal.</summary>
    Task<IQueryable<Ticket>> RequesterVisibleTicketsAsync(CancellationToken ct = default);
}

public sealed class PermissionService(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IEnumerable<IPrivilegeScopeGuard> scopeGuards) : IPermissionService
{
    private readonly Dictionary<Guid, UserPermissions?> _cache = [];

    public async Task<UserPermissions?> CurrentAsync(CancellationToken ct = default)
    {
        var actor = actors.Current;
        return actor.UserId is { } id && !actor.IsInternal ? await ForUserAsync(id, ct) : null;
    }

    public async Task<UserPermissions?> ForUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.IsOrgAdmin, u.IsAgent, u.IsActive })
            .FirstOrDefaultAsync(ct);

        // Deactivated users hold no permissions at all (INV-ID-4).
        if (user is null || !user.IsActive)
        {
            _cache[userId] = null;
            return null;
        }

        var memberships = await db.WorkspaceMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.WorkspaceId, m.RoleId, RoleName = m.Role!.Name, m.Role.Privileges, m.TicketScope })
            .ToListAsync(ct);

        var groups = await db.AgentGroupMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.GroupId, m.IsObserver })
            .ToListAsync(ct);

        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.HeadUserId == userId || d.PrimeUserId == userId)
            .Select(d => d.Id)
            .ToListAsync(ct);

        var permissions = new UserPermissions
        {
            UserId = user.Id,
            IsOrgAdmin = user.IsOrgAdmin,
            IsAgent = user.IsAgent || user.IsOrgAdmin,
            Workspaces = memberships.ToDictionary(
                m => m.WorkspaceId,
                m => new WorkspaceAccess(m.WorkspaceId, m.RoleId, m.RoleName, m.TicketScope, m.Privileges.ToHashSet(StringComparer.Ordinal))),
            GroupIds = groups.Where(g => !g.IsObserver).Select(g => g.GroupId).ToHashSet(),
            ObserverGroupIds = groups.Where(g => g.IsObserver).Select(g => g.GroupId).ToHashSet(),
            LeadDepartmentIds = departments.ToHashSet(),
        };
        _cache[userId] = permissions;
        return permissions;
    }

    public async Task<bool> HasAsync(string privilege, Guid? workspaceId = null, CancellationToken ct = default)
    {
        var actor = actors.Current;
        if (actor.IsInternal)
        {
            return true;
        }
        if (ScopeDenied(actor, privilege))
        {
            return false;
        }
        var permissions = await CurrentAsync(ct);
        return permissions?.Has(privilege, workspaceId) == true;
    }

    public async Task DemandAsync(string privilege, Guid? workspaceId = null, CancellationToken ct = default)
    {
        if (!await HasAsync(privilege, workspaceId, ct))
        {
            throw new ForbiddenException();
        }
    }

    public async Task<IQueryable<Ticket>> AgentVisibleTicketsAsync(CancellationToken ct = default)
    {
        var actor = actors.Current;
        if (actor.IsInternal)
        {
            return db.Tickets;
        }
        if (ScopeDenied(actor, Privileges.TicketsView))
        {
            return db.Tickets.Where(_ => false);
        }
        var p = await CurrentAsync(ct);
        return p is null ? db.Tickets.Where(_ => false) : ApplyAgentVisibility(db.Tickets, p);
    }

    public async Task<IQueryable<Ticket>> RequesterVisibleTicketsAsync(CancellationToken ct = default)
    {
        var actor = actors.Current;
        // Scoped tokens (API keys, MCP grants) must carry a ticket-read scope even for a requester's own tickets.
        if (ScopeDenied(actor, Privileges.TicketsView))
        {
            return db.Tickets.Where(_ => false);
        }
        var p = await CurrentAsync(ct);
        if (p is null || actor.IsInternal)
        {
            return db.Tickets.Where(_ => false);
        }
        return ApplyRequesterVisibility(db.Tickets, p);
    }

    /// <summary>
    /// True when a token's scopes don't cover <paramref name="privilege"/>. An MCP call must always carry
    /// scopes; one without is denied rather than treated as unrestricted (defence in depth, MCP-16).
    /// </summary>
    private bool ScopeDenied(Actor actor, string privilege) =>
        (actor.Channel == Channel.Mcp && actor.Scopes is null)
        || (actor.Scopes is not null && scopeGuards.Any(g => !g.Allows(actor, privilege)));

    /// <summary>Ticket scope rules (FR-IAM-11) as a translatable query filter.</summary>
    public static IQueryable<Ticket> ApplyAgentVisibility(IQueryable<Ticket> query, UserPermissions p)
    {
        if (p.IsOrgAdmin)
        {
            return query;
        }

        var visible = p.Workspaces.Values.Where(w => w.Has(Privileges.TicketsView)).ToList();
        var global = visible.Where(w => w.Scope == TicketScope.Global).Select(w => w.WorkspaceId).ToList();
        var grouped = visible.Where(w => w.Scope == TicketScope.Group).Select(w => w.WorkspaceId).ToList();
        var restricted = visible.Where(w => w.Scope == TicketScope.Restricted).Select(w => w.WorkspaceId).ToList();
        var groups = p.GroupIds.Concat(p.ObserverGroupIds).ToList();
        var uid = p.UserId;

        return query.Where(t =>
            global.Contains(t.WorkspaceId)
            || (grouped.Contains(t.WorkspaceId) && ((t.GroupId != null && groups.Contains(t.GroupId.Value)) || t.AgentId == uid))
            || (restricted.Contains(t.WorkspaceId) && t.AgentId == uid));
    }

    /// <summary>Requester portal visibility: own, requested-for, shared, or led department. Never spam/trash.</summary>
    public static IQueryable<Ticket> ApplyRequesterVisibility(IQueryable<Ticket> query, UserPermissions p)
    {
        var uid = p.UserId;
        var departments = p.LeadDepartmentIds.ToList();
        return query.Where(t =>
            t.DeletedAt == null && !t.IsSpam && t.PurgedAt == null
            && (t.RequesterId == uid
                || t.RequestedForId == uid
                || t.Shares.Any(s => s.UserId == uid)
                || (t.DepartmentId != null && departments.Contains(t.DepartmentId.Value))));
    }

    public static bool CanAgentSee(Ticket t, UserPermissions p)
    {
        if (p.IsOrgAdmin)
        {
            return true;
        }
        if (!p.Workspaces.TryGetValue(t.WorkspaceId, out var access) || !access.Has(Privileges.TicketsView))
        {
            return false;
        }
        return access.Scope switch
        {
            TicketScope.Global => true,
            TicketScope.Group => t.AgentId == p.UserId
                || (t.GroupId is { } g && (p.GroupIds.Contains(g) || p.ObserverGroupIds.Contains(g))),
            _ => t.AgentId == p.UserId,
        };
    }
}
