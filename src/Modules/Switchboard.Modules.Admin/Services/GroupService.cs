using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

public sealed record GroupRow(Guid Id, string Name, string? Description, AssignmentMode Mode, int Members, int Observers, int OpenTickets);

public sealed record MemberInput(Guid UserId, GroupRole Role);

public sealed record GroupInput(
    string Name,
    string? Description,
    AssignmentMode Mode,
    int? MaxOpenPerAgent,
    int? EscalationMinutes,
    Guid? EscalateToUserId,
    IReadOnlyList<MemberInput>? Members);

public sealed record GroupMember(Guid UserId, string Name, string? Email, bool IsObserver, bool IsActive);

public sealed record RequesterGroupRow(Guid Id, string Name, string? Description, int Members);

/// <summary>Agent groups (FR-IAM-8) and manual requester groups (FR-IAM-7).</summary>
public sealed class GroupService(SwitchboardDbContext db, AdminAccess access, IAuditLog audit)
{
    /// <summary>Assignment modes offered now; skill-based routing comes with the routing module.</summary>
    public static readonly IReadOnlyList<AssignmentMode> Modes = [AssignmentMode.None, AssignmentMode.RoundRobin, AssignmentMode.LoadBased];

    // ---------------------------------------------------------------- agent groups

    public async Task<IReadOnlyList<GroupRow>> ListAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, workspaceId, ct);
        var groups = await db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == workspaceId).OrderBy(g => g.Name)
            .Select(g => new
            {
                g.Id, g.Name, g.Description, g.AssignmentMode,
                Members = g.Members.Count(m => !m.IsObserver),
                Observers = g.Members.Count(m => m.IsObserver),
            })
            .ToListAsync(ct);
        var open = await db.Tickets.Where(t => t.WorkspaceId == workspaceId && t.GroupId != null && t.DeletedAt == null && t.MergedIntoId == null
                && (t.Status!.Category == StatusCategory.Open || t.Status.Category == StatusCategory.Pending))
            .GroupBy(t => t.GroupId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return groups.Select(g => new GroupRow(g.Id, g.Name, g.Description, g.AssignmentMode, g.Members, g.Observers, open.GetValueOrDefault(g.Id))).ToList();
    }

    public async Task<(AgentGroup Group, IReadOnlyList<GroupMember> Members, int Tickets)> GetAsync(Guid id, CancellationToken ct = default)
    {
        var group = await db.AgentGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.GroupsManage, group.WorkspaceId, ct);
        var members = await db.AgentGroupMembers.AsNoTracking().Where(m => m.GroupId == id).OrderBy(m => m.User!.DisplayName)
            .Select(m => new GroupMember(m.UserId, m.User!.DisplayName, m.User.Email, m.IsObserver, m.User.IsActive))
            .ToListAsync(ct);
        var tickets = await db.Tickets.CountAsync(t => t.GroupId == id, ct);
        return (group, members, tickets);
    }

    /// <summary>People who can join a group in this workspace: active agents with a role there, and organization admins.</summary>
    public async Task<IReadOnlyList<PersonOption>> CandidatesAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, workspaceId, ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.IsOrgAdmin || (u.IsAgent && u.Memberships.Any(m => m.WorkspaceId == workspaceId))))
            .OrderBy(u => u.DisplayName)
            .Select(u => new PersonOption(u.Id, u.DisplayName, u.Email, true))
            .ToListAsync(ct);
    }

    public async Task<AgentGroup> CreateAsync(Guid workspaceId, GroupInput input, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, workspaceId, ct);
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId && !w.IsArchived, ct))
        {
            throw new NotFoundException();
        }
        var group = new AgentGroup { WorkspaceId = workspaceId };
        await ApplyAsync(group, input, [], ct);
        db.AgentGroups.Add(group);
        audit.Record("groups.created", "agent_group", group.Id.ToString(), new { name = group.Name, mode = group.AssignmentMode, members = input.Members?.Count(m => m.Role != GroupRole.None) ?? 0 });
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task UpdateAsync(Guid id, GroupInput input, CancellationToken ct = default)
    {
        var group = await db.AgentGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.GroupsManage, group.WorkspaceId, ct);
        var changes = await ApplyAsync(group, input, group.Members, ct);
        if (changes.Count == 0)
        {
            return;
        }
        audit.Record("groups.updated", "agent_group", id.ToString(), changes);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<string, object?>> ApplyAsync(AgentGroup group, GroupInput input, List<AgentGroupMember> current, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            errors["Name"] = [name.Length == 0 ? "Name the group." : "Keep the name under 200 characters."];
        }
        else if (await db.AgentGroups.AnyAsync(g => g.WorkspaceId == group.WorkspaceId && g.Name == name && g.Id != group.Id, ct))
        {
            errors["Name"] = [$"A group called {name} already exists in this workspace."];
        }
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description is { Length: > 1000 })
        {
            errors["Description"] = ["Keep the description under 1,000 characters."];
        }
        if (!Modes.Contains(input.Mode))
        {
            errors["Mode"] = ["Choose how new tickets are assigned."];
        }
        if (input.MaxOpenPerAgent is { } max && max is < 1 or > 1000)
        {
            errors["MaxOpenPerAgent"] = ["Use a number from 1 to 1,000, or leave it empty for no limit."];
        }
        if (input.EscalationMinutes is { } minutes && minutes is < 1 or > 10080)
        {
            errors["EscalationMinutes"] = ["Use a number of minutes from 1 to 10,080 (one week), or leave it empty."];
        }
        if (input.EscalationMinutes is not null && input.EscalateToUserId is null)
        {
            errors["EscalateToUserId"] = ["Choose who hears about unassigned tickets."];
        }
        if (input.EscalateToUserId is { } target && !await db.Users.AnyAsync(u => u.Id == target && u.IsActive && (u.IsAgent || u.IsOrgAdmin), ct))
        {
            errors["EscalateToUserId"] = ["Choose an active agent."];
        }

        var desired = (input.Members ?? []).Where(m => m.Role != GroupRole.None).DistinctBy(m => m.UserId).ToList();
        if (input.Members is not null && desired.Count > 0)
        {
            var ids = desired.Select(m => m.UserId).ToList();
            var eligible = await db.Users.Where(u => ids.Contains(u.Id) && u.IsActive
                    && (u.IsOrgAdmin || (u.IsAgent && u.Memberships.Any(m => m.WorkspaceId == group.WorkspaceId))))
                .Select(u => u.Id).ToListAsync(ct);
            // Existing members stay valid even if they were deactivated since; only additions must be eligible.
            if (ids.Except(eligible).Except(current.Select(c => c.UserId)).Any())
            {
                errors["Members"] = ["Members must be active agents with a role in this workspace."];
            }
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var changes = new Dictionary<string, object?>();
        if (group.Name != name)
        {
            changes["name"] = new { from = group.Name, to = name };
            group.Name = name;
        }
        if (group.Description != description)
        {
            changes["description"] = true;
            group.Description = description;
        }
        if (group.AssignmentMode != input.Mode)
        {
            changes["mode"] = new { from = group.AssignmentMode, to = input.Mode };
            group.AssignmentMode = input.Mode;
        }
        if (group.MaxOpenPerAgent != input.MaxOpenPerAgent)
        {
            changes["maxOpenPerAgent"] = input.MaxOpenPerAgent;
            group.MaxOpenPerAgent = input.MaxOpenPerAgent;
        }
        if (group.UnassignedEscalationMinutes != input.EscalationMinutes || group.EscalateToUserId != input.EscalateToUserId)
        {
            changes["escalation"] = new { minutes = input.EscalationMinutes, userId = input.EscalateToUserId };
            group.UnassignedEscalationMinutes = input.EscalationMinutes;
            group.EscalateToUserId = input.EscalationMinutes is null ? null : input.EscalateToUserId;
        }

        if (input.Members is not null)
        {
            var added = 0;
            var removed = 0;
            var switched = 0;
            foreach (var m in current.Where(c => desired.All(d => d.UserId != c.UserId)).ToList())
            {
                group.Members.Remove(m);
                db.AgentGroupMembers.Remove(m);
                removed++;
            }
            foreach (var d in desired)
            {
                var existing = current.FirstOrDefault(c => c.UserId == d.UserId);
                if (existing is null)
                {
                    group.Members.Add(new AgentGroupMember { GroupId = group.Id, UserId = d.UserId, IsObserver = d.Role == GroupRole.Observer });
                    added++;
                }
                else if (existing.IsObserver != (d.Role == GroupRole.Observer))
                {
                    existing.IsObserver = d.Role == GroupRole.Observer;
                    switched++;
                }
            }
            if (added + removed + switched > 0)
            {
                changes["members"] = new { added, removed, switched };
            }
        }
        return changes;
    }

    /// <summary>Tickets and tasks of the group move to <paramref name="replacementId"/>, or lose their group (SR-DEL-2).</summary>
    public async Task<int> DeleteAsync(Guid id, Guid? replacementId, CancellationToken ct = default)
    {
        var group = await db.AgentGroups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Privileges.GroupsManage, group.WorkspaceId, ct);
        AgentGroup? replacement = null;
        if (replacementId is { } rid)
        {
            replacement = await db.AgentGroups.FirstOrDefaultAsync(g => g.Id == rid && g.Id != id && g.WorkspaceId == group.WorkspaceId, ct)
                ?? throw new ValidationException("ReplacementId", "Choose a group in the same workspace.");
        }

        return await db.InTransactionAsync(async () =>
        {
            var moved = await TicketRemap.ApplyAsync(db, t => t.GroupId == id,
                t =>
                {
                    t.GroupId = replacement?.Id;
                    return [new FieldChange("Group", group.Name, replacement?.Name)];
                },
                replacement is null ? $"Removed from group {group.Name}, which was deleted" : $"Moved from group {group.Name} to {replacement.Name}", ct,
                t => [new TicketAssigned(t.Id, t.WorkspaceId, id, replacement?.Id, t.AgentId, t.AgentId)]);
            foreach (var task in await db.TicketTasks.Where(t => t.GroupId == id).ToListAsync(ct))
            {
                task.GroupId = replacement?.Id;
                db.Activity(task, "updated", replacement is null ? $"Task removed from group {group.Name}" : $"Task moved to group {replacement.Name}",
                    [new FieldChange("Group", group.Name, replacement?.Name)]);
            }
            db.AgentGroups.Remove(group);
            audit.Record("groups.deleted", "agent_group", id.ToString(), new { name = group.Name, moved, replacementId });
            await db.SaveChangesAsync(ct);
            return moved;
        }, ct);
    }

    // ---------------------------------------------------------------- requester groups

    public async Task<IReadOnlyList<RequesterGroupRow>> ListRequesterGroupsAsync(CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, ct: ct);
        return await db.RequesterGroups.AsNoTracking().OrderBy(g => g.Name)
            .Select(g => new RequesterGroupRow(g.Id, g.Name, g.Description, g.Members.Count))
            .ToListAsync(ct);
    }

    public async Task<(RequesterGroup Group, IReadOnlyList<GroupMember> Members)> GetRequesterGroupAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, ct: ct);
        var group = await db.RequesterGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException();
        var members = await (from m in db.RequesterGroupMembers.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             where m.GroupId == id
                             orderby u.DisplayName
                             select new GroupMember(u.Id, u.DisplayName, u.Email, false, u.IsActive)).ToListAsync(ct);
        return (group, members);
    }

    public async Task<RequesterGroup> SaveRequesterGroupAsync(Guid? id, string name, string? description, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, ct: ct);
        var clean = (name ?? "").Trim();
        if (clean.Length is 0 or > 200)
        {
            throw new ValidationException("Name", clean.Length == 0 ? "Name the group." : "Keep the name under 200 characters.");
        }
        if (await db.RequesterGroups.AnyAsync(g => g.Name == clean && g.Id != id, ct))
        {
            throw new ValidationException("Name", $"A requester group called {clean} already exists.");
        }
        var desc = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (desc is { Length: > 1000 })
        {
            throw new ValidationException("Description", "Keep the description under 1,000 characters.");
        }
        RequesterGroup group;
        if (id is { } existing)
        {
            group = await db.RequesterGroups.FirstOrDefaultAsync(g => g.Id == existing, ct) ?? throw new NotFoundException();
            if (group.Name == clean && group.Description == desc)
            {
                return group;
            }
            audit.Record("requester_groups.updated", "requester_group", group.Id.ToString(), new { name = clean });
        }
        else
        {
            group = new RequesterGroup();
            db.RequesterGroups.Add(group);
            audit.Record("requester_groups.created", "requester_group", group.Id.ToString(), new { name = clean });
        }
        group.Name = clean;
        group.Description = desc;
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<int> AddRequesterMembersAsync(Guid id, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, ct: ct);
        if (!await db.RequesterGroups.AnyAsync(g => g.Id == id, ct))
        {
            throw new NotFoundException();
        }
        var existing = await db.RequesterGroupMembers.Where(m => m.GroupId == id).Select(m => m.UserId).ToListAsync(ct);
        var valid = await db.Users.Where(u => userIds.Contains(u.Id) && u.IsActive && !u.IsForgotten).Select(u => u.Id).ToListAsync(ct);
        var added = valid.Except(existing).ToList();
        if (added.Count == 0)
        {
            return 0;
        }
        foreach (var userId in added)
        {
            db.RequesterGroupMembers.Add(new RequesterGroupMember { GroupId = id, UserId = userId });
        }
        audit.Record("requester_groups.members_added", "requester_group", id.ToString(), new { users = added });
        await db.SaveChangesAsync(ct);
        return added.Count;
    }

    public async Task RemoveRequesterMemberAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, ct: ct);
        var member = await db.RequesterGroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId, ct);
        if (member is null)
        {
            return;
        }
        db.RequesterGroupMembers.Remove(member);
        audit.Record("requester_groups.member_removed", "requester_group", id.ToString(), new { userId });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRequesterGroupAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.GroupsManage, ct: ct);
        var group = await db.RequesterGroups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException();
        db.RequesterGroups.Remove(group);
        audit.Record("requester_groups.deleted", "requester_group", id.ToString(), new { name = group.Name });
        await db.SaveChangesAsync(ct);
    }
}
