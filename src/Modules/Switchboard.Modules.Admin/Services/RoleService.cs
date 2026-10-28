using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

public sealed record RoleRow(Guid Id, string Name, string? Description, bool IsSystem, int PrivilegeCount, int People);

public sealed record RoleUser(Guid UserId, string Name, string Workspace, TicketScope Scope, bool IsActive);

public sealed record RoleInput(string Name, string? Description, IReadOnlyCollection<string> Privileges);

/// <summary>Agent roles (FR-IAM-10) with the no-escalation rules of SR-AUTHZ-3.</summary>
public sealed class RoleService(
    SwitchboardDbContext db,
    AdminAccess access,
    IEnumerable<PrivilegeDefinition> definitions,
    IAuditLog audit)
{
    /// <summary>Every privilege registered by Core and the loaded modules, once each.</summary>
    public IReadOnlyList<PrivilegeDefinition> Definitions { get; } = definitions.DistinctBy(d => d.Key).ToList();

    /// <summary>Definitions grouped by <see cref="PrivilegeDefinition.Group"/>, Core groups first.</summary>
    public IReadOnlyList<IGrouping<string, PrivilegeDefinition>> Grouped =>
        Definitions.GroupBy(d => d.Group).OrderBy(g => g.Key switch { "Tickets" => 0, "Users and access" => 1, "Administration" => 2, _ => 3 }).ThenBy(g => g.Key).ToList();

    public async Task<IReadOnlyList<RoleRow>> ListAsync(CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.RolesManage, ct: ct);
        var roles = await db.Roles.AsNoTracking().OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name).ToListAsync(ct);
        var usage = await db.WorkspaceMemberships.AsNoTracking().GroupBy(m => m.RoleId)
            .Select(g => new { g.Key, People = g.Select(m => m.UserId).Distinct().Count() })
            .ToDictionaryAsync(g => g.Key, g => g.People, ct);
        return roles.Select(r => new RoleRow(r.Id, r.Name, r.Description, r.IsSystem, r.Privileges.Count, usage.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<Role> GetAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.RolesManage, ct: ct);
        return await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException();
    }

    /// <summary>Who has this role, in which workspace (SR-DEL-2 shows this before deletion).</summary>
    public async Task<IReadOnlyList<RoleUser>> UsersAsync(Guid roleId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.RolesManage, ct: ct);
        return await db.WorkspaceMemberships.AsNoTracking().Where(m => m.RoleId == roleId)
            .OrderBy(m => m.User!.DisplayName)
            .Select(m => new RoleUser(m.UserId, m.User!.DisplayName, m.Workspace!.Name, m.TicketScope, m.User.IsActive))
            .ToListAsync(ct);
    }

    /// <summary>Privileges the acting user may add to or remove from <paramref name="roleId"/> (all of them for a new role they could grant).</summary>
    public async Task<IReadOnlySet<string>> GrantableAsync(Guid? roleId, CancellationToken ct = default)
    {
        var workspaces = roleId is { } id ? await WorkspacesUsingAsync(id, ct) : [];
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Definitions)
        {
            if (await access.HoldsAsync(d.Key, workspaces, ct))
            {
                result.Add(d.Key);
            }
        }
        return result;
    }

    /// <summary>True when the acting user holds this role somewhere and so may not edit it.</summary>
    public async Task<bool> IsOwnRoleAsync(Guid roleId, CancellationToken ct = default) =>
        access.ActorUserId is { } me && !await access.IsOrgAdminAsync(ct)
        && await db.WorkspaceMemberships.AnyAsync(m => m.RoleId == roleId && m.UserId == me, ct);

    public async Task<Role> CreateAsync(RoleInput input, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.RolesManage, ct: ct);
        var name = await ValidateNameAsync(input.Name, null, ct);
        var privileges = Normalize(input.Privileges);
        await EnsureCanChangeAsync(privileges, [], ct);
        var role = new Role { Name = name, Description = Description(input.Description), Privileges = [.. privileges] };
        db.Roles.Add(role);
        audit.Record("roles.created", "role", role.Id.ToString(), new { name, privileges });
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task UpdateAsync(Guid id, RoleInput input, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.RolesManage, ct: ct);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException();
        if (await IsOwnRoleAsync(id, ct))
        {
            throw new ForbiddenException("You can't change a role you have. Ask another admin to do it.");
        }

        var privileges = Normalize(input.Privileges);
        var added = privileges.Except(role.Privileges).ToList();
        var removed = role.Privileges.Except(privileges).ToList();
        await EnsureCanChangeAsync(added.Concat(removed).ToList(), await WorkspacesUsingAsync(id, ct), ct);

        var changes = new Dictionary<string, object?>();
        if (!role.IsSystem)
        {
            // Built-in roles keep their names (FR-IAM-10); their privileges stay editable.
            var name = await ValidateNameAsync(input.Name, id, ct);
            var description = Description(input.Description);
            if (name != role.Name)
            {
                changes["name"] = new { from = role.Name, to = name };
                role.Name = name;
            }
            if (description != role.Description)
            {
                changes["description"] = true;
                role.Description = description;
            }
        }
        if (added.Count == 0 && removed.Count == 0 && changes.Count == 0)
        {
            return;
        }
        role.Privileges = [.. privileges];
        changes["added"] = added;
        changes["removed"] = removed;
        audit.Record("roles.updated", "role", id.ToString(), changes);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A role in use moves its people to <paramref name="replacementId"/> first (SR-DEL-2).</summary>
    public async Task DeleteAsync(Guid id, Guid? replacementId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.RolesManage, ct: ct);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException();
        if (role.IsSystem)
        {
            throw new ValidationException("Role", "Built-in roles can't be deleted.");
        }
        if (await IsOwnRoleAsync(id, ct))
        {
            throw new ForbiddenException("You can't delete a role you have. Ask another admin to do it.");
        }

        var memberships = await db.WorkspaceMemberships.Where(m => m.RoleId == id).ToListAsync(ct);
        var workspaces = memberships.Select(m => m.WorkspaceId).Distinct().ToList();
        await EnsureCanChangeAsync(role.Privileges, workspaces, ct);
        Role? replacement = null;
        if (memberships.Count > 0)
        {
            if (replacementId is not { } rid || rid == id)
            {
                throw new ValidationException("ReplacementId", $"{memberships.Count} {(memberships.Count == 1 ? "person has" : "people have")} this role. Choose the role they get instead.");
            }
            replacement = await db.Roles.FirstOrDefaultAsync(r => r.Id == rid, ct)
                ?? throw new ValidationException("ReplacementId", "That replacement role no longer exists.");
            foreach (var ws in workspaces)
            {
                await access.EnsureCanAssignRoleAsync(replacement, ws, ct);
            }
            foreach (var m in memberships)
            {
                m.RoleId = replacement.Id;
            }
        }
        db.Roles.Remove(role);
        audit.Record("roles.deleted", "role", id.ToString(), new { name = role.Name, reassigned = memberships.Count, replacement = replacement?.Name });
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureCanChangeAsync(IReadOnlyCollection<string> privileges, IReadOnlyCollection<Guid> workspaces, CancellationToken ct)
    {
        foreach (var p in privileges)
        {
            if (!await access.HoldsAsync(p, workspaces, ct))
            {
                var title = Definitions.FirstOrDefault(d => d.Key == p)?.Title ?? p;
                throw new ForbiddenException($"You can't give or remove \"{title}\" because you don't have it yourself.");
            }
        }
    }

    private async Task<List<Guid>> WorkspacesUsingAsync(Guid roleId, CancellationToken ct) =>
        await db.WorkspaceMemberships.Where(m => m.RoleId == roleId).Select(m => m.WorkspaceId).Distinct().ToListAsync(ct);

    private List<string> Normalize(IEnumerable<string>? privileges)
    {
        var known = Definitions.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var list = (privileges ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var unknown = list.FirstOrDefault(p => !known.Contains(p));
        if (unknown is not null)
        {
            throw new ValidationException("Privileges", $"\"{unknown}\" isn't a privilege this instance knows.");
        }
        return list.Order(StringComparer.Ordinal).ToList();
    }

    private async Task<string> ValidateNameAsync(string? raw, Guid? exceptId, CancellationToken ct)
    {
        var name = (raw ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("Name", name.Length == 0 ? "Name the role." : "Keep the name under 100 characters.");
        }
        if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != exceptId, ct))
        {
            throw new ValidationException("Name", $"A role called {name} already exists.");
        }
        return name;
    }

    private static string? Description(string? raw)
    {
        var d = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        return d is { Length: > 500 } ? throw new ValidationException("Description", "Keep the description under 500 characters.") : d;
    }
}
