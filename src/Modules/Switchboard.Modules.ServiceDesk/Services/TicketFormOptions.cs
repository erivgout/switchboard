using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.ServiceDesk.Services;

public sealed record NamedOption(Guid Id, string Name);

public sealed record StatusOption(Guid Id, string Name, StatusCategory Category, int Order);

public sealed record SourceOption(Guid Id, string Key, string Name, string Icon);

public sealed record CategoryNode(Guid Id, Guid? ParentId, string Name, int Level);

/// <summary>An agent group with the agents who work in it (observers excluded).</summary>
public sealed record GroupOption(Guid Id, string Name, IReadOnlyList<Guid> MemberIds);

public sealed record PersonOption(Guid Id, string Name, string? Email, string? Department, string? JobTitle, bool IsVip, bool IsAgent);

/// <summary>Choices for ticket fields in one workspace.</summary>
public sealed class WorkspaceOptions
{
    public required Workspace Workspace { get; init; }
    public required IReadOnlyList<StatusOption> Statuses { get; init; }
    public required IReadOnlyList<SourceOption> Sources { get; init; }
    public required IReadOnlyList<GroupOption> Groups { get; init; }
    /// <summary>Active agents who can be assigned in this workspace (INV-TKT-3).</summary>
    public required IReadOnlyList<NamedOption> Agents { get; init; }
    public required IReadOnlyList<CategoryNode> Categories { get; init; }
    public required IReadOnlyList<NamedOption> Departments { get; init; }
    public required IReadOnlyList<CustomFieldDefinition> CustomFields { get; init; }

    public IEnumerable<CategoryNode> ChildrenOf(Guid? parentId, int level) =>
        Categories.Where(c => c.Level == level && c.ParentId == parentId);

    /// <summary>Agents to offer for a group: its members, or everyone when no group is set.</summary>
    public IReadOnlyList<NamedOption> AgentsFor(Guid? groupId, Guid? alwaysInclude = null)
    {
        if (groupId is not { } g || Groups.FirstOrDefault(x => x.Id == g) is not { } group)
        {
            return Agents;
        }
        return Agents.Where(a => group.MemberIds.Contains(a.Id) || a.Id == alwaysInclude).ToList();
    }
}

/// <summary>Loads ticket field choices and searches people for the requester picker.</summary>
public sealed class TicketFormOptions(SwitchboardDbContext db, ICustomFieldService customFields, IPermissionService permissions)
{
    public async Task<WorkspaceOptions> LoadAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == workspaceId, ct);
        var statuses = await db.TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == workspaceId)
            .OrderBy(s => s.Order).Select(s => new StatusOption(s.Id, s.Name, s.Category, s.Order)).ToListAsync(ct);
        var sources = await db.TicketSources.AsNoTracking().OrderBy(s => s.Order)
            .Select(s => new SourceOption(s.Id, s.Key, s.Name, s.Icon)).ToListAsync(ct);
        var groups = await db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == workspaceId).OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name, Members = g.Members.Where(m => !m.IsObserver).Select(m => m.UserId).ToList() })
            .ToListAsync(ct);
        var agents = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.IsOrgAdmin || u.Memberships.Any(m => m.WorkspaceId == workspaceId)))
            .OrderBy(u => u.DisplayName).Select(u => new NamedOption(u.Id, u.DisplayName)).ToListAsync(ct);
        var categories = await db.TicketCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId)
            .OrderBy(c => c.Level).ThenBy(c => c.Order).ThenBy(c => c.Name)
            .Select(c => new CategoryNode(c.Id, c.ParentId, c.Name, c.Level)).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new NamedOption(d.Id, d.Name)).ToListAsync(ct);
        var fields = await customFields.GetDefinitionsAsync("ticket", workspaceId, ct);

        return new WorkspaceOptions
        {
            Workspace = workspace,
            Statuses = statuses,
            Sources = sources,
            Groups = groups.Select(g => new GroupOption(g.Id, g.Name, g.Members)).ToList(),
            Agents = agents,
            Categories = categories,
            Departments = departments,
            CustomFields = fields,
        };
    }

    /// <summary>Workspaces where the current user can create tickets.</summary>
    public async Task<IReadOnlyList<NamedOption>> CreatableWorkspacesAsync(CancellationToken ct = default)
    {
        var p = await permissions.CurrentAsync(ct);
        if (p is null)
        {
            return [];
        }
        var all = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived)
            .OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name)
            .Select(w => new NamedOption(w.Id, w.Name)).ToListAsync(ct);
        return all.Where(w => p.Has(Privileges.TicketsCreate, w.Id)).ToList();
    }

    /// <summary>People for the requester picker. Needs users.view or tickets.create.</summary>
    public async Task<IReadOnlyList<PersonOption>> SearchPeopleAsync(string? q, int take = 8, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(Privileges.UsersView, ct: ct) && !await permissions.HasAsync(Privileges.TicketsCreate, ct: ct))
        {
            throw new ForbiddenException();
        }
        var term = (q ?? "").Trim();
        if (term.Length < 2)
        {
            return [];
        }
        if (term.Length > 100)
        {
            term = term[..100];
        }
        var normalized = term.ToUpperInvariant();
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && !u.IsForgotten && (u.DisplayName.Contains(term) || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(normalized))))
            .OrderByDescending(u => u.DisplayName.StartsWith(term))
            .ThenBy(u => u.DisplayName)
            .Take(Math.Clamp(take, 1, 25))
            .Select(u => new PersonOption(u.Id, u.DisplayName, u.Email, u.Department != null ? u.Department.Name : null, u.JobTitle, u.IsVip, u.IsAgent))
            .ToListAsync(ct);
    }
}
